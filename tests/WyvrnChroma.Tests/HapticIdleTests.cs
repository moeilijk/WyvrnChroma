using Xunit;

namespace WyvrnChroma.Tests;

/// <summary>
/// Idle-base resolution against realistic <c>wyvrn.config</c> semantics: there is NO <c>Idle</c> external command;
/// the game instead flags effects with <c>"Idle": "set"</c> and several commands may re-set the running base
/// (007 marks both <c>Idle_Keyboard</c> and <c>Shoot_Red2_Keyboard</c>). The canonical resting effect is the one
/// named <c>Idle_&lt;device&gt;</c>, and the idle base must loop continuously.
/// </summary>
public sealed class HapticIdleTests : IDisposable
{
    private const int Gold = 0x0000C8C8;
    private const int Red = 0x000000FF;

    private readonly string _root;

    public HapticIdleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WyvrnIdleTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeGame(string name, string config, params (string stem, int color)[] chromas)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "wyvrn.config"), config);
        foreach (var (stem, color) in chromas)
            WriteChroma(Path.Combine(dir, stem + ".chroma"), color);
        return dir;
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

    // Mirrors 007: no "Idle" command; Idle_Keyboard is used by commands (Animation "Idle", no loop) and flagged
    // Idle:"set" in others. Shoot_Red2_Keyboard is ALSO Idle:"set" — the canonical Idle_<device> must still win.
    private const string RealisticConfig = """
    {
      "ExternalCommands": [
        { "External_Command_ID": "Shoot_SMG", "Chroma_Events": [ { "Chroma_Effect": "Shoot_Red2_Keyboard", "Animation": "Shoot", "Idle": "set" } ] },
        { "External_Command_ID": "Climb_Off", "Chroma_Events": [ { "Chroma_Effect": "Idle_Keyboard", "Animation": "Idle", "Interrupt": true, "Priority": "VeryHigh" } ] },
        { "External_Command_ID": "Start", "Chroma_Events": [ { "Chroma_Effect": "Idle_Keyboard", "Idle": "set" } ] }
      ]
    }
    """;

    [Fact]
    public void GetIdleEffect_PrefersCanonicalIdleEffect_AndForcesLoop()
    {
        MakeGame("007", RealisticConfig, ("Idle_Keyboard", Gold), ("Shoot_Red2_Keyboard", Red));
        var catalog = new HapticCatalog(_root);

        var idle = catalog.GetIdleEffect("007", ChromaDevice.Keyboard);

        Assert.NotNull(idle);
        Assert.Equal("Idle_Keyboard", idle!.Mapping.ChromaEffect); // canonical wins over the other Idle:"set" effect
        Assert.True(idle.Mapping.Loops);                            // idle base always loops
        Assert.True(idle.CreatePlayer().Loop);
        Assert.Equal(new ChromaColor(0xC8, 0xC8, 0x00), idle.Animation.Frames[0].Colors[0]); // the golden wave
    }

    [Fact]
    public void GetIdleEffect_NoRealIdleCommand_StillResolves()
    {
        // Proves the exact O2 bug: there is no External_Command_ID "Idle", so the old event lookup returned null.
        MakeGame("007", RealisticConfig, ("Idle_Keyboard", Gold), ("Shoot_Red2_Keyboard", Red));
        var catalog = new HapticCatalog(_root);

        Assert.Null(catalog.GetEffectForEvent("007", "Idle")); // no such command
        Assert.NotNull(catalog.GetIdleEffect("007", ChromaDevice.Keyboard)); // but the idle base resolves
    }

    [Fact]
    public void GetIdleEffect_FilenameFallback_WhenConfigDeclaresNoIdle()
    {
        // Config binds no idle-flagged effect, but the Idle_Keyboard.chroma file is present on disk.
        const string noIdleFlag = """
        {
          "ExternalCommands": [
            { "External_Command_ID": "Hit", "Chroma_Events": [ { "Chroma_Effect": "Hit_Keyboard", "Animation": "Hit" } ] }
          ]
        }
        """;
        MakeGame("Fallback", noIdleFlag, ("Hit_Keyboard", Red), ("Idle_Keyboard", Gold));
        var catalog = new HapticCatalog(_root);

        var idle = catalog.GetIdleEffect("Fallback", ChromaDevice.Keyboard);

        Assert.NotNull(idle);
        Assert.Equal("Idle_Keyboard", idle!.Mapping.ChromaEffect);
        Assert.True(idle.Mapping.Loops);
        Assert.Equal(new ChromaColor(0xC8, 0xC8, 0x00), idle.Animation.Frames[0].Colors[0]);
    }

    [Fact]
    public void GetIdleEffect_NoIdleFlagAndNoFile_ReturnsNull()
    {
        const string noIdleFlag = """
        {
          "ExternalCommands": [
            { "External_Command_ID": "Hit", "Chroma_Events": [ { "Chroma_Effect": "Hit_Keyboard", "Animation": "Hit" } ] }
          ]
        }
        """;
        MakeGame("Bare", noIdleFlag, ("Hit_Keyboard", Red)); // no Idle_Keyboard file
        var catalog = new HapticCatalog(_root);

        Assert.Null(catalog.GetIdleEffect("Bare", ChromaDevice.Keyboard));
    }

    [Fact]
    public void GetIdleEffect_WrongDevice_ReturnsNull()
    {
        MakeGame("007", RealisticConfig, ("Idle_Keyboard", Gold), ("Shoot_Red2_Keyboard", Red));
        var catalog = new HapticCatalog(_root);

        // Idle is only bound/available for the keyboard here; no Idle_Mouse effect or file.
        Assert.Null(catalog.GetIdleEffect("007", ChromaDevice.Mouse));
    }
}
