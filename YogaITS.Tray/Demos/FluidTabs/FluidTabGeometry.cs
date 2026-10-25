namespace YogaITS.Tray.Demos.FluidTabs;

// All input/layout values are DIPs; Composition performs final DPI rasterization.
internal readonly record struct FluidTabGeometry(float Width, float Height)
{
    public const int Count = 4;
    public const float Padding = 4;
    public float Available => Math.Max(4, Width - 2 * Padding);
    public float Cell => Available / Count;
    public float PillHeight => Math.Max(1, Math.Min(Height - 2 * Padding, Cell));
    public static FluidTabGeometry Create(double width, double height) => new(
        double.IsFinite(width) ? (float)Math.Clamp(width, 12, 32768) : 12,
        double.IsFinite(height) ? (float)Math.Clamp(height, 10, 1024) : 10);
    public float Left(int index) => Padding + Math.Clamp(index, 0, Count - 1) * Cell;
    public float DragLeft(double x) => !double.IsFinite(x) ? Left(0) :
        (float)Math.Clamp(x - Cell / 2, Padding, Padding + Available - Cell);
    public int IndexAt(double x) => !double.IsFinite(x) ? 0 :
        (int)Math.Clamp(Math.Floor((x - Padding) / Cell), 0, Count - 1);
    public static int Next(int selected, int delta) => (Math.Clamp(selected, 0, Count-1) + delta % Count + Count) % Count;
}
