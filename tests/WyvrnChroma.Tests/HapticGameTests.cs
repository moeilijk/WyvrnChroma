using Xunit;

namespace WyvrnChroma.Tests;

public sealed class HapticGameTests : IDisposable
{
    private const int Gold = 0x0000C8C8;
    private const int Red = 0x000000FF;

    private readonly string _root;
    private readonly string _gameDir;

    public HapticGameTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WyvrnChromaTests_" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_root, "Test Game");
        Directory.CreateDirectory(_gameDir);

        // wyvrn.config: Idle (looping keyboard), Hit (one-shot keyboard), and Ghost (mapped but file missing).
        File.WriteAllText(Path.Combine(_gameDir, "wyvrn.config"), """
        {
          "ExternalCommands": [
            { "External_Command_ID": "Idle", "Chroma_Events": [ { "Chroma_Effect": "Idle_Keyboard", "Animation": "Idle", "Loop": "infinity" } ] },
            { "External_Command_ID": "Hit", "Chroma_Events": [ { "Chroma_Effect": "Hit_Keyboard", "Animation": "Hit" } ] },
            { "External_Command_ID": "Ghost", "Chroma_Events": [ { "Chroma_Effect": "Ghost_Keyboard", "Animation": "Ghost" } ] }
          ]
        }
        """);

        WriteChroma(Path.Combine(_gameDir, "Idle_Keyboard.chroma"), Gold);
        WriteChroma(Path.Combine(_gameDir, "Hit_Keyboard.chroma"), Red);
        // Ghost_Keyboard.chroma intentionally absent (mirrors the real Reload_SMG_* gap).
    }

    private static void WriteChroma(string path, int colorRef)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);            // version
        w.Write((byte)1);      // deviceType 2D
        w.Write((byte)3);      // device
        w.Write(1);            // frameCount
        w.Write(1f / 30f);     // duration
        for (var i = 0; i < 6 * 22; i++) w.Write(colorRef);
        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Games_ListsFoldersWithAConfig()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Not A Game")); // no wyvrn.config → excluded
        var catalog = new HapticCatalog(_root);

        Assert.Equal(new[] { "Test Game" }, catalog.Games);
    }

    [Fact]
    public void GetGame_KnownAndUnknown()
    {
        var catalog = new HapticCatalog(_root);

        Assert.NotNull(catalog.GetGame("Test Game"));
        Assert.NotNull(catalog.GetGame("test game")); // case-insensitive
        Assert.Null(catalog.GetGame("No Such Game"));
    }

    [Fact]
    public void GetEffectForEvent_ResolvesEventToParsedAnimation()
    {
        var catalog = new HapticCatalog(_root);

        var idle = catalog.GetEffectForEvent("Test Game", "Idle");
        Assert.NotNull(idle);
        Assert.Equal("Idle_Keyboard", idle!.Mapping.ChromaEffect);
        Assert.True(idle.Mapping.Loops);
        Assert.Equal(132, idle.Animation.LedCount);
        // Independent property: the parsed colour matches what we wrote, not the implementation's own logic.
        Assert.Equal(new ChromaColor(0xC8, 0xC8, 0x00), idle.Animation.Frames[0].Colors[0]);

        var hit = catalog.GetEffectForEvent("Test Game", "Hit");
        Assert.NotNull(hit);
        Assert.False(hit!.Mapping.Loops);
        Assert.Equal(new ChromaColor(0xFF, 0x00, 0x00), hit.Animation.Frames[0].Colors[0]);
    }

    [Fact]
    public void CreatePlayer_UsesLoopSemantics()
    {
        var catalog = new HapticCatalog(_root);

        Assert.True(catalog.GetEffectForEvent("Test Game", "Idle")!.CreatePlayer().Loop);
        Assert.False(catalog.GetEffectForEvent("Test Game", "Hit")!.CreatePlayer().Loop);
    }

    [Fact]
    public void ResolveEvent_MissingChromaFile_ReturnsNull()
    {
        var catalog = new HapticCatalog(_root);

        // Mapped in the config but the .chroma file is absent → tolerated, null.
        Assert.Null(catalog.GetEffectForEvent("Test Game", "Ghost"));
        // But the mapping itself is still discoverable.
        Assert.NotNull(catalog.GetGame("Test Game")!.GetMapping("Ghost"));
    }

    [Fact]
    public void Resolve_UnknownEventDeviceOrGame_ReturnsNull()
    {
        var catalog = new HapticCatalog(_root);

        Assert.Null(catalog.GetEffectForEvent("Test Game", "Nope"));
        Assert.Null(catalog.GetEffectForEvent("Test Game", "Idle", ChromaDevice.Mouse)); // device not bound
        Assert.Null(catalog.GetEffectForEvent("No Such Game", "Idle"));
    }

    [Fact]
    public void HapticCatalog_EmptyRoot_Throws()
        => Assert.Throws<ArgumentException>(() => new HapticCatalog("  "));

    // The CDN catalog ships wyvrn.config with inconsistent casing (wyvrn.config / Wyvrn.config / WYVRN.config). On
    // case-sensitive file systems (Linux/WSL) a hard-coded lower-case name would miss ~15% of games, so config
    // resolution must be case-insensitive — both for discovery and for the event mapping it encodes.
    [Theory]
    [InlineData("Wyvrn.config")]
    [InlineData("WYVRN.config")]
    public void CaseInsensitiveConfigName_DiscoveredAndResolved(string configName)
    {
        var gameDir = Path.Combine(_root, "Cased Game");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, configName), """
        {
          "ExternalCommands": [
            { "External_Command_ID": "Aim_On", "Chroma_Events": [ { "Chroma_Effect": "Aim_Off15_Keyboard", "Animation": "Aim", "Loop": "infinity" } ] }
          ]
        }
        """);
        // The effect file is named independently of the event id — the convention <Event>_<Device> would not find it.
        WriteChroma(Path.Combine(gameDir, "Aim_Off15_Keyboard.chroma"), Red);

        var catalog = new HapticCatalog(_root);

        Assert.Contains("Cased Game", catalog.Games);
        Assert.NotNull(catalog.GetGame("Cased Game"));

        var aim = catalog.GetEffectForEvent("Cased Game", "Aim_On");
        Assert.NotNull(aim);
        Assert.Equal("Aim_Off15_Keyboard", aim!.Mapping.ChromaEffect); // mapping comes from the config, not the name
        Assert.True(aim.Mapping.Loops);
        Assert.Equal(new ChromaColor(0xFF, 0x00, 0x00), aim.Animation.Frames[0].Colors[0]);
    }

    // Guarded full-pipeline check against a real catalog — either a local Synapse install or the cached CDN
    // download (LocalApplicationData/WyvrnChroma/hapticFolders/<version>/hapticFolders). Skips cleanly when none is
    // present, so it proves the "no Synapse, just the CDN download" path on machines that have it.
    [Fact]
    public void RealCatalog_ResolvesEventToKeyboardAnimation_IfPresent()
    {
        var root = RealCatalogRoots().FirstOrDefault(Directory.Exists);
        if (root is null)
            return; // no local install and nothing cached on this machine

        var catalog = new HapticCatalog(root);
        var game = catalog.GetGame("007 First Light"); // ships its config as "Wyvrn.config" — exercises case-insensitive resolution
        if (game is null)
            return; // catalog present but this game is not installed

        // Aim_On resolves via the config to a differently-named keyboard effect (Aim_Off15_Keyboard) — a mapping
        // the <Event>_<Device> file-name convention could not reproduce.
        var aim = catalog.GetEffectForEvent("007 First Light", "Aim_On");
        Assert.NotNull(aim);
        Assert.Equal(ChromaDevice.Keyboard, aim!.Mapping.Device);
        Assert.Equal("Aim_Off15_Keyboard", aim.Mapping.ChromaEffect);
        Assert.True(aim.Animation.LedCount > 0);
        Assert.NotEmpty(aim.Animation.Frames);
    }

    private static IEnumerable<string> RealCatalogRoots()
    {
        yield return @"C:\Program Files (x86)\Interhaptics\hapticFolders";
        yield return "/mnt/c/Program Files (x86)/Interhaptics/hapticFolders";

        // Cached CDN download: a versioned dir, each wrapping the per-game folders in another "hapticFolders".
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WyvrnChroma", "hapticFolders");
        if (!Directory.Exists(dataDir))
            yield break;
        foreach (var versionDir in Directory.EnumerateDirectories(dataDir))
        {
            var wrapped = Path.Combine(versionDir, "hapticFolders");
            yield return Directory.Exists(wrapped) ? wrapped : versionDir;
        }
    }
}
