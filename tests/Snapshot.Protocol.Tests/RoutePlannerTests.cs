using System.IO.Compression;
using System.Text;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Hosting.Netlify;
using Snapshot.Protocol.Output;
using Snapshot.Protocol.Routes;
using Xunit;

namespace Snapshot.Protocol.Tests;

public sealed class RoutePlannerTests
{
    [Fact]
    public void MyPath_generates_agreed_meaningful_case_variants()
    {
        var variants = SnapshotOutputPlanner.GenerateCaseVariants("/MyPath").ToHashSet(StringComparer.Ordinal);

        Assert.Contains("/MyPath", variants);
        Assert.Contains("/myPath", variants);
        Assert.Contains("/mypath", variants);
        Assert.Contains("/Mypath", variants);
        Assert.Contains("/MYPATH", variants);
        Assert.Equal(5, variants.Count);
    }

    [Fact]
    public void Root_maps_to_index_subdirectory()
    {
        var route = SnapshotRoute.Parse("/");
        Assert.Equal("index/index.html", route.OutputPath.Value);
    }

    [Theory]
    [InlineData("/products%2Fhidden")]
    [InlineData("/products%5Chidden")]
    [InlineData("https://example.test/products%2fhidden")]
    public void Encoded_path_separators_are_rejected(string value)
    {
        Assert.Throws<SnapshotRouteException>(() => SnapshotRoute.Parse(value));
    }

    [Fact]
    public async Task Discovery_reads_urlset_and_rejects_query_routes()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sitemap.xml"), """
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://example.test/one</loc></url>
                  <url><loc>https://example.test/two?id=2</loc></url>
                </urlset>
                """);

            var result = await new SitemapRouteDiscoverer().DiscoverAsync(root, new SnapshotRouteDiscoveryOptions(), CancellationToken.None);

            Assert.Contains(result.Routes, route => route.Path == "/one");
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ROUTE003");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Discovery_reads_gzip_urlsets_and_ignores_sitemap_indexes_as_pages()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sitemap-index.xml"), """
                <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <sitemap><loc>https://example.test/sitemap-pages.xml.gz</loc></sitemap>
                </sitemapindex>
                """);

            await using (var file = File.Create(Path.Combine(root, "sitemap-pages.xml.gz")))
            await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            {
                var bytes = Encoding.UTF8.GetBytes("""
                    <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                      <url><loc>https://example.test/from-gzip</loc></url>
                    </urlset>
                    """);
                await gzip.WriteAsync(bytes);
            }

            var result = await new SitemapRouteDiscoverer().DiscoverAsync(root, new SnapshotRouteDiscoveryOptions(), CancellationToken.None);

            Assert.Contains(result.Routes, route => route.Path == "/from-gzip");
            Assert.DoesNotContain(result.Routes, route => route.Path.EndsWith(".gz", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Windows_mode_disables_physical_case_aliases()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "loader");
            var route = SnapshotRoute.Parse("/MyPath");
            var plan = new SnapshotOutputPlanner().Create(
                root,
                [route],
                SnapshotTargetFilesystem.Windows,
                new SnapshotCaseAliasOptions(),
                []);

            Assert.DoesNotContain(plan.GeneratedEntries, entry => entry.Kind == SnapshotGeneratedEntryKind.CaseAlias);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Existing_developer_index_wins_over_generated_snapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = Path.Combine(root, "manual");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "index.html"), "manual");
            var route = SnapshotRoute.Parse("/manual");

            var plan = new SnapshotOutputPlanner().Create(
                root,
                [route],
                SnapshotTargetFilesystem.CaseSensitive,
                new SnapshotCaseAliasOptions { Enabled = false },
                []);

            Assert.DoesNotContain(plan.RoutesToRender, candidate => candidate.Path == "/manual");
            Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "OUTPUT102" && diagnostic.Route == "/manual");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Netlify_generated_blocks_replace_only_the_managed_section()
    {
        const string existing = "developer line\n\n# BEGIN Snapshot Protocol\nold generated line\n# END Snapshot Protocol\nfooter\n";

        var merged = NetlifyArtifactGenerator.MergeGeneratedBlock(existing, "new generated line");

        Assert.Contains("developer line", merged, StringComparison.Ordinal);
        Assert.Contains("footer", merged, StringComparison.Ordinal);
        Assert.Contains("new generated line", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("old generated line", merged, StringComparison.Ordinal);
        Assert.Equal(1, merged.Split("# BEGIN Snapshot Protocol", StringSplitOptions.None).Length - 1);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
