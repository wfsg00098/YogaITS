using System.Text.Json;

namespace YogaITS.Tray.Services;

internal static class UserSettingsStore
{
    private static readonly object Gate = new();
    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YogaITS");
    private static readonly string FilePath = Path.Combine(DataDirectory, "settings.json");

    private static Dictionary<string, JsonElement> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public static T Get<T>(string key, T fallback)
    {
        lock (Gate)
        {
            try { return Load().TryGetValue(key, out var value) ? value.Deserialize<T>() ?? fallback : fallback; }
            catch (JsonException) { return fallback; }
        }
    }

    public static void Set<T>(string key, T value)
    {
        lock (Gate)
        {
            var settings = Load();
            settings[key] = JsonSerializer.SerializeToElement(value);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, FilePath, overwrite: true);
        }
    }
}
