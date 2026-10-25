using YogaITS.Tray.Services;

internal static class BackgroundImageChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAILED image import check: " + message);
            checks++;
        }
        string temporary = Path.Combine(Path.GetTempPath(), "YogaITS-Images-" + Guid.NewGuid().ToString("N"));
        string originals = Path.Combine(temporary, "originals");
        string managed = Path.Combine(temporary, "data", "Backgrounds");
        Directory.CreateDirectory(originals);
        var store = new BackgroundImageStore(managed);
        try
        {
            string original = Path.Combine(originals, "背景.PNG");
            byte[] content = Enumerable.Range(0, 250000).Select(index => (byte)index).ToArray();
            await File.WriteAllBytesAsync(original, content);
            string imported = await store.ImportAsync(original);
            Check(imported != original && store.Contains(imported), "Selection copies into the data directory.");
            Check(Path.GetFileName(imported) == "背景.PNG", "Original display filename is preserved.");
            Check(File.ReadAllBytes(imported).SequenceEqual(content), "Copied image matches every source byte.");
            File.Delete(original);
            Check(File.ReadAllBytes(imported).SequenceEqual(content), "Deleting the original does not break the managed copy.");
            Check(await store.ImportAsync(imported) == imported, "Restoring an existing managed image avoids another copy.");
            Check(Directory.GetFiles(managed, "*", SearchOption.AllDirectories).Length == 1, "Repeated restore does not duplicate images.");

            await File.WriteAllBytesAsync(original, [1, 2, 3]);
            string second = await store.ImportAsync(original);
            Check(second != imported && File.ReadAllBytes(second).SequenceEqual(new byte[] { 1, 2, 3 }),
                "Selecting another image with the same filename creates a separate completed copy.");
            Check(File.ReadAllBytes(imported).SequenceEqual(content), "In-flight rendering can keep reading the previous image.");
            Check(!store.Contains(Path.Combine(managed, "..", "outside.png")) &&
                !store.Contains(managed + "-other/image.png"), "Sibling and parent paths are not managed images.");
            string unsupported = Path.Combine(originals, "other.txt");
            File.WriteAllText(unsupported, "unchanged");
            bool rejected = false;
            try { await store.ImportAsync(unsupported); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(unsupported) == "unchanged", "Unsupported file types leave the source untouched.");
            rejected = false;
            try { await store.ImportAsync(Path.Combine(originals, "missing.png")); } catch (FileNotFoundException) { rejected = true; }
            Check(rejected, "Missing images fail without publishing a partial path.");
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            bool canceled = false;
            try { await store.ImportAsync(original, stop.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Canceled imports do not create a background copy.");
            using (var locked = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                rejected = false;
                try { await store.ImportAsync(original); } catch (IOException) { rejected = true; }
                Check(rejected, "A source that cannot be read fails without replacing the current image.");
            }
            Check(Directory.GetFiles(managed, "*", SearchOption.AllDirectories).Length == 2 &&
                !Directory.EnumerateFiles(managed, "*.tmp", SearchOption.AllDirectories).Any(),
                "Failures and cancellations leave no temporary or extra published images.");
        }
        finally { Directory.Delete(temporary, recursive: true); }
        return checks;
    }
}
