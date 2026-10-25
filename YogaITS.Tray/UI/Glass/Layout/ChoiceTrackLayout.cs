using YogaITS.Tray.UI.Layout;

namespace YogaITS.Tray.UI.Glass.Layout;

internal readonly record struct TabFrame(float X, float Y, float Width, float Height)
{
    public float Center => X + Width / 2;
    public float CenterY => Y + Height / 2;
}

internal sealed record ChoiceTrackLayout(TabFrame Track, TabFrame[] Items, int Columns = 4, int[]? RowStarts = null)
{
    public const int Count = 4;
    public int Rows => RowStarts?.Length ?? (Items.Length + Columns - 1) / Columns;
    public TabFrame Capsule(int index) => Items[Math.Clamp(index, 0, Items.Length - 1)];
    public static double NaturalHeight(int count, int columns) => 80 + 60 * ((count - 1) / columns);

    public static ChoiceTrackLayout Create(double width, double height, int count = Count, int columns = 0)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        columns = columns == 0 ? count : columns;
        if (columns < 1 || columns > count) throw new ArgumentOutOfRangeException(nameof(columns));
        float w = double.IsFinite(width) ? (float)Math.Clamp(width, 80, 32768) : 240;
        int rows = (count + columns - 1) / columns;
        float trackHeight = rows * 60;
        float h = double.IsFinite(height) ? (float)Math.Clamp(height, trackHeight, 2048) : trackHeight + 20;
        const float padding = 6, spacing = 2;
        float cell = (w - 2 * padding - (columns - 1) * spacing) / columns;
        float top = (h - trackHeight) / 2;
        var frames = new TabFrame[count];
        for (int i = 0; i < count; i++)
        {
            bool fullLastRow = count % columns == 1 && i == count - 1;
            frames[i] = new(padding + i % columns * (cell + spacing), top + padding + i / columns * 60,
                fullLastRow ? w - 2 * padding : cell, 48);
        }
        return new(new(0, top, w, trackHeight), frames, columns);
    }

    public int Hit(double x) => Hit(x, Items[0].CenterY);

    public static ChoiceTrackLayout CreateMeasured(double width, IReadOnlyList<double> sizes)
    {
        width = double.IsFinite(width) && width > 12 ? width : 600;
        double available = Math.Max(1, width - 12);
        var rows = ChoiceLayout.Rows(available, sizes);
        var items = new TabFrame[sizes.Count];
        for (int row = 0; row < rows.Length; row++)
        {
            var (start, count) = rows[row];
            var widths = ChoiceLayout.Fill(available, sizes, start, count);
            double x = 6;
            for (int column = 0; column < count; column++)
            {
                items[start + column] = new((float)x, 16 + row * 60, (float)widths[column], 48);
                x += widths[column] + 6;
            }
        }
        return new(new(0, 10, (float)width, rows.Length * 60), items,
            rows.Max(row => row.Count), rows.Select(row => row.Start).ToArray());
    }
    public int Hit(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        int nearest = 0;
        double distance = double.MaxValue, centerDistance = double.MaxValue;
        for (int i = 0; i < Items.Length; i++)
        {
            var item = Items[i];
            double dx = Math.Max(Math.Max(item.X - x, x - item.X - item.Width), 0);
            double dy = Math.Max(Math.Max(item.Y - y, y - item.Y - item.Height), 0);
            double next = dx * dx + dy * dy;
            double center = Math.Pow(x - item.Center, 2) + Math.Pow(y - item.CenterY, 2);
            if (next < distance || (next == distance && center < centerDistance))
            { distance = next; centerDistance = center; nearest = i; }
        }
        return nearest;
    }

    public int VerticalNeighbor(int index, int direction)
    {
        index = Math.Clamp(index, 0, Items.Length - 1);
        if (RowStarts is { } starts)
        {
            int current = Array.FindLastIndex(starts, start => start <= index);
            int target = Math.Clamp(current + Math.Sign(direction), 0, starts.Length - 1);
            if (current == target) return index;
            int end = target + 1 < starts.Length ? starts[target + 1] : Items.Length;
            return Enumerable.Range(starts[target], end - starts[target])
                .MinBy(candidate => Math.Abs(Items[candidate].Center - Items[index].Center));
        }
        int row = Math.Clamp(index / Columns + Math.Sign(direction), 0, Rows - 1);
        if (row == index / Columns) return index;
        return Enumerable.Range(row * Columns, Math.Min(Columns, Items.Length - row * Columns))
            .MinBy(candidate => Math.Abs(Items[candidate].Center - Items[index].Center));
    }
}
