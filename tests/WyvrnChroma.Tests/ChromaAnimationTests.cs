using Xunit;

namespace WyvrnChroma.Tests;

public class ChromaAnimationTests
{
    // .chroma colour words are 0x00BBGGRR.
    private const int Green = 0x0000FF00; // R=00 G=FF B=00
    private const int Red = 0x000000FF;   // R=FF G=00 B=00
    private const int Blue = 0x00FF0000;  // R=00 G=00 B=FF
    private const int Gold = 0x0000C8C8;  // R=C8 G=C8 B=00  (007's Idle/menu hue)

    /// <summary>Build a synthetic .chroma blob (no Razer content) for round-trip testing.</summary>
    private static byte[] BuildChroma(int version, byte deviceType, byte device, (float duration, int[] colorRefs)[] frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms); // BinaryWriter is little-endian, matching the format
        w.Write(version);
        w.Write(deviceType);
        w.Write(device);
        w.Write(frames.Length);
        foreach (var (duration, colors) in frames)
        {
            w.Write(duration);
            foreach (var c in colors) w.Write(c);
        }
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Parse_TwoFrames_ReadsHeaderDurationsAndColours()
    {
        var bytes = BuildChroma(1, deviceType: 1, device: 2, new[]
        {
            (1.0f, new[] { Green, Red }),
            (0.5f, new[] { Blue, Gold }),
        });

        var anim = ChromaAnimation.Parse(bytes);

        Assert.Equal(1, anim.Version);
        Assert.Equal(ChromaDeviceType.TwoD, anim.DeviceType);
        Assert.Equal((byte)2, anim.Device);
        Assert.Equal(2, anim.LedCount);
        Assert.Equal(2, anim.Frames.Count);

        Assert.Equal(1.0f, anim.Frames[0].Duration);
        Assert.Equal(new ChromaColor(0x00, 0xFF, 0x00), anim.Frames[0].Colors[0]);
        Assert.Equal(new ChromaColor(0xFF, 0x00, 0x00), anim.Frames[0].Colors[1]);

        Assert.Equal(0.5f, anim.Frames[1].Duration);
        Assert.Equal(new ChromaColor(0x00, 0x00, 0xFF), anim.Frames[1].Colors[0]);
        Assert.Equal(new ChromaColor(0xC8, 0xC8, 0x00), anim.Frames[1].Colors[1]);
    }

    [Fact]
    public void Parse_KeyboardGrid_Derives132Leds()
    {
        var grid = new int[6 * 22];
        for (var i = 0; i < grid.Length; i++) grid[i] = Gold;

        var anim = ChromaAnimation.Parse(BuildChroma(1, deviceType: 1, device: 3, new[] { (1f / 30f, grid) }));

        Assert.Equal(132, anim.LedCount);
        Assert.Single(anim.Frames);
        Assert.All(anim.Frames[0].Colors, c => Assert.Equal(new ChromaColor(0xC8, 0xC8, 0x00), c));
    }

    [Fact]
    public void FromColorRef_DecodesBbGgRr()
    {
        Assert.Equal(new ChromaColor(0xC8, 0xC8, 0x00), ChromaColor.FromColorRef(Gold));
        Assert.True(ChromaColor.FromColorRef(0x00000000).IsBlack);
        Assert.Equal("#C8C800", ChromaColor.FromColorRef(Gold).ToString());
    }

    [Fact]
    public void Parse_TooShort_Throws()
        => Assert.Throws<ChromaFormatException>(() => ChromaAnimation.Parse(new byte[5]));

    [Fact]
    public void Parse_ZeroFrames_Throws()
        => Assert.Throws<ChromaFormatException>(() => ChromaAnimation.Parse(BuildChroma(1, 1, 2, Array.Empty<(float, int[])>())));

    [Fact]
    public void Parse_RaggedBody_Throws()
    {
        // header says 2 frames but the body cannot split evenly into 2
        var bytes = BuildChroma(1, 1, 2, new[] { (1.0f, new[] { Green, Red }) });
        bytes[6] = 2; // overwrite frameCount = 2 while only 1 frame of data is present
        Assert.Throws<ChromaFormatException>(() => ChromaAnimation.Parse(bytes));
    }
}
