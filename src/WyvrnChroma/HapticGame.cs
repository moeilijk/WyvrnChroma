namespace WyvrnChroma;

/// <summary>
/// An event resolved to renderable data: the <c>wyvrn.config</c> mapping (timing/priority semantics) plus the
/// parsed <c>.chroma</c> animation and the file it came from.
/// </summary>
public sealed record ResolvedChromaEffect(ChromaEventEffect Mapping, ChromaAnimation Animation, string FilePath)
{
    /// <summary>A player primed with this effect's loop semantics (<c>Loop=="infinity"</c> → looping).</summary>
    public ChromaPlayer CreatePlayer() => new(Animation, loop: Mapping.Loops);
}

/// <summary>
/// One game's haptic folder: its <c>wyvrn.config</c> and the <c>.chroma</c> files beside it. Resolves an incoming
/// event name (from the Chroma event stream, e.g. <c>Crouch_Off</c>) to the parsed animation to play, tolerating
/// events/devices that are not bound and effects whose <c>.chroma</c> file is absent (returns <c>null</c>).
/// </summary>
public sealed class HapticGame
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ChromaAnimation?> _animationCache = new(StringComparer.OrdinalIgnoreCase);
    private WyvrnConfig? _config;

    /// <summary>The game name (the haptic folder's name, e.g. <c>007 First Light</c>).</summary>
    public string Name { get; }

    /// <summary>The game's haptic folder (contains <c>wyvrn.config</c> + the <c>.chroma</c> files).</summary>
    public string Directory { get; }

    public HapticGame(string name, string directory)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    /// <summary>
    /// This game's <c>wyvrn.config</c>. The CDN catalog ships it per game but with inconsistent casing
    /// (<c>wyvrn.config</c> / <c>Wyvrn.config</c> / <c>WYVRN.config</c>), so it is resolved case-insensitively —
    /// case-sensitive file systems (Linux/WSL) would otherwise miss ~15% of the catalog. Falls back to the
    /// canonical lower-case name when none is present.
    /// </summary>
    public string ConfigPath => FindConfig(Directory) ?? Path.Combine(Directory, "wyvrn.config");

    /// <summary>The actual <c>wyvrn.config</c> file in <paramref name="directory"/> (any casing), or <c>null</c>.</summary>
    internal static string? FindConfig(string directory) =>
        System.IO.Directory.Exists(directory)
            ? System.IO.Directory.EnumerateFiles(directory)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "wyvrn.config",
                    StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>The parsed <c>wyvrn.config</c> (loaded once, cached).</summary>
    public WyvrnConfig Config
    {
        get
        {
            lock (_gate)
                return _config ??= WyvrnConfig.Load(ConfigPath);
        }
    }

    /// <summary>The <c>wyvrn.config</c> mapping for an event/device, or <c>null</c> when it is not bound.</summary>
    public ChromaEventEffect? GetMapping(string eventId, ChromaDevice device = ChromaDevice.Keyboard) =>
        Config.GetEffect(eventId, device);

    /// <summary>
    /// Resolve an event to its parsed effect for <paramref name="device"/>. Returns <c>null</c> when the event/
    /// device is not bound or its <c>.chroma</c> file is missing; a present-but-corrupt file surfaces as a
    /// <see cref="ChromaFormatException"/>.
    /// </summary>
    public ResolvedChromaEffect? ResolveEvent(string eventId, ChromaDevice device = ChromaDevice.Keyboard)
    {
        var mapping = GetMapping(eventId, device);
        if (mapping is null)
            return null;

        var anim = LoadAnimation(mapping.ChromaEffect);
        if (anim is null)
            return null;

        return new ResolvedChromaEffect(mapping, anim, ChromaFilePath(mapping.ChromaEffect));
    }

    /// <summary>Just the parsed animation for an event/device (or <c>null</c>), without the mapping metadata.</summary>
    public ChromaAnimation? GetEffectForEvent(string eventId, ChromaDevice device = ChromaDevice.Keyboard) =>
        ResolveEvent(eventId, device)?.Animation;

    /// <summary>
    /// Resolve the game's looping idle base for <paramref name="device"/> — the resting animation that plays
    /// between one-shot events. Uses the config's idle-flagged effect (<see cref="WyvrnConfig.GetIdleEffect"/>),
    /// falling back to a <c>Idle_&lt;device&gt;.chroma</c> file when the config declares none. The returned effect
    /// always loops (an idle base plays continuously regardless of the source effect's own <c>Loop</c>). Returns
    /// <c>null</c> only when neither the config nor a matching file yields a parseable animation.
    /// </summary>
    public ResolvedChromaEffect? ResolveIdle(ChromaDevice device = ChromaDevice.Keyboard)
    {
        var mapping = Config.GetIdleEffect(device);
        if (mapping is not null)
        {
            var anim = LoadAnimation(mapping.ChromaEffect);
            if (anim is not null)
                return new ResolvedChromaEffect(mapping with { Loops = true }, anim,
                    ChromaFilePath(mapping.ChromaEffect));
        }

        var stem = "Idle_" + device;
        var fileAnim = LoadAnimation(stem);
        if (fileAnim is not null)
            return new ResolvedChromaEffect(
                new ChromaEventEffect(stem, device, Animation: "Idle", Loops: true, Interrupt: false,
                    Priority: null, Idle: true, Push: false),
                fileAnim, ChromaFilePath(stem));

        return null;
    }

    private string ChromaFilePath(string chromaEffect) => Path.Combine(Directory, chromaEffect + ".chroma");

    private ChromaAnimation? LoadAnimation(string chromaEffect)
    {
        lock (_gate)
        {
            if (_animationCache.TryGetValue(chromaEffect, out var cached))
                return cached;

            var path = ChromaFilePath(chromaEffect);
            var anim = File.Exists(path) ? ChromaAnimation.Load(path) : null;
            _animationCache[chromaEffect] = anim;
            return anim;
        }
    }
}

/// <summary>
/// A resolved Wyvrn haptic-folder catalog: a directory whose subfolders are per-game haptic folders. Combine with
/// <see cref="HapticCatalogProvider"/> (which produces the root directory from a local install or the CDN) to go
/// from a catalog to a game's per-event <c>.chroma</c> effects.
/// </summary>
public sealed class HapticCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HapticGame> _games = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The directory that directly contains the per-game folders.</summary>
    public string RootDirectory { get; }

    public HapticCatalog(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
        RootDirectory = rootDirectory;
    }

    /// <summary>
    /// A subfolder is a game when it ships a <c>wyvrn.config</c>, resolved case-insensitively (the CDN catalog uses
    /// mixed casing — <c>wyvrn.config</c> / <c>Wyvrn.config</c> / <c>WYVRN.config</c>). The config is required: the
    /// event→<c>.chroma</c> mapping it encodes cannot be reconstructed from file names (one event often maps to a
    /// differently-named or shared effect, e.g. <c>Aim_On</c> → <c>Aim_Off15_Keyboard</c>).
    /// </summary>
    private static bool IsGameFolder(string dir) => HapticGame.FindConfig(dir) is not null;

    /// <summary>Names of the games present (subfolders that contain a <c>wyvrn.config</c>, any casing).</summary>
    public IReadOnlyList<string> Games
    {
        get
        {
            if (!System.IO.Directory.Exists(RootDirectory))
                return Array.Empty<string>();
            return System.IO.Directory.EnumerateDirectories(RootDirectory)
                .Where(IsGameFolder)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToArray();
        }
    }

    /// <summary>The game whose folder name matches <paramref name="gameName"/> (case-insensitive), or <c>null</c>.</summary>
    public HapticGame? GetGame(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return null;

        lock (_gate)
        {
            if (_games.TryGetValue(gameName, out var cached))
                return cached;

            var dir = Path.Combine(RootDirectory, gameName);
            if (!System.IO.Directory.Exists(dir) || !IsGameFolder(dir))
                return null;

            var game = new HapticGame(gameName, dir);
            _games[gameName] = game;
            return game;
        }
    }

    /// <summary>Resolve a game's event to its parsed effect, or <c>null</c> when anything along the path is absent.</summary>
    public ResolvedChromaEffect? GetEffectForEvent(string gameName, string eventId,
        ChromaDevice device = ChromaDevice.Keyboard) =>
        GetGame(gameName)?.ResolveEvent(eventId, device);

    /// <summary>Resolve a game's looping idle base for <paramref name="device"/>, or <c>null</c>.</summary>
    public ResolvedChromaEffect? GetIdleEffect(string gameName, ChromaDevice device = ChromaDevice.Keyboard) =>
        GetGame(gameName)?.ResolveIdle(device);
}
