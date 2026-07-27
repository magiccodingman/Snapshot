using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snapshot.Protocol.Archive;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Manifest;
using Xunit;

namespace Snapshot.Processing.Tests;

public sealed class SnapshotArchiveProcessorTests
{
    [Fact]
    public async Task Archive_processing_only_rewrites_manifested_snapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "snapshot-processing-tests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "source.zip");
        var outputPath = Path.Combine(root, "processed.zip");
        Directory.CreateDirectory(root);

        const string loader = "<!doctype html>\n<!-- loader must remain exact -->\n<script id=\"snapshot-protocol\"></script>";
        const string alias = "<!doctype html><meta http-equiv=\"refresh\" content=\"0;url=/docs/\"><script>location.replace('/docs/')</script>";
        const string snapshot = """
            <!doctype html>
            <html>
            <head>
              <!-- removable snapshot comment -->
              <title>Docs</title>
              <link rel="canonical" href="https://example.test/docs">
              <script type="application/ld+json">
                { "name": "Docs" }
              </script>
            </head>
            <body><main>Docs</main></body>
            </html>
            """;

        try
        {
            await CreateArchiveAsync(sourcePath, loader, alias, snapshot);

            var result = await new SnapshotArchiveProcessor().ProcessAsync(sourcePath, outputPath);

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.Equal(1, result.ProcessedSnapshots);
            Assert.True(result.ProcessedSnapshotBytes < result.OriginalSnapshotBytes);

            await using var archive = await SnapshotArchive.OpenAsync(outputPath);
            Assert.Equal(loader, await archive.ReadTextAsync("index.html"));
            Assert.Equal(alias, await archive.ReadTextAsync("DOCS/index.html"));
            var processed = await archive.ReadTextAsync("docs/index.html");
            Assert.DoesNotContain("removable snapshot comment", processed, StringComparison.Ordinal);
            Assert.Contains("{\"name\":\"Docs\"}", processed, StringComparison.Ordinal);

            var diagnostics = await archive.ValidateIntegrityAsync();
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CreateArchiveAsync(
        string path,
        string loader,
        string alias,
        string snapshot)
    {
        var loaderBytes = Encoding.UTF8.GetBytes(loader);
        var aliasBytes = Encoding.UTF8.GetBytes(alias);
        var snapshotBytes = Encoding.UTF8.GetBytes(snapshot);
        var manifest = new SnapshotManifest
        {
            SourceFileCount = 1,
            CanonicalRouteCount = 1,
            AliasCount = 1,
            Entries =
            [
                CreateEntry("index.html", SnapshotManifestEntryKind.Source, loaderBytes),
                CreateEntry("docs/index.html", SnapshotManifestEntryKind.Snapshot, snapshotBytes, "/docs"),
                CreateEntry("DOCS/index.html", SnapshotManifestEntryKind.CaseAlias, aliasBytes, "/DOCS", "/docs")
            ]
        };

        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        await WriteEntryAsync(zip, "index.html", loaderBytes);
        await WriteEntryAsync(zip, "docs/index.html", snapshotBytes);
        await WriteEntryAsync(zip, "DOCS/index.html", aliasBytes);
        var manifestEntry = zip.CreateEntry("snapshot-manifest.json", CompressionLevel.SmallestSize);
        await using var manifestStream = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(manifestStream, manifest, SnapshotManifestJson.Options);
    }

    private static SnapshotManifestEntry CreateEntry(
        string path,
        SnapshotManifestEntryKind kind,
        byte[] content,
        string? route = null,
        string? canonicalRoute = null) =>
        new(
            path,
            kind,
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            route,
            canonicalRoute);

    private static async Task WriteEntryAsync(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        await using var stream = entry.Open();
        await stream.WriteAsync(content);
    }
}
