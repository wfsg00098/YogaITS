using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YogaITS.Tray.Services.Updates;

internal static class UpdateManifestChecks
{
    public static int Run()
    {
        int checks = 0;
        string root = Path.Combine(Path.GetTempPath(), "YogaITS-Manifest-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Update SHA-256: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        string manifest = Path.Combine(source, "SHA256SUMS.txt");
        string file = Path.Combine(source, "背景 image.png");
        const string row = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  背景 image.png\n";
        try
        {
            File.WriteAllText(file, "abc", new UTF8Encoding(false));
            UpdatePackage.VerifyFileChecksums(source, required: false);
            checks++;
            Reject(() => UpdatePackage.VerifyFileChecksums(source, required: true), "New packages cannot omit the list.");
            foreach (string valid in new[] { row, row.ToUpperInvariant().Replace("IMAGE.PNG", "image.png"), row.Replace("\n", "\r\n") })
            {
                File.WriteAllText(manifest, valid);
                UpdatePackage.VerifyFileChecksums(source, required: true);
                checks++;
            }
            foreach (string invalid in new[] { "", row + row, row + row.Replace("image.png", "IMAGE.PNG"),
                row.Replace("  背景", " 背景"), row.Replace(row[..64], new string('g', 64)),
                row.Replace("背景 image.png", "../escape"), row.Replace("背景 image.png", "C:/escape"),
                row.Replace("背景 image.png", "SHA256SUMS.txt"), row.Replace("背景 image.png", "missing.png"),
                row.Replace(row[..64], new string('0', 64)) })
            {
                File.WriteAllText(manifest, invalid);
                Reject(() => UpdatePackage.VerifyFileChecksums(source, required: true), "Malformed, unsafe, duplicate, missing, or wrong hash rejected.");
            }
            File.WriteAllText(manifest, row);
            File.WriteAllText(file, "abd");
            Reject(() => UpdatePackage.VerifyFileChecksums(source, required: true), "Same-sized modified bytes fail validation.");
            File.WriteAllText(file, "abc");
            File.WriteAllText(Path.Combine(source, "extra.dll"), "unexpected");
            Reject(() => UpdatePackage.VerifyFileChecksums(source, required: true), "Unlisted files cannot bypass complete coverage.");
            File.Delete(Path.Combine(source, "extra.dll"));

            // Exercise the same extraction boundary used by the real updater.
            foreach (string required in UpdatePackage.RequiredFiles)
                File.WriteAllText(Path.Combine(source, required), "fixture " + required);
            File.WriteAllText(Path.Combine(source, "build-flavor.json"), JsonSerializer.Serialize(new
            { flavor = "native", executable = "YogaITS.Tray.exe", checksumManifest = "SHA256SUMS.txt" }));
            void WriteManifest() => File.WriteAllLines(manifest, Directory.EnumerateFiles(source)
                .Where(path => path != manifest).Select(path =>
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + "  " + Path.GetFileName(path)));
            int attempt = 0;
            void Extract()
            {
                string archive = Path.Combine(root, $"{++attempt}.zip");
                ZipFile.CreateFromDirectory(source, archive);
                // Even an archive that matches the Release digest must pass its file list.
                UpdatePackage.ExtractAndVerify(archive, Path.Combine(root, $"extracted-{attempt}"),
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))));
            }
            WriteManifest();
            Extract();
            checks++;
            File.WriteAllText(Path.Combine(source, "YogaITS.Tray.dll"), "tampered");
            Reject(Extract, "ZIP digest success cannot hide a per-file checksum failure.");
            WriteManifest();
            File.Delete(manifest);
            Reject(Extract, "A new build marker requires its declared list during extraction.");
        }
        finally { Directory.Delete(root, recursive: true); }
        return checks;
    }
}
