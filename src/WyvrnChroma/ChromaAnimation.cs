using System.Buffers.Binary;

namespace WyvrnChroma;

/// <summary>Device class a <c>.chroma</c> animation targets (Razer ChromaAnimation device-type enum).</summary>
public enum ChromaDeviceType : byte
{
    OneD = 0,
    TwoD = 1,
}

/// <summary>One animation frame: the LED colours to display for <see cref="Duration"/> seconds.</summary>
public sealed class ChromaFrame
{
    public float Duration { get; }
    public IReadOnlyList<ChromaColor> Colors { get; }

    public ChromaFrame(float duration, IReadOnlyList<ChromaColor> colors)
    {
        Duration = duration;
        Colors = colors;
    }
}

/// <summary>
/// A parsed Razer ChromaAnimation (<c>.chroma</c>) file — the per-event effect data 007: First Light (and
/// other Wyvrn games) ship. Format (little-endian):
/// <code>
///   int32  version
///   byte   deviceType            (0 = 1D, 1 = 2D)
///   byte   device                (device enum within the type)
///   int32  frameCount
///   frameCount × (
///       float32 duration         (seconds)
///       ledCount × int32 colour  (0x00BBGGRR)
///   )
/// </code>
/// <c>ledCount</c> is constant across frames and derived from the file length (no Razer enum tables needed),
/// e.g. a keyboard frame is 6×22 = 132 LEDs.
/// </summary>
public sealed class ChromaAnimation
{
    private const int HeaderSize = 10; // int32 version + byte deviceType + byte device + int32 frameCount

    public int Version { get; }
    public ChromaDeviceType DeviceType { get; }
    public byte Device { get; }

    /// <summary>Number of LEDs per frame (e.g. 192 for an 8×24 extended keyboard).</summary>
    public int LedCount { get; }

    /// <summary>
    /// The 2D grid this animation's device uses (e.g. 8×24 for an extended keyboard), or <c>null</c> when the
    /// device is unknown — then treat each frame as a flat <see cref="LedCount"/> strip.
    /// </summary>
    public ChromaGrid? Grid => ChromaGrids.ForDevice(DeviceType, Device);

    public IReadOnlyList<ChromaFrame> Frames { get; }

    private ChromaAnimation(int version, ChromaDeviceType deviceType, byte device, int ledCount,
        IReadOnlyList<ChromaFrame> frames)
    {
        Version = version;
        DeviceType = deviceType;
        Device = device;
        LedCount = ledCount;
        Frames = frames;
    }

    public static ChromaAnimation Load(string path) => Parse(File.ReadAllBytes(path));

    public static ChromaAnimation Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new ChromaFormatException($"Too short: {data.Length} bytes (need at least {HeaderSize}).");

        var version = BinaryPrimitives.ReadInt32LittleEndian(data);
        var deviceType = data[4];
        var device = data[5];
        var frameCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(6, 4));

        if (frameCount <= 0)
            throw new ChromaFormatException($"Invalid frame count: {frameCount}.");

        long body = data.Length - HeaderSize;
        if (body % frameCount != 0)
            throw new ChromaFormatException($"Body of {body} bytes is not divisible into {frameCount} frames.");

        long perFrame = body / frameCount;
        long colourBytes = perFrame - sizeof(float); // each frame starts with a float duration
        if (colourBytes <= 0 || colourBytes % 4 != 0)
            throw new ChromaFormatException($"Per-frame colour bytes ({colourBytes}) invalid.");
        var ledCount = (int)(colourBytes / 4);

        var frames = new ChromaFrame[frameCount];
        var offset = HeaderSize;
        for (var f = 0; f < frameCount; f++)
        {
            var duration = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, sizeof(float)));
            offset += sizeof(float);

            var colors = new ChromaColor[ledCount];
            for (var i = 0; i < ledCount; i++)
            {
                var word = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                offset += 4;
                colors[i] = ChromaColor.FromColorRef(word);
            }

            frames[f] = new ChromaFrame(duration, colors);
        }

        return new ChromaAnimation(version, (ChromaDeviceType)deviceType, device, ledCount, frames);
    }
}

/// <summary>Thrown when a <c>.chroma</c> blob does not match the expected ChromaAnimation layout.</summary>
public sealed class ChromaFormatException : Exception
{
    public ChromaFormatException(string message) : base(message)
    {
    }
}
