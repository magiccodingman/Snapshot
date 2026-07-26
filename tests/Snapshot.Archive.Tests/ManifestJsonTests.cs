using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snapshot.Protocol.Archive;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Manifest;
using Xunit;

namespace Snapshot.Archive.Tests;

public sealed class ManifestJsonTests
{
    [Fact]
    public async Task Web_json_manifest_round_trips_and_validates_every_archive_entry()
    {
        var path = Path.Combine(Path.GetTempPath(), "snapshot-manifest-tests", $"{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            var indexBytes = Encoding.UTF8.GetBytes("<!doctype html><title>Manifest round trip</title>");
            var assetBytes = Encoding.UTF8.GetBytes("manifested asset");
            var manifest = new SnapshotManifest
            {
                SourceFileCount = 2,
                Entries =
                [
                    CreateEntry("index.html", indexBytes),
                    CreateEntry("assets/example.txt", assetBytes)
                ]
            };

            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteEntryAsync(zip, "index.html", indexBytes);
                await WriteEntryAsync(zip, "assets/example.txt", assetBytes);

                var manifestEntry = zip.CreateEntry("snapshot-manifest.json", CompressionLevel.SmallestSize);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(
                    manifestStream,
                    manifest,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            }

            await using var archive = await SnapshotArchive.OpenAsync(path);
            var manifestJson = await archive.ReadTextAsync("snapshot-manifest.json");
            Assert.Contains("\"entries\"", manifestJson, StringComparison.Ordinal);

            var reloaded = await archive.TryReadManifestAsync();
            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded.Entries.Count);
            Assert.Contains(reloaded.Entries, entry => entry.Path == "index.html");
            Assert.Contains(reloaded.Entries, entry => entry.Path == "assets/example.txt");

            var diagnostics = await archive.ValidateIntegrityAsync();
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static SnapshotManifestEntry CreateEntry(string path, byte[] content) =>
        new(
            path,
            SnapshotManifestEntryKind.Source,
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    private static async Task WriteEntryAsync(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        await using var stream = entry.Open();
        await stream.WriteAsync(content);
    }
}
