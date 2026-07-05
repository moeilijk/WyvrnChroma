using Xunit;

namespace WyvrnChroma.Tests;

public class ChromaGridTests
{
    [Theory]
    [InlineData(ChromaDeviceType.TwoD, 3, 8, 24)] // extended keyboard (007's .chroma)
    [InlineData(ChromaDeviceType.TwoD, 0, 6, 22)] // classic keyboard
    [InlineData(ChromaDeviceType.TwoD, 2, 9, 7)]  // mouse
    [InlineData(ChromaDeviceType.TwoD, 1, 4, 5)]  // keypad
    [InlineData(ChromaDeviceType.OneD, 2, 1, 15)] // mousepad
    [InlineData(ChromaDeviceType.OneD, 1, 1, 5)]  // headset
    [InlineData(ChromaDeviceType.OneD, 0, 1, 5)]  // chroma link
    public void ForDevice_KnownDevices(ChromaDeviceType type, byte device, int rows, int cols)
    {
        var grid = ChromaGrids.ForDevice(type, device);
        Assert.NotNull(grid);
        Assert.Equal(rows, grid!.Value.Rows);
        Assert.Equal(cols, grid.Value.Columns);
        Assert.Equal(rows * cols, grid.Value.LedCount);
    }

    [Fact]
    public void ForDevice_Unknown_ReturnsNull()
        => Assert.Null(ChromaGrids.ForDevice(ChromaDeviceType.TwoD, 9));

    [Fact]
    public void Resample_SameDimensions_IsIdentity()
    {
        var src = new[] { C(1), C(2), C(3), C(4) }; // 2x2
        var dst = ChromaGrids.Resample(src, 2, 2, 2, 2);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Resample_PreservesCorners()
    {
        // 8x24 grid where each cell encodes its (row,col) so we can check the mapping of corners.
        const int sr = 8, sc = 24;
        var src = new ChromaColor[sr * sc];
        for (var r = 0; r < sr; r++)
        for (var c = 0; c < sc; c++)
            src[r * sc + c] = new ChromaColor((byte)r, (byte)c, 0);

        const int dr = 6, dc = 22;
        var dst = ChromaGrids.Resample(src, sr, sc, dr, dc);

        // Corners of the destination map to corners of the source.
        Assert.Equal(new ChromaColor(0, 0, 0), dst[0]);                       // top-left
        Assert.Equal(new ChromaColor(0, (byte)(sc - 1), 0), dst[dc - 1]);     // top-right
        Assert.Equal(new ChromaColor((byte)(sr - 1), 0, 0), dst[(dr - 1) * dc]); // bottom-left
        Assert.Equal(new ChromaColor((byte)(sr - 1), (byte)(sc - 1), 0), dst[dr * dc - 1]); // bottom-right
    }

    [Fact]
    public void Resample_ToSingleRow_PicksFirstRow()
    {
        var src = new[] { C(10), C(20), C(30), C(40) }; // 2x2
        var dst = ChromaGrids.Resample(src, 2, 2, 1, 2);
        Assert.Equal(new[] { C(10), C(20) }, dst); // row 0 only
    }

    [Theory]
    [InlineData(0, 2, 2, 2)]
    [InlineData(2, 2, 0, 2)]
    public void Resample_NonPositiveDims_Throws(int sr, int sc, int dr, int dc)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ChromaGrids.Resample(new[] { C(1) }, sr, sc, dr, dc));

    [Fact]
    public void Resample_SourceSmallerThanGrid_Throws()
        => Assert.Throws<ArgumentException>(() => ChromaGrids.Resample(new[] { C(1) }, 2, 2, 2, 2));

    [Fact]
    public void Animation_Grid_ReflectsDevice()
    {
        // build a minimal 8x24 keyboard-extended frame
        var leds = 8 * 24;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1); w.Write((byte)1); w.Write((byte)3); w.Write(1); // ver, 2D, device 3, 1 frame
        w.Write(1f / 30f);
        for (var i = 0; i < leds; i++) w.Write(0x00112233);
        w.Flush();

        var anim = ChromaAnimation.Parse(ms.ToArray());
        Assert.Equal(192, anim.LedCount);
        Assert.Equal(ChromaGrids.ExtendedKeyboard, anim.Grid);
    }

    private static ChromaColor C(byte v) => new(v, v, v);
}
