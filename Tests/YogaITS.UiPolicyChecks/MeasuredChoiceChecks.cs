using YogaITS.Tray.UI;
using YogaITS.Tray.Demos.SwiftTabs;

internal static class MeasuredChoiceChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Measured color layout: " + message);
            checks++;
        }
        foreach (int count in new[] { 1, 7, 9, 17, 40 })
        foreach (double width in new[] { 180.0, 320, 460, 800 })
        {
            double[] sizes = Enumerable.Range(0, count).Select(i => 60.0 + i % 5 * 31).ToArray();
            var layout = SwiftTabLayout.CreateMeasured(width, sizes);
            var rows = ChoiceLayout.Rows(width - 12, sizes);
            Check(layout.Items.Length == count && layout.Rows == rows.Length, "Every discovered mode has a layout slot without a fixed count limit.");
            for (int row = 0; row < rows.Length; row++)
            {
                var (start, length) = rows[row];
                Check(layout.Items[start].X == 6 && Math.Abs(layout.Items[start + length - 1].X +
                    layout.Items[start + length - 1].Width - (width - 6)) < 0.001,
                    "Every row, including the final row, fills the complete track width.");
                for (int i = start; i < start + length; i++)
                {
                    var item = layout.Items[i];
                    Check(length == 1 || item.Width + 0.001 >= sizes[i], "Packing never squeezes a label below its measured width.");
                    Check(layout.Hit(item.Center, item.CenterY) == i, "Variable row hit testing keeps the original mode order.");
                    if (i + 1 < start + length)
                        Check(Math.Abs(layout.Items[i + 1].X - item.X - item.Width - 6) < 0.001, "Rows use consistent spacing.");
                    foreach (int direction in new[] { -1, 1 })
                    {
                        int neighbor = layout.VerticalNeighbor(i, direction);
                        int expected = Math.Clamp(row + direction, 0, rows.Length - 1);
                        Check(layout.Items[neighbor].Y == layout.Items[rows[expected].Start].Y,
                            "Keyboard navigation uses real rows rather than a fixed column count.");
                    }
                }
            }
            var selection = new OptionSelectionState(count);
            Check(selection.Request(count - 1) == (count > 1), "Future larger option sets preserve confirmed/requested semantics.");
        }
        Check(ChoiceLayout.Rows(200, new double[] { 100, 100 }).Length == 2,
            "Spacing is included when deciding whether labels would wrap.");
        return checks;
    }
}
