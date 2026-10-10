using System.Text.Json;

namespace YogaITS.Tray.Services.Elevoc;

internal static class ElevocJson
{
    public static string? Status(string json) => String(json, "status");
    public static string? Mode(string json) => String(json, "mode");
    public static bool? Bool(string json, string name) => ScalarBool(json, name);
    public static long? Long(string json, string name) => ScalarLong(json, name);

    public static HashSet<string> SupportedFunctions(string json)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement? functions = FindProperty(doc.RootElement, "supported_functions");
            if (functions is not JsonElement e || e.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement item in e.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is string s && s.Length > 0)
                    result.Add(s);
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    public static SpeakerIdSnapshot SpeakerId(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SpeakerIdSnapshot.Invalid("空响应");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string? status = FindStringProperty(doc.RootElement, "status");
            JsonElement? voiceId = FindProperty(doc.RootElement, "voiceid");

            if (voiceId is not JsonElement v || v.ValueKind != JsonValueKind.Object)
                return SpeakerIdSnapshot.Invalid($"status={status ?? "<unknown>"}, voiceid object 未找到");

            JsonElement? metadata = FindProperty(v, "metadata");
            JsonElement? version = FindProperty(v, "version");
            JsonElement? data = FindProperty(v, "data");

            string? algorithm = metadata is JsonElement m ? FindStringProperty(m, "algorithm") : null;
            string? dataType = metadata is JsonElement m2 ? FindStringProperty(m2, "data_type") : null;
            string? model = metadata is JsonElement m3 ? FindStringProperty(m3, "model_version") : null;
            int? declaredCount = metadata is JsonElement m4 ? FindIntProperty(m4, "data_count") : null;
            long? timestamp = metadata is JsonElement m5 ? FindLongProperty(m5, "timestamp") : null;

            int? major = version is JsonElement ver ? FindIntProperty(ver, "major") : null;
            int? minor = version is JsonElement ver2 ? FindIntProperty(ver2, "minor") : null;

            if (data is not JsonElement d || d.ValueKind != JsonValueKind.Array)
            {
                return new SpeakerIdSnapshot(
                    false,
                    Array.Empty<double>(),
                    algorithm,
                    model,
                    dataType,
                    declaredCount,
                    timestamp,
                    major,
                    minor,
                    $"status={status ?? "<unknown>"}; voiceid.data 未返回");
            }

            var values = new List<double>();
            foreach (JsonElement item in d.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double x) || !double.IsFinite(x))
                    return SpeakerIdSnapshot.Invalid("voiceid.data 包含非有限或非数字值");
                values.Add(x);
            }

            double l2 = Math.Sqrt(values.Sum(x => x * x));
            bool valid = values.Count == 192 && (declaredCount is null || declaredCount == 192);

            string summary =
                $"count={values.Count} (declared={declaredCount?.ToString() ?? "?"}), " +
                $"L2={l2:G9}, algorithm={algorithm ?? "?"}, model={model ?? "?"}, " +
                $"type={dataType ?? "?"}, version={major?.ToString() ?? "?"}.{minor?.ToString() ?? "?"}, " +
                $"timestamp={timestamp?.ToString() ?? "?"}";

            return new SpeakerIdSnapshot(
                valid,
                values.ToArray(),
                algorithm,
                model,
                dataType,
                declaredCount,
                timestamp,
                major,
                minor,
                summary);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return SpeakerIdSnapshot.Invalid("解析失败: " + ex.Message);
        }
    }

    public static string? String(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return FindStringProperty(doc.RootElement, name);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? ScalarBool(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement? p = FindProperty(doc.RootElement, name);
            return p?.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ScalarLong(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement? p = FindProperty(doc.RootElement, name);
            if (p is JsonElement e && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long value))
                return value;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;

                JsonElement? nested = FindProperty(property.Value, name);
                if (nested is not null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                JsonElement? nested = FindProperty(item, name);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private static string? FindStringProperty(JsonElement element, string name)
    {
        JsonElement? p = FindProperty(element, name);
        return p is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static int? FindIntProperty(JsonElement element, string name)
    {
        JsonElement? p = FindProperty(element, name);
        return p is JsonElement e && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int value)
            ? value
            : null;
    }

    private static long? FindLongProperty(JsonElement element, string name)
    {
        JsonElement? p = FindProperty(element, name);
        return p is JsonElement e && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long value)
            ? value
            : null;
    }
}

internal readonly record struct SpeakerIdSnapshot(
    bool IsValid,
    double[] Data,
    string? Algorithm,
    string? ModelVersion,
    string? DataType,
    int? DeclaredCount,
    long? Timestamp,
    int? VersionMajor,
    int? VersionMinor,
    string Summary)
{
    public static SpeakerIdSnapshot Invalid(string reason) =>
        new(false, Array.Empty<double>(), null, null, null, null, null, null, null, reason);
}
