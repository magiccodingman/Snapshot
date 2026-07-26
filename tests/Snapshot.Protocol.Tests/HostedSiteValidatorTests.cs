using System.Net;
using System.Net.Http.Headers;
using Snapshot.Protocol.Manifest;
using Snapshot.Protocol.Validation;
using Xunit;

namespace Snapshot.Protocol.Tests;

public sealed class HostedSiteValidatorTests
{
    [Fact]
    public async Task Divergent_bodies_with_the_same_etag_are_reported()
    {
        using var http = new HttpClient(new RouteHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/one/"] = "<html>one</html>",
            ["/two/"] = "<html>two</html>"
        }, "\"shared\""));

        var result = await new HostedSiteValidator(http).ValidateAsync(
            new Uri("https://example.test"),
            Manifest("/one", "/two"));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOST001");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Identical_bodies_with_the_same_etag_are_not_reported_as_divergent()
    {
        using var http = new HttpClient(new RouteHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/one/"] = "<html>same</html>",
            ["/two/"] = "<html>same</html>"
        }, "\"shared\""));

        var result = await new HostedSiteValidator(http).ValidateAsync(
            new Uri("https://example.test"),
            Manifest("/one", "/two"));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOST001");
        Assert.True(result.Succeeded);
    }

    private static SnapshotManifest Manifest(params string[] routes) => new()
    {
        CanonicalRouteCount = routes.Length,
        Entries = routes.Select(route => new SnapshotManifestEntry(
            route.Trim('/') + "/index.html",
            SnapshotManifestEntryKind.Snapshot,
            0,
            string.Empty,
            Route: route)).ToArray()
    };

    private sealed class RouteHandler(IReadOnlyDictionary<string, string> bodies, string etag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (!bodies.TryGetValue(path, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
            response.Headers.ETag = new EntityTagHeaderValue(etag);
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
