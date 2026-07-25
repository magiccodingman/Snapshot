using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Archive;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Routes;
using Xunit;

namespace Snapshot.Archive.Tests;

public sealed class ArchiveTests
{
    [Fact]
    public async Task Engine_builds_rooted_zip_and_archive_streams_files()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "artifact.zip");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """
                <!doctype html><script id="snapshot-protocol" data-site-version="1" src="snapshot-protocol.js"></script>
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "sitemap.xml"), """
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://example.test/</loc></url></urlset>
                """);

            var engine = SnapshotEngine.CreateBuilder().UseRenderer(new FakeRenderer()).Build();
            var result = await engine.BuildAsync(new SnapshotBuildRequest
            {
                SourceDirectory = root,
                OutputPath = output,
                CaseAliases = new SnapshotCaseAliasOptions { Enabled = false }
            });

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
            await using var archive = await SnapshotArchive.OpenAsync(output);
            Assert.True(archive.FileExists("index.html"));
            Assert.True(archive.FileExists("index/index.html"));
            Assert.True(archive.FileExists("snapshot-manifest.json"));

            var streamed = new List<string>();
            await archive.StreamFilesAsync(context =>
            {
                streamed.Add(context.Path);
                return ValueTask.CompletedTask;
            });
            Assert.Contains("index/index.html", streamed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "snapshot-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeRenderer : ISnapshotRenderer
    {
        public Task<IReadOnlyList<SnapshotRenderResult>> RenderAsync(
            SnapshotRenderRequest request,
            IProgress<SnapshotProgress>? progress,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<SnapshotRenderResult> results = request.Routes.Select(route => new SnapshotRenderResult(
                route,
                true,
                $"<!doctype html><html><head><link rel=\"canonical\" href=\"https://example.test{route.Path}\"><meta name=\"snapshot:route\" content=\"{route.Path}\"></head><body><div data-test-page-id=\"fake\"></div></body></html>",
                1,
                TimeSpan.FromMilliseconds(1))).ToArray();
            return Task.FromResult(results);
        }
    }
}
