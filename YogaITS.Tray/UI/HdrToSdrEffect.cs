using Microsoft.Graphics.Canvas.Effects;

namespace YogaITS.Tray.UI;

internal static class HdrToSdrEffect
{
    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var source = typeof(HdrToSdrEffect).Assembly.GetManifestResourceStream("YogaITS.Tray.HdrToSdr.cso")
            ?? throw new InvalidOperationException("Missing embedded HDR-to-SDR shader.");
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    });

    public static PixelShaderEffect Create() => new(Shader.Value);
}
