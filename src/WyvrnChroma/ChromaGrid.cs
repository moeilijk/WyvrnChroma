namespace WyvrnChroma;

/// <summary>The 2D dimensions of a device's LED grid (1D devices are a single row).</summary>
public readonly record struct ChromaGrid(int Rows, int Columns)
{
    public int LedCount => Rows * Columns;
}

/// <summary>
/// Maps a <c>.chroma</c> <c>(deviceType, device)</c> header to the Razer ChromaAnimation grid dimensions, and
/// resamples a frame from one grid to another. Sizes are the documented Razer grids, confirmed against the real
/// 007 catalog: keyboard <c>device==3</c> is the <b>extended</b> 8×24 grid (192 LEDs), not the classic 6×22.
/// </summary>
public static class ChromaGrids
{
    /// <summary>The classic Razer keyboard custom grid (RZKEY positions: row = HIBYTE, col = LOBYTE).</summary>
    public static readonly ChromaGrid StandardKeyboard = new(6, 22);

    /// <summary>The extended keyboard custom grid the Wyvrn <c>.chroma</c> keyboard effects are authored for.</summary>
    public static readonly ChromaGrid ExtendedKeyboard = new(8, 24);

    /// <summary>
    /// The grid for a <c>.chroma</c> header, or <c>null</c> when the (type, device) pair is unknown — in which
    /// case the caller should treat the frame as a flat 1×<c>LedCount</c> strip.
    /// </summary>
    public static ChromaGrid? ForDevice(ChromaDeviceType deviceType, byte device) => (deviceType, device) switch
    {
        (ChromaDeviceType.OneD, 0) => new ChromaGrid(1, 5),   // ChromaLink
        (ChromaDeviceType.OneD, 1) => new ChromaGrid(1, 5),   // Headset
        (ChromaDeviceType.OneD, 2) => new ChromaGrid(1, 15),  // Mousepad
        (ChromaDeviceType.TwoD, 0) => StandardKeyboard,       // Keyboard (classic)
        (ChromaDeviceType.TwoD, 1) => new ChromaGrid(4, 5),   // Keypad
        (ChromaDeviceType.TwoD, 2) => new ChromaGrid(9, 7),   // Mouse
        (ChromaDeviceType.TwoD, 3) => ExtendedKeyboard,       // Keyboard (extended)
        _ => null,
    };

    /// <summary>
    /// Nearest-neighbour resample of a row-major <paramref name="source"/> grid (<paramref name="srcRows"/>×
    /// <paramref name="srcCols"/>) into a <paramref name="dstRows"/>×<paramref name="dstCols"/> grid. Used to map
    /// the extended 8×24 keyboard frame onto the classic 6×22 grid Aurora already renders, preserving the overall
    /// gradient/wave the effect draws across the whole keyboard.
    /// </summary>
    public static ChromaColor[] Resample(IReadOnlyList<ChromaColor> source, int srcRows, int srcCols,
        int dstRows, int dstCols)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (srcRows <= 0 || srcCols <= 0 || dstRows <= 0 || dstCols <= 0)
            throw new ArgumentOutOfRangeException(nameof(dstRows), "Grid dimensions must be positive.");
        if (source.Count < srcRows * srcCols)
            throw new ArgumentException("Source is smaller than its stated grid.", nameof(source));

        var result = new ChromaColor[dstRows * dstCols];
        for (var r = 0; r < dstRows; r++)
        {
            var sr = Map(r, dstRows, srcRows);
            for (var c = 0; c < dstCols; c++)
            {
                var sc = Map(c, dstCols, srcCols);
                result[r * dstCols + c] = source[sr * srcCols + sc];
            }
        }
        return result;
    }

    // Map a destination index to the nearest source index across the two extents.
    private static int Map(int dstIndex, int dstExtent, int srcExtent)
    {
        if (dstExtent == 1)
            return 0;
        var src = (int)Math.Round(dstIndex * (srcExtent - 1.0) / (dstExtent - 1));
        return Math.Clamp(src, 0, srcExtent - 1);
    }
}
