using System;
using System.Collections.Generic;
using System.Linq;

namespace Spread;

// Line-by-line port of GNOME Shell's window-overview layout (js/ui/workspace.js:
// UnalignedLayoutStrategy + WorkspaceLayout._createBestLayout / _isBetterScaleAndSpace).
// Keep this faithful: quirks (e.g. row.fullHeight being updated before a window is rejected) are intentional.

internal readonly record struct LBox(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

internal readonly record struct LArea(double X, double Y, double Width, double Height);

/// <summary>A window slot. Index refers to the position in the input list.</summary>
internal readonly record struct LSlot(double X, double Y, double Width, double Height, int Index, int Row, int Col);

internal sealed class GnomeLayout
{
    public const double WindowPreviewMaximumScale = 0.95;
    public const double LayoutScaleWeight = 1;
    public const double LayoutSpaceWeight = 0.1;

    readonly double _monitorHeight, _rowSpacing, _columnSpacing;

    sealed class Row
    {
        public double X, Y, Width, Height, FullWidth, FullHeight, AdditionalScale;
        public readonly List<int> Windows = new();
    }

    sealed class LayoutResult
    {
        public int NumRows, MaxColumns;
        public double GridWidth, GridHeight, Scale;
        public List<Row> Rows = new();
    }

    public GnomeLayout(double monitorHeight, double rowSpacing, double columnSpacing)
    {
        _monitorHeight = monitorHeight; _rowSpacing = rowSpacing; _columnSpacing = columnSpacing;
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    double ComputeWindowScale(LBox w) => Lerp(1.5, 1, w.Height / _monitorHeight);

    static bool IsBetterScaleAndSpace(double oldScale, double oldSpace, double scale, double space)
    {
        double spacePower = (space - oldSpace) * LayoutSpaceWeight;
        double scalePower = (scale - oldScale) * LayoutScaleWeight;
        if (scale > oldScale && space > oldSpace) return true;
        if (scale > oldScale && space <= oldSpace) return scalePower > spacePower;
        if (scale <= oldScale && space > oldSpace) return spacePower > scalePower;
        return false;
    }

    /// <summary>Slots are returned in the input order.</summary>
    public LSlot[] Compute(IReadOnlyList<LBox> windows, LArea areaIn)
    {
        int n = windows.Count;
        if (n == 0) return Array.Empty<LSlot>();
        // parseInt() on the container box in the original
        var area = new LArea(Math.Truncate(areaIn.X), Math.Truncate(areaIn.Y), Math.Truncate(areaIn.Width), Math.Truncate(areaIn.Height));

        LayoutResult? last = null;
        int lastNumColumns = -1;
        double lastScale = 0, lastSpace = 0;
        for (int numRows = 1; ; numRows++)
        {
            int numColumns = (int)Math.Ceiling(n / (double)numRows);
            if (numColumns == lastNumColumns) break;

            var layout = ComputeLayout(windows, numRows);
            var (scale, space) = ComputeScaleAndSpace(layout, area);
            if (last != null && !IsBetterScaleAndSpace(lastScale, lastSpace, scale, space)) break;

            last = layout; lastNumColumns = numColumns; lastScale = scale; lastSpace = space;
        }

        var slots = ComputeWindowSlots(windows, last!, area);
        var ordered = new LSlot[n];
        foreach (var s in slots) ordered[s.Index] = s;
        return ordered;
    }

    static Row NewRow() => new();

    static bool KeepSameRow(Row row, double width, double idealRowWidth)
    {
        if (row.FullWidth + width <= idealRowWidth) return true;
        double oldRatio = row.FullWidth / idealRowWidth;
        double newRatio = (row.FullWidth + width) / idealRowWidth;
        return Math.Abs(1 - newRatio) < Math.Abs(1 - oldRatio);
    }

    LayoutResult ComputeLayout(IReadOnlyList<LBox> windows, int numRows)
    {
        var rows = new List<Row>();
        double totalWidth = 0;
        for (int i = 0; i < windows.Count; i++)
            totalWidth += windows[i].Width * ComputeWindowScale(windows[i]);
        double idealRowWidth = totalWidth / numRows;

        // stable sort by vertical centre (JS Array.sort is stable)
        var sorted = Enumerable.Range(0, windows.Count).OrderBy(i => windows[i].CenterY).ToList();

        int windowIdx = 0;
        for (int i = 0; i < numRows; i++)
        {
            var row = NewRow();
            rows.Add(row);
            for (; windowIdx < sorted.Count; windowIdx++)
            {
                int wi = sorted[windowIdx];
                var w = windows[wi];
                double s = ComputeWindowScale(w);
                double width = w.Width * s, height = w.Height * s;
                row.FullHeight = Math.Max(row.FullHeight, height);
                if (KeepSameRow(row, width, idealRowWidth) || i == numRows - 1)
                {
                    row.Windows.Add(wi);
                    row.FullWidth += width;
                }
                else break;
            }
        }

        double gridHeight = 0;
        Row? maxRow = null;
        for (int i = 0; i < numRows; i++)
        {
            var row = rows[i];
            var byX = row.Windows.OrderBy(idx => windows[idx].CenterX).ToList();
            row.Windows.Clear(); row.Windows.AddRange(byX);
            if (maxRow == null || row.FullWidth > maxRow.FullWidth) maxRow = row;
            gridHeight += row.FullHeight;
        }

        return new LayoutResult
        {
            NumRows = numRows, Rows = rows, MaxColumns = maxRow!.Windows.Count,
            GridWidth = maxRow.FullWidth, GridHeight = gridHeight
        };
    }

    (double scale, double space) ComputeScaleAndSpace(LayoutResult layout, LArea area)
    {
        double hspacing = (layout.MaxColumns - 1) * _columnSpacing;
        double vspacing = (layout.NumRows - 1) * _rowSpacing;
        double spacedWidth = area.Width - hspacing;
        double spacedHeight = area.Height - vspacing;
        double horizontalScale = spacedWidth / layout.GridWidth;
        double verticalScale = spacedHeight / layout.GridHeight;
        double scale = Math.Min(Math.Min(horizontalScale, verticalScale), WindowPreviewMaximumScale);
        double scaledLayoutWidth = layout.GridWidth * scale + hspacing;
        double scaledLayoutHeight = layout.GridHeight * scale + vspacing;
        double space = (scaledLayoutWidth * scaledLayoutHeight) / (area.Width * area.Height);
        layout.Scale = scale;
        return (scale, space);
    }

    List<LSlot> ComputeWindowSlots(IReadOnlyList<LBox> windows, LayoutResult layout, LArea area)
    {
        // _computeRowSizes
        double scale = layout.Scale;
        var rows = layout.Rows;
        foreach (var row in rows)
        {
            row.Width = row.FullWidth * scale + (row.Windows.Count - 1) * _columnSpacing;
            row.Height = row.FullHeight * scale;
        }

        var slots = new List<LSlot>();
        double heightWithoutSpacing = 0;
        foreach (var row in rows) heightWithoutSpacing += row.Height;

        double verticalSpacing = (rows.Count - 1) * _rowSpacing;
        double additionalVerticalScale = Math.Min(1, (area.Height - verticalSpacing) / heightWithoutSpacing);

        double compensation = 0, y = 0;
        foreach (var row in rows)
        {
            double horizontalSpacing = (row.Windows.Count - 1) * _columnSpacing;
            double widthWithoutSpacing = row.Width - horizontalSpacing;
            double additionalHorizontalScale = Math.Min(1, (area.Width - horizontalSpacing) / widthWithoutSpacing);

            if (additionalHorizontalScale < additionalVerticalScale)
            {
                row.AdditionalScale = additionalHorizontalScale;
                compensation += (additionalVerticalScale - additionalHorizontalScale) * row.Height;
            }
            else row.AdditionalScale = additionalVerticalScale;

            row.X = area.X + Math.Max(area.Width - (widthWithoutSpacing * row.AdditionalScale + horizontalSpacing), 0) / 2;
            row.Y = area.Y + Math.Max(area.Height - (heightWithoutSpacing + verticalSpacing), 0) / 2 + y;
            y += row.Height * row.AdditionalScale + _rowSpacing;
        }
        compensation /= 2;

        for (int ri = 0; ri < rows.Count; ri++)
        {
            var row = rows[ri];
            double rowY = row.Y + compensation;
            double rowHeight = row.Height * row.AdditionalScale;
            double x = row.X;
            for (int ci = 0; ci < row.Windows.Count; ci++)
            {
                int wi = row.Windows[ci];
                var w = windows[wi];
                double s = scale * ComputeWindowScale(w) * row.AdditionalScale;
                double cellWidth = w.Width * s, cellHeight = w.Height * s;
                s = Math.Min(s, WindowPreviewMaximumScale);
                double cloneWidth = w.Width * s, cloneHeight = w.Height * s;

                double cloneX = x + (cellWidth - cloneWidth) / 2;
                double cloneY = rows.Count == 1 ? rowY + (rowHeight - cloneHeight) / 2 : rowY + rowHeight - cellHeight;

                slots.Add(new LSlot(Math.Floor(cloneX), Math.Floor(cloneY), cloneWidth, cloneHeight, wi, ri, ci));
                x += cellWidth + _columnSpacing;
            }
        }
        return slots;
    }
}

/// <summary>
/// "Natural" layout: windows keep their real relative positions on the desktop (left stays left, top stays top)
/// but are scaled down and pushed apart until nothing overlaps. The largest scale that still fits the area wins.
/// Each window is treated as its frame plus GNOME's chrome (close button above, icon + title below) and a gap,
/// so icons and titles never collide with neighbours.
/// </summary>
internal sealed class NaturalLayout
{
    readonly double _monW, _monH, _top, _bottom, _side, _gap, _maxScale, _minLong;
    /// <summary>Scale chosen by the last Compute call (for diagnostics/tests).</summary>
    public double LastScale { get; private set; }
    public bool LastFeasible { get; private set; }

    public NaturalLayout(double monitorW, double monitorH, double marginTop, double marginBottom, double marginSide,
                         double gap, double maxScale, double minLong)
    {
        _monW = monitorW; _monH = monitorH; _top = marginTop; _bottom = marginBottom; _side = marginSide;
        _gap = gap; _maxScale = maxScale; _minLong = minLong;
    }

    public LSlot[] Compute(IReadOnlyList<LBox> windows, LArea area)
    {
        int n = windows.Count;
        if (n == 0) return Array.Empty<LSlot>();

        // Anchors: the real desktop positions mapped affinely into the work area.
        double kx = area.Width / _monW, ky = area.Height / _monH;
        var ax = new double[n]; var ay = new double[n];
        for (int i = 0; i < n; i++) { ax[i] = area.X + windows[i].CenterX * kx; ay[i] = area.Y + windows[i].CenterY * ky; }

        var px = new double[n]; var py = new double[n]; var ew = new double[n]; var eh = new double[n];
        double lo = 0.04, hi = _maxScale, best = -1;
        bool feasible = true;
        var bx = new double[n]; var by = new double[n];

        if (Solve(windows, area, ax, ay, hi, 160, px, py, ew, eh)) { best = hi; Array.Copy(px, bx, n); Array.Copy(py, by, n); }
        else
        {
            for (int it = 0; it < 9; it++)
            {
                double mid = (lo + hi) / 2;
                if (Solve(windows, area, ax, ay, mid, 160, px, py, ew, eh)) { best = mid; lo = mid; Array.Copy(px, bx, n); Array.Copy(py, by, n); }
                else hi = mid;
            }
            if (best < 0) { best = lo; feasible = Solve(windows, area, ax, ay, best, 300, bx, by, ew, eh); }
        }
        // final pass at the chosen scale (also fills ew/eh for it)
        Array.Copy(bx, px, n); Array.Copy(by, py, n);
        Solve(windows, area, ax, ay, best, 120, px, py, ew, eh, keepPositions: true);
        LastScale = best; LastFeasible = feasible;

        var slots = new LSlot[n];
        for (int i = 0; i < n; i++)
            slots[i] = new LSlot(Math.Floor(px[i] - ew[i] / 2), Math.Floor(py[i] - eh[i] / 2), ew[i], eh[i], i, 0, 0);
        return slots;
    }

    /// <summary>Relaxes positions for one scale; true when no boxes overlap and everything fits inside the area.</summary>
    bool Solve(IReadOnlyList<LBox> w, LArea a, double[] ax, double[] ay, double s, int maxIter,
               double[] px, double[] py, double[] ew, double[] eh, bool keepPositions = false)
    {
        int n = w.Count;
        for (int i = 0; i < n; i++)
        {
            double lng = Math.Max(w[i].Width, w[i].Height) * s;
            double m = lng <= 0 ? 1 : Math.Clamp(_minLong / lng, 1, 1.5);   // small windows are enlarged a little, like GNOME
            ew[i] = w[i].Width * s * m; eh[i] = w[i].Height * s * m;
            if (!keepPositions) { px[i] = ax[i]; py[i] = ay[i]; }
        }

        double hs = _side + _gap / 2, hv = _gap / 2;
        for (int iter = 0; iter < maxIter; iter++)
        {
            double moved = 0; bool any = false;
            for (int i = 0; i < n; i++)
            {
                double li = px[i] - ew[i] / 2 - hs, ri = px[i] + ew[i] / 2 + hs, ti = py[i] - eh[i] / 2 - _top - hv, bi = py[i] + eh[i] / 2 + _bottom + hv;
                for (int j = i + 1; j < n; j++)
                {
                    double lj = px[j] - ew[j] / 2 - hs, rj = px[j] + ew[j] / 2 + hs, tj = py[j] - eh[j] / 2 - _top - hv, bj = py[j] + eh[j] / 2 + _bottom + hv;
                    double ox = Math.Min(ri, rj) - Math.Max(li, lj), oy = Math.Min(bi, bj) - Math.Max(ti, tj);
                    if (ox <= 0.25 || oy <= 0.25) continue;
                    any = true;
                    double dx = (lj + rj) / 2 - (li + ri) / 2, dy = (tj + bj) / 2 - (ti + bi) / 2, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-3) { double ang = i * 2.39996 + j * 1.2345; dx = Math.Cos(ang); dy = Math.Sin(ang); len = 1; }
                    dx /= len; dy /= len;
                    double tx = Math.Abs(dx) > 1e-6 ? ox / Math.Abs(dx) : double.MaxValue;
                    double ty = Math.Abs(dy) > 1e-6 ? oy / Math.Abs(dy) : double.MaxValue;
                    double mv = 0.5 * (Math.Min(tx, ty) + 0.5) * 0.85;
                    px[i] -= dx * mv; py[i] -= dy * mv; px[j] += dx * mv; py[j] += dy * mv;
                    moved += 2 * mv;
                    li = px[i] - ew[i] / 2 - hs; ri = px[i] + ew[i] / 2 + hs; ti = py[i] - eh[i] / 2 - _top - hv; bi = py[i] + eh[i] / 2 + _bottom + hv;
                }
            }
            // The spring keeps windows near where they really are, but it fades out over the first 60% of the
            // iterations so the last ones are push-only and overlaps fully resolve.
            double springEnd = keepPositions ? 0 : maxIter * 0.6;   // the final polish pass is push-only
            double springK = iter < springEnd ? 0.02 * (1 - iter / springEnd) : 0;
            for (int i = 0; i < n; i++)
            {
                double sx = (ax[i] - px[i]) * springK, sy = (ay[i] - py[i]) * springK;
                px[i] += sx; py[i] += sy; moved += Math.Abs(sx) + Math.Abs(sy);
                // keep the whole box (frame + chrome) inside the area
                double l = px[i] - ew[i] / 2 - hs, r = px[i] + ew[i] / 2 + hs, t = py[i] - eh[i] / 2 - _top - hv, b = py[i] + eh[i] / 2 + _bottom + hv;
                if (r - l >= a.Width) px[i] = a.X + a.Width / 2; else if (l < a.X) px[i] += a.X - l; else if (r > a.X + a.Width) px[i] -= r - (a.X + a.Width);
                if (b - t >= a.Height) py[i] = a.Y + a.Height / 2; else if (t < a.Y) py[i] += a.Y - t; else if (b > a.Y + a.Height) py[i] -= b - (a.Y + a.Height);
            }
            if (!any && iter >= springEnd) break;      // settled: push-only phase found no overlap
            if (iter >= springEnd && moved < 0.01 * n) break;
        }

        // feasibility: no overlap deeper than 1px, and everything inside the area
        for (int i = 0; i < n; i++)
        {
            double li = px[i] - ew[i] / 2 - hs, ri = px[i] + ew[i] / 2 + hs, ti = py[i] - eh[i] / 2 - _top - hv, bi = py[i] + eh[i] / 2 + _bottom + hv;
            if (li < a.X - 1 || ri > a.X + a.Width + 1 || ti < a.Y - 1 || bi > a.Y + a.Height + 1) return false;
            for (int j = i + 1; j < n; j++)
            {
                double lj = px[j] - ew[j] / 2 - hs, rj = px[j] + ew[j] / 2 + hs, tj = py[j] - eh[j] / 2 - _top - hv, bj = py[j] + eh[j] / 2 + _bottom + hv;
                if (Math.Min(ri, rj) - Math.Max(li, lj) > 1 && Math.Min(bi, bj) - Math.Max(ti, tj) > 1) return false;
            }
        }
        return true;
    }
}


/// <summary>
/// Geometry of a "photo stack": windows of one app folded into a single layout box. The card at ring depth d from the front is
/// drawn at its own size, top-left anchored d steps up and to the left of the front card, so a strip of every lower card sticks out.
/// The box does not depend on which card is in front (scrolling never reshuffles the rest of the overview).
/// </summary>
internal static class StackLayout
{
    public static int Visible(int n, int visible) => Math.Max(1, Math.Min(Math.Max(1, visible), n));

    public static double Offset(IReadOnlyList<(double w, double h)> sizes, double offsetPct)
    {
        double longest = 0;
        foreach (var s in sizes) longest = Math.Max(longest, Math.Max(s.w, s.h));
        return Math.Max(8, offsetPct / 100.0 * longest);
    }

    public static int Depth(int index, int front, int n) => ((index - front) % n + n) % n;

    /// <summary>Card rectangles relative to the box (same order as <paramref name="sizes"/>) and the box size. Cards deeper than the visible count share the deepest position.</summary>
    public static LBox[] Cards(IReadOnlyList<(double w, double h)> sizes, int front, int visible, double offsetPct, out double boxW, out double boxH)
    {
        int n = sizes.Count, v = Visible(n, visible);
        double off = Offset(sizes, offsetPct), maxW = 0, maxH = 0;
        foreach (var s in sizes) { maxW = Math.Max(maxW, s.w); maxH = Math.Max(maxH, s.h); }
        boxW = maxW + (v - 1) * off; boxH = maxH + (v - 1) * off;
        var res = new LBox[n];
        for (int j = 0; j < n; j++)
        {
            int d = Math.Min(Depth(j, front, n), v - 1);
            double pos = (v - 1 - d) * off;
            res[j] = new LBox(pos, pos, sizes[j].w, sizes[j].h);
        }
        return res;
    }
}
