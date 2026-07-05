namespace WyvrnChroma;

/// <summary>
/// Plays a <see cref="ChromaAnimation"/> over time. Given an elapsed time it returns the frame to show:
/// frame durations are cumulative, and <see cref="Loop"/> decides what happens past the end —
/// loop back to the start, or hold the last frame (one-shot). This matches the Wyvrn semantics where an
/// event animation either loops (the Idle base) or plays once and the next event takes over.
/// </summary>
public sealed class ChromaPlayer
{
    private readonly float[] _cumulativeEnd; // cumulative end time (seconds) of each frame

    public ChromaAnimation Animation { get; }
    public bool Loop { get; }

    /// <summary>Total play length in seconds (sum of frame durations).</summary>
    public float TotalDuration { get; }

    public ChromaPlayer(ChromaAnimation animation, bool loop = true)
    {
        Animation = animation ?? throw new ArgumentNullException(nameof(animation));
        if (animation.Frames.Count == 0)
            throw new ArgumentException("Animation has no frames.", nameof(animation));

        Loop = loop;
        _cumulativeEnd = new float[animation.Frames.Count];
        var sum = 0f;
        for (var i = 0; i < animation.Frames.Count; i++)
        {
            var d = animation.Frames[i].Duration;
            if (d < 0f || float.IsNaN(d)) d = 0f;
            sum += d;
            _cumulativeEnd[i] = sum;
        }
        TotalDuration = sum;
    }

    /// <summary>Index of the frame visible at <paramref name="elapsedSeconds"/>.</summary>
    public int FrameIndexAt(double elapsedSeconds)
    {
        var count = Animation.Frames.Count;
        if (count == 1 || elapsedSeconds <= 0d)
            return 0;

        if (TotalDuration <= 0f) // every frame has zero duration
            return Loop ? 0 : count - 1;

        var t = elapsedSeconds;
        if (t >= TotalDuration)
        {
            if (!Loop)
                return count - 1; // hold the last frame
            t %= TotalDuration;
        }

        for (var i = 0; i < _cumulativeEnd.Length; i++)
            if (t < _cumulativeEnd[i])
                return i;
        return count - 1;
    }

    /// <summary>The frame visible at <paramref name="elapsedSeconds"/>.</summary>
    public ChromaFrame FrameAt(double elapsedSeconds) => Animation.Frames[FrameIndexAt(elapsedSeconds)];

    /// <summary>True once a one-shot animation has played through (always false when looping).</summary>
    public bool IsFinished(double elapsedSeconds) => !Loop && elapsedSeconds >= TotalDuration;
}
