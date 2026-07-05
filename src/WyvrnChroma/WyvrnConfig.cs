using System.Text.Json;
using System.Text.Json.Serialization;

namespace WyvrnChroma;

/// <summary>The Razer Chroma device a <c>.chroma</c> effect targets (the suffix of a <c>Chroma_Effect</c> id).</summary>
public enum ChromaDevice
{
    Keyboard,
    Mouse,
    Mousepad,
    Headset,
    Keypad,
    ChromaLink,
}

/// <summary>Relative ordering Wyvrn assigns to an effect when several want to play at once.</summary>
public enum WyvrnPriority
{
    VeryLow,
    Low,
    Medium,
    High,
    VeryHigh,
}

/// <summary>
/// One per-device Chroma effect bound to a game event in <c>wyvrn.config</c>.
/// <see cref="ChromaEffect"/> is the <c>.chroma</c> file name stem (e.g. <c>Crouch_On_Keyboard</c> →
/// <c>Crouch_On_Keyboard.chroma</c>); resolving it to an actual file (which may be absent) is the catalog's job.
/// </summary>
public sealed record ChromaEventEffect(
    string ChromaEffect,
    ChromaDevice Device,
    string? Animation,
    bool Loops,
    bool Interrupt,
    WyvrnPriority? Priority,
    bool Idle,
    bool Push);

/// <summary>
/// A parsed Wyvrn <c>wyvrn.config</c>: maps a game's external command ids (the event names that arrive over the
/// Chroma event stream, e.g. <c>Crouch_Off</c>) to their per-device <c>.chroma</c> effects. Pure data; no Razer
/// DLLs and no file-system access beyond <see cref="Load"/>.
/// </summary>
public sealed class WyvrnConfig
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ChromaEventEffect>> _byEvent;

    private WyvrnConfig(IReadOnlyDictionary<string, IReadOnlyList<ChromaEventEffect>> byEvent) => _byEvent = byEvent;

    /// <summary>All external command ids (event names) declared in the config.</summary>
    public IReadOnlyCollection<string> Events => (IReadOnlyCollection<string>)_byEvent.Keys;

    /// <summary>Read and parse a <c>wyvrn.config</c> file (UTF-8, BOM tolerated).</summary>
    public static WyvrnConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parse <c>wyvrn.config</c> JSON text.</summary>
    public static WyvrnConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new WyvrnConfigException("wyvrn.config is empty.");

        ConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ConfigDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new WyvrnConfigException("wyvrn.config is not valid JSON.", ex);
        }

        if (dto?.ExternalCommands is null)
            throw new WyvrnConfigException("wyvrn.config has no ExternalCommands.");

        var map = new Dictionary<string, IReadOnlyList<ChromaEventEffect>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in dto.ExternalCommands)
        {
            if (string.IsNullOrEmpty(cmd.Id) || cmd.ChromaEvents is null)
                continue;

            var effects = new List<ChromaEventEffect>(cmd.ChromaEvents.Count);
            foreach (var ce in cmd.ChromaEvents)
            {
                if (string.IsNullOrEmpty(ce.ChromaEffect) || !TryParseDevice(ce.ChromaEffect, out var device))
                    continue; // unrecognised / device-less effect — nothing to render

                effects.Add(new ChromaEventEffect(
                    ChromaEffect: ce.ChromaEffect,
                    Device: device,
                    Animation: ce.Animation,
                    Loops: IsInfiniteLoop(ce.Loop),
                    Interrupt: ce.Interrupt ?? false,
                    Priority: ParsePriority(ce.Priority),
                    Idle: !string.IsNullOrEmpty(ce.Idle),
                    Push: ce.Push ?? false));
            }

            if (effects.Count == 0)
                continue;

            // A duplicated command id aggregates its effects rather than discarding the earlier ones.
            if (map.TryGetValue(cmd.Id, out var existing))
                effects.InsertRange(0, existing);
            map[cmd.Id] = effects;
        }

        return new WyvrnConfig(map);
    }

    /// <summary>All per-device effects bound to <paramref name="eventId"/>, or an empty list when unknown.</summary>
    public IReadOnlyList<ChromaEventEffect> GetEffects(string eventId) =>
        eventId is not null && _byEvent.TryGetValue(eventId, out var list) ? list : Array.Empty<ChromaEventEffect>();

    /// <summary>The effect bound to <paramref name="eventId"/> for <paramref name="device"/>, or <c>null</c>.</summary>
    public ChromaEventEffect? GetEffect(string eventId, ChromaDevice device)
    {
        foreach (var e in GetEffects(eventId))
            if (e.Device == device)
                return e;
        return null;
    }

    /// <summary>
    /// The effect that acts as the game's looping idle base for <paramref name="device"/> — what should play
    /// between one-shot events. There is no <c>Idle</c> external command; Wyvrn instead flags effects with
    /// <c>"Idle": "set"</c> (several commands may re-set the running base, e.g. <c>Idle_Keyboard</c> and
    /// <c>Shoot_Red2_Keyboard</c>). The canonical resting effect is the one named <c>Idle_&lt;device&gt;</c>
    /// (007 ships <c>Idle_Keyboard.chroma</c>, the golden wave); prefer it, else the first idle-flagged effect for
    /// the device. Returns <c>null</c> when the game declares no idle for the device.
    /// </summary>
    public ChromaEventEffect? GetIdleEffect(ChromaDevice device)
    {
        var canonical = "Idle_" + device;
        ChromaEventEffect? fallback = null;
        foreach (var list in _byEvent.Values)
            foreach (var e in list)
            {
                if (!e.Idle || e.Device != device)
                    continue;
                if (string.Equals(e.ChromaEffect, canonical, StringComparison.OrdinalIgnoreCase))
                    return e;
                fallback ??= e;
            }
        return fallback;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Device = the token after the last underscore of the Chroma_Effect (e.g. <c>..._Keyboard</c>).</summary>
    internal static bool TryParseDevice(string chromaEffect, out ChromaDevice device)
    {
        int us = chromaEffect.LastIndexOf('_');
        var token = us >= 0 ? chromaEffect[(us + 1)..] : chromaEffect;
        return Enum.TryParse(token, ignoreCase: true, out device);
    }

    private static bool IsInfiniteLoop(string? loop) =>
        loop is not null && loop.Equals("infinity", StringComparison.OrdinalIgnoreCase);

    private static WyvrnPriority? ParsePriority(string? priority) =>
        Enum.TryParse<WyvrnPriority>(priority, ignoreCase: true, out var p) ? p : null;

    private sealed record ConfigDto(
        [property: JsonPropertyName("ExternalCommands")] List<CommandDto>? ExternalCommands);

    private sealed record CommandDto(
        [property: JsonPropertyName("External_Command_ID")] string? Id,
        [property: JsonPropertyName("Chroma_Events")] List<ChromaEventDto>? ChromaEvents);

    private sealed record ChromaEventDto(
        [property: JsonPropertyName("Chroma_Effect")] string? ChromaEffect,
        [property: JsonPropertyName("Animation")] string? Animation,
        [property: JsonPropertyName("Loop")] string? Loop,
        [property: JsonPropertyName("Interrupt")] bool? Interrupt,
        [property: JsonPropertyName("Priority")] string? Priority,
        [property: JsonPropertyName("Idle")] string? Idle,
        [property: JsonPropertyName("Push")] bool? Push);
}

/// <summary>Thrown when a <c>wyvrn.config</c> is empty, malformed, or has no commands.</summary>
public sealed class WyvrnConfigException : Exception
{
    public WyvrnConfigException(string message) : base(message)
    {
    }

    public WyvrnConfigException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
