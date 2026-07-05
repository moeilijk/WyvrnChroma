using Xunit;

namespace WyvrnChroma.Tests;

public class ChromaPlayerTests
{
    // An animation with the given frame durations; each frame's single LED encodes its index (R = index).
    private static ChromaAnimation Anim(params float[] durations)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);          // version
        w.Write((byte)1);    // deviceType (2D)
        w.Write((byte)2);    // device
        w.Write(durations.Length);
        for (var i = 0; i < durations.Length; i++)
        {
            w.Write(durations[i]);
            w.Write(i); // 1 LED, colorRef = i -> ChromaColor.R == i
        }
        w.Flush();
        return ChromaAnimation.Parse(ms.ToArray());
    }

    private static int Index(ChromaFrame f) => f.Colors[0].R;

    [Fact]
    public void SingleFrame_AlwaysFrameZero()
    {
        var p = new ChromaPlayer(Anim(1.0f));
        Assert.Equal(0, p.FrameIndexAt(0));
        Assert.Equal(0, p.FrameIndexAt(5));
        Assert.Equal(0, p.FrameIndexAt(-3));
        Assert.Equal(1.0f, p.TotalDuration);
    }

    [Fact]
    public void OneShot_AdvancesThenHoldsLast()
    {
        var p = new ChromaPlayer(Anim(1.0f, 0.5f), loop: false); // total 1.5
        Assert.Equal(0, p.FrameIndexAt(0));
        Assert.Equal(0, p.FrameIndexAt(0.999));
        Assert.Equal(1, p.FrameIndexAt(1.0)); // boundary -> next frame
        Assert.Equal(1, p.FrameIndexAt(1.4));
        Assert.Equal(1, p.FrameIndexAt(1.5)); // held past the end
        Assert.Equal(1, p.FrameIndexAt(100));
        Assert.False(p.IsFinished(1.4));
        Assert.True(p.IsFinished(1.5));
    }

    [Fact]
    public void Loop_WrapsAround()
    {
        var p = new ChromaPlayer(Anim(1.0f, 0.5f), loop: true); // total 1.5
        Assert.Equal(0, p.FrameIndexAt(1.5)); // 1.5 % 1.5 = 0.0
        Assert.Equal(0, p.FrameIndexAt(2.0)); // 0.5 -> frame 0
        Assert.Equal(1, p.FrameIndexAt(2.6)); // 1.1 -> frame 1
        Assert.False(p.IsFinished(100));
    }

    [Fact]
    public void FrameAt_ReturnsTheCorrectFrame()
    {
        var p = new ChromaPlayer(Anim(1.0f, 0.5f), loop: false);
        Assert.Equal(0, Index(p.FrameAt(0.5)));
        Assert.Equal(1, Index(p.FrameAt(1.2)));
    }

    [Fact]
    public void ZeroDurationFrames_DoNotDivideByZero()
    {
        Assert.Equal(0, new ChromaPlayer(Anim(0f, 0f), loop: true).FrameIndexAt(10));
        Assert.Equal(1, new ChromaPlayer(Anim(0f, 0f), loop: false).FrameIndexAt(10));
    }

    [Fact]
    public void NullAnimation_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ChromaPlayer(null!));
}
