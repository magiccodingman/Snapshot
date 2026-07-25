using Snapshot.Protocol.Build;
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

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
