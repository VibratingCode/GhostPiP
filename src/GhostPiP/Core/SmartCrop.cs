namespace GhostPiP.Core;

public enum Side { Top, Bottom, Left, Right }

/// <summary>The picture-reading tricks behind Find video, Snap and Shave. No UI, no Windows calls.</summary>
public static class SmartCrop
{
    const int ScanWidth = 320;
    const int ChangeThreshold = 16;
    const double RunThreshold = 255 * 0.03;

    /// <summary>
    /// Fractional (left, top, width, height) box around the part of the window that moved between frames,
    /// i.e. the playing video. Null if nothing moved, or the window resized mid-scan.
    /// </summary>
    public static double[]? FindMotion(IReadOnlyList<Frame> frames)
    {
        if (frames.Count < 2 || frames.Any(f => f.Width != frames[0].Width || f.Height != frames[0].Height))
            return null;

        int w = ScanWidth;
        int h = Math.Max(1, (int)Math.Round((double)w * frames[0].Height / frames[0].Width));
        byte[][] small = frames.Select(f => Shrink(f, w, h)).ToArray();

        var mask = new byte[w * h];
        for (int f = 1; f < small.Length; f++)
        {
            byte[] a = small[f - 1], b = small[f];
            for (int i = 0; i < mask.Length; i++)
                if (Math.Abs(a[i] - b[i]) > ChangeThreshold)
                    mask[i] = 255;
        }

        // Columns first, then rows inside those columns, then columns again inside those rows.
        // Small changing bits (spinners, clocks) fall below the threshold.
        var cols = LongestRun(ColumnProfile(mask, w, 0, h), RunThreshold);
        if (cols is null)
            return null;
        var rows = LongestRun(RowProfile(mask, w, h, cols.Value.Start, cols.Value.End), RunThreshold);
        if (rows is null)
            return null;
        var narrowed = LongestRun(ColumnProfile(mask, w, rows.Value.Start, rows.Value.End), RunThreshold);
        if (narrowed is not null)
            cols = narrowed;

        double l = (double)cols.Value.Start / w, t = (double)rows.Value.Start / h;
        double cw = (double)(cols.Value.End - cols.Value.Start) / w, ch = (double)(rows.Value.End - rows.Value.Start) / h;
        if (cw < 0.05 || ch < 0.05)
            return null;
        return new[] { Math.Round(l, 4), Math.Round(t, 4), Math.Round(cw, 4), Math.Round(ch, 4) };
    }

    /// <summary>
    /// Tightens a motion box against the sharp borders of the video, so the result lands on exact pixels
    /// rather than the 1/320th-of-a-window grid the scan works on.
    /// </summary>
    public static int[] RefineToEdges(Frame frame, int[] edges, int radius = 12)
    {
        var (l, t, r, b) = (edges[0], edges[1], edges[2], edges[3]);
        int nt = StrongestEdge(frame, Side.Top, t, l, r, radius) ?? t;
        int nb = StrongestEdge(frame, Side.Bottom, b, l, r, radius) ?? b;
        int nl = StrongestEdge(frame, Side.Left, l, nt, nb, radius) ?? l;
        int nr = StrongestEdge(frame, Side.Right, r, nt, nb, radius) ?? r;
        return nr - nl >= 16 && nb - nt >= 16 ? new[] { nl, nt, nr, nb } : edges;
    }

    /// <summary>
    /// The pixel boundary near <paramref name="line"/> with the sharpest colour change across the span
    /// (columns for top/bottom, rows for left/right), or null if it's all smooth.
    /// </summary>
    public static int? StrongestEdge(Frame img, Side side, int line, int spanStart, int spanEnd, int radius = 40)
    {
        bool horizontal = side is Side.Top or Side.Bottom;
        int size = horizontal ? img.Height : img.Width;
        int lo = Math.Max(1, line - radius), hi = Math.Min(size - 1, line + radius);
        if (hi < lo || spanEnd - spanStart < 2)
            return null;

        var means = new (double R, double G, double B)[hi - lo + 2];
        for (int i = 0; i < means.Length; i++)
        {
            int p = lo - 1 + i;
            means[i] = horizontal
                ? LineMean(img, spanStart, p, spanEnd, p + 1)
                : LineMean(img, p, spanStart, p + 1, spanEnd);
        }

        double best = 0;
        int? bestLine = null;
        for (int i = 1; i < means.Length; i++)
        {
            int p = lo - 1 + i; // boundary between pixel p-1 and pixel p
            double score = Math.Abs(means[i].R - means[i - 1].R) + Math.Abs(means[i].G - means[i - 1].G) +
                           Math.Abs(means[i].B - means[i - 1].B) - Math.Abs(p - line) * 0.05;
            if (score > best)
            {
                best = score;
                bestLine = p;
            }
        }
        return best > 24 ? bestLine : null;
    }

    /// <summary>
    /// Moves one cut inward past rows or columns of flat colour (letterbox bars, page background).
    /// Returns the new line, or null if the edge isn't flat. <paramref name="edges"/> is {left, top, right, bottom}.
    /// </summary>
    public static int? ShaveFlat(Frame img, Side side, int[] edges, int minPx)
    {
        int l = edges[0], t = edges[1], r = edges[2], b = edges[3];
        bool horizontal = side is Side.Top or Side.Bottom;

        ((double R, double G, double B) Mean, double Sd) Stats(int p) =>
            horizontal ? LineStats(img, l, p, r, p + 1) : LineStats(img, p, t, p + 1, b);

        var (start, step, limit) = side switch
        {
            Side.Top => (t, 1, b - minPx),
            Side.Bottom => (b - 1, -1, t + minPx - 1),
            Side.Left => (l, 1, r - minPx),
            _ => (r - 1, -1, l + minPx - 1),
        };

        var first = Stats(start);
        if (first.Sd > 6)
            return null;
        int p = start;
        while (step > 0 ? p < limit : p > limit)
        {
            var s = Stats(p);
            double drift = Math.Abs(s.Mean.R - first.Mean.R) + Math.Abs(s.Mean.G - first.Mean.G) + Math.Abs(s.Mean.B - first.Mean.B);
            if (s.Sd > 6 || drift > 24)
                break;
            p += step;
        }
        int found = step > 0 ? p : p + 1;
        return found == (step > 0 ? start : start + 1) ? null : found;
    }

    /// <summary>"16:9" when it's close to a familiar shape, otherwise "1.333:1".</summary>
    public static string RatioLabel(int w, int h)
    {
        if (h <= 0)
            return "";
        double ratio = (double)w / h;
        foreach (var (a, b) in new[] { (16, 9), (4, 3), (21, 9), (32, 9), (3, 2), (5, 4), (1, 1), (9, 16) })
            if (Math.Abs(ratio / ((double)a / b) - 1) < 0.005)
                return $"{a}:{b}";
        return $"{ratio:0.000}:1";
    }

    static byte[] Shrink(Frame f, int w, int h)
    {
        var sums = new long[w * h];
        var counts = new int[w * h];
        byte[] px = f.Pixels;
        for (int y = 0; y < f.Height; y++)
        {
            int dy = y * h / f.Height;
            int row = y * f.Width * 4;
            for (int x = 0; x < f.Width; x++)
            {
                int i = row + x * 4;
                int luma = (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8;
                int d = dy * w + x * w / f.Width;
                sums[d] += luma;
                counts[d]++;
            }
        }
        var result = new byte[w * h];
        for (int i = 0; i < result.Length; i++)
            result[i] = counts[i] > 0 ? (byte)(sums[i] / counts[i]) : (byte)0;
        return result;
    }

    static double[] ColumnProfile(byte[] mask, int w, int rowStart, int rowEnd)
    {
        var values = new double[w];
        int rows = Math.Max(1, rowEnd - rowStart);
        for (int x = 0; x < w; x++)
        {
            long sum = 0;
            for (int y = rowStart; y < rowEnd; y++)
                sum += mask[y * w + x];
            values[x] = (double)sum / rows;
        }
        return values;
    }

    static double[] RowProfile(byte[] mask, int w, int h, int colStart, int colEnd)
    {
        var values = new double[h];
        int cols = Math.Max(1, colEnd - colStart);
        for (int y = 0; y < h; y++)
        {
            long sum = 0;
            for (int x = colStart; x < colEnd; x++)
                sum += mask[y * w + x];
            values[y] = (double)sum / cols;
        }
        return values;
    }

    static (int Start, int End)? LongestRun(double[] values, double threshold)
    {
        (int Start, int End)? best = null;
        int start = -1;
        for (int i = 0; i <= values.Length; i++)
        {
            double v = i < values.Length ? values[i] : 0;
            if (v > threshold && start < 0)
                start = i;
            else if (v <= threshold && start >= 0)
            {
                if (best is null || i - start > best.Value.End - best.Value.Start)
                    best = (start, i);
                start = -1;
            }
        }
        return best;
    }

    static (double R, double G, double B) LineMean(Frame f, int x0, int y0, int x1, int y1)
    {
        long r = 0, g = 0, b = 0, n = 0;
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(f.Width, x1);
        y1 = Math.Min(f.Height, y1);
        for (int y = y0; y < y1; y++)
        {
            int i = (y * f.Width + x0) * 4;
            for (int x = x0; x < x1; x++, i += 4)
            {
                b += f.Pixels[i];
                g += f.Pixels[i + 1];
                r += f.Pixels[i + 2];
                n++;
            }
        }
        return n == 0 ? (0, 0, 0) : ((double)r / n, (double)g / n, (double)b / n);
    }

    static ((double R, double G, double B) Mean, double Sd) LineStats(Frame f, int x0, int y0, int x1, int y1)
    {
        var mean = LineMean(f, x0, y0, x1, y1);
        double sr = 0, sg = 0, sb = 0;
        long n = 0;
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(f.Width, x1);
        y1 = Math.Min(f.Height, y1);
        for (int y = y0; y < y1; y++)
        {
            int i = (y * f.Width + x0) * 4;
            for (int x = x0; x < x1; x++, i += 4)
            {
                sb += Math.Pow(f.Pixels[i] - mean.B, 2);
                sg += Math.Pow(f.Pixels[i + 1] - mean.G, 2);
                sr += Math.Pow(f.Pixels[i + 2] - mean.R, 2);
                n++;
            }
        }
        if (n == 0)
            return (mean, 0);
        return (mean, Math.Sqrt(Math.Max(sr, Math.Max(sg, sb)) / n));
    }
}
