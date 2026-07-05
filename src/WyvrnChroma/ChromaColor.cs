namespace WyvrnChroma;

/// <summary>
/// A single RGB colour from a Chroma animation. On disk a <c>.chroma</c> stores each LED as a
/// little-endian 0x00BBGGRR word (a Win32 <c>COLORREF</c>).
/// </summary>
public readonly struct ChromaColor : IEquatable<ChromaColor>
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public ChromaColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>Decode a raw <c>.chroma</c> colour word (0x00BBGGRR / Win32 COLORREF).</summary>
    public static ChromaColor FromColorRef(int colorRef)
        => new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));

    public bool IsBlack => R == 0 && G == 0 && B == 0;

    public bool Equals(ChromaColor other) => R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is ChromaColor c && Equals(c);
    public override int GetHashCode() => (R << 16) | (G << 8) | B;
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
