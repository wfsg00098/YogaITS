namespace YogaITS.Tray.UI;

internal readonly record struct CaptureFrameRates(double Published, double Copied, double Drawn);

// Called by the existing diagnostics refresh, not a render/polling timer.
// Rates use elapsed monotonic time rather than assuming exactly one-second ticks.
internal sealed class CaptureFrameRateMeter
{
    private double _time = double.NaN;
    private long _published, _copied, _drawn;
    private CaptureFrameRates _rates;

    public CaptureFrameRates Sample(double nowSeconds, long published, long copied, long drawn)
    {
        if (!double.IsFinite(nowSeconds)) return _rates;
        if (double.IsFinite(_time) && nowSeconds == _time) return _rates;
        double elapsed = nowSeconds - _time;
        _rates = elapsed > 0 && published >= _published && copied >= _copied && drawn >= _drawn
            ? new((published - _published) / elapsed, (copied - _copied) / elapsed, (drawn - _drawn) / elapsed)
            : default;
        _time = nowSeconds; _published = published; _copied = copied; _drawn = drawn;
        return _rates;
    }
}