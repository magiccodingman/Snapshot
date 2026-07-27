using System.Runtime.CompilerServices;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Routes;
using Xunit;

namespace Snapshot.Processing.Tests;

public sealed class ProcessingBuildIntegrationTests
{
    [Fact]
    public async Task Processing_error_marks_the_route_failed_and_rejects_the_artifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "snapshot-processing-build-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "wwwroot");
        var output = Path.Combine(root, "site.zip");
        Directory.CreateDirectory(source);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(source, "index.html"),
                "<!doctype html><html><head><script id=\"snapshot-protocol\"></script></head><body></body></html>");

            var route = SnapshotRoute.Parse("/docs");
            var renderer = new FixedRenderer(new SnapshotRenderResult(
                route,
                true,
                "<!doctype html><html><head><title>Docs</title><link rel=\"canonical\" href=\"https://example.test/wrong\"></head><body>Docs</body></html>",
                1,
                TimeSpan.FromMilliseconds(5)));
            var engine = SnapshotEngine.CreateBuilder()
                .UseRenderer(renderer)
                .UseStandardProcessing()
                .Build();

            var result = await engine.BuildAsync(new SnapshotBuildRequest
            {
                SourceDirectory = source,
                OutputPath = output,
                Discovery = new SnapshotRouteDiscoveryOptions
                {
                    Mode = SnapshotRouteDiscoveryMode.ExplicitOnly,
                    AdditionalRoutes = [route.Path]
                },
                CaseAliases = new SnapshotCaseAliasOptions
                {
                    Enabled = false,
                    GenerateMissingPrefixGateways = false
                },
                Concurrency = 1
            });

            Assert.False(result.Succeeded);
            var routeResult = Assert.Single(result.Routes);
            Assert.False(routeResult.Succeeded);
            Assert.Equal(SnapshotDiagnosticCodes.CanonicalRouteMismatch, routeResult.ErrorCode);
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Code == SnapshotDiagnosticCodes.CanonicalRouteMismatch &&
                diagnostic.Route == route.Path &&
                diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
            Assert.Equal(1, result.Diagnostics.Count(diagnostic =>
                diagnostic.Code == SnapshotDiagnosticCodes.CanonicalRouteMismatch &&
                diagnostic.Route == route.Path));
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FixedRenderer : ISnapshotRenderer
    {
        private readonly SnapshotRenderResult _result;

        public FixedRenderer(SnapshotRenderResult result)
        {
            _result = result;
        }

        public async IAsyncEnumerable<SnapshotRenderResult> RenderAsync(
            SnapshotRenderRequest request,
            IProgress<SnapshotProgress>? progress,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield return _result;
        }
    }
}
