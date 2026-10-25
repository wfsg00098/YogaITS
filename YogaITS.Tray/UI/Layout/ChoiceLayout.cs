namespace YogaITS.Tray.UI.Layout;

internal static class ChoiceLayout
{
    public static (int Start, int Count)[] Rows(double width, IReadOnlyList<double> sizes, double gap = 6)
    {
        width = double.IsFinite(width) && width > 0 ? width : 1;
        var rows = new List<(int, int)>();
        int start = 0;
        double used = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            double size = Math.Max(1, sizes[i]);
            if (i > start && used + gap + size > width)
            {
                rows.Add((start, i - start));
                start = i;
                used = 0;
            }
            used += (i > start ? gap : 0) + size;
        }
        if (start < sizes.Count) rows.Add((start, sizes.Count - start));
        return rows.ToArray();
    }

    public static double[] Fill(double width, IReadOnlyList<double> sizes, int start, int count, double gap = 6)
    {
        double extra = (width - gap * (count - 1) - Enumerable.Range(start, count).Sum(i => sizes[i])) / count;
        return Enumerable.Range(start, count).Select(i => Math.Max(1, sizes[i] + extra)).ToArray();
    }
}
