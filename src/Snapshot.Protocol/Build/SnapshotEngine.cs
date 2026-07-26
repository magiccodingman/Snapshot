using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Output;
using Snapshot.Protocol.Processing;
using Snapshot.Protocol.Protocol;
using Snapshot.Protocol.Routes;
using Snapshot.Protocol.Validation;

namespace Snapshot.Protocol.Build;

public sealed partial class SnapshotEngine
{
    private readonly ISnapshotRenderer _renderer;
    private readonly ISnapshotProcessor _processor;
    private readonly ISnapshotLogger _logger;

    internal SnapshotEngine(ISnapshotRenderer renderer, ISnapshotProcessor processor, ISnapshotLogger logger)
    {
        _renderer = renderer;
        _processor = processor;
        _logger = logger;
    }

    public static SnapshotEngineBuilder CreateBuilder() => new();

    public async Task<SnapshotBuildResult> BuildAsync(
        SnapshotBuildRequest request,
        IProgress<SnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<SnapshotDiagnostic>();
        var routeResults = new List<SnapshotRouteResult>();
        string? temporaryPath = null;

        try
        {
            progress?.Report(new SnapshotProgress(SnapshotProgressStage.Validating, "Validating source and output paths."));
            var sourceDirectory = Path.GetFullPath(request.SourceDirectory);
            var outputPath = Path.GetFullPath(request.OutputPath);

            ValidateRequest(request, sourceDirectory, outputPath, diagnostics);
            if (HasErrors(diagnostics))
            {
                return Failed(stopwatch, diagnostics, routeResults);
            }

            var indexPath = Path.Combine(sourceDirectory, "index.html");
            var indexHtml = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
            var siteVersion = ReadSiteVersion(indexHtml);
            if (!indexHtml.Contains($"id=\"{SnapshotProtocolConstants.ScriptId}\"", StringComparison.OrdinalIgnoreCase) &&
                !indexHtml.Contains($"id='{SnapshotProtocolConstants.ScriptId}'", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    $"Source index.html does not include the required script id '{SnapshotProtocolConstants.ScriptId}'.",
                    Source: indexPath));
                return Failed(stopwatch, diagnostics, routeResults);
            }

            progress?.Report(new SnapshotProgress(SnapshotProgressStage.DiscoveringRoutes, "Scanning sitemap XML and explicit routes."));
            var discoverer = new SitemapRouteDiscoverer();
            var discovery = await discoverer.DiscoverAsync(sourceDirectory, request.Discovery, cancellationToken).ConfigureAwait(false);
            var canonicalRoutes = request.RootGateway.Enabled
                ? discovery.Routes
                : discovery.Routes.Where(static route => route.Path != "/").ToArray();

            if (!request.RootGateway.Enabled && canonicalRoutes.Count != discovery.Routes.Count)
            {
                _logger.Log(new SnapshotLogEntry(
                    SnapshotLogLevel.Information,
                    "Root gateway generation is disabled; the source index.html loader will remain without a generated index/index.html snapshot."));
            }

            progress?.Report(new SnapshotProgress(SnapshotProgressStage.Planning, "Planning canonical snapshots, gateways, aliases, and provider artifacts."));
            var planner = new SnapshotOutputPlanner();
            var plan = planner.Create(
                sourceDirectory,
                canonicalRoutes,
                request.TargetFilesystem,
                request.CaseAliases,
                discovery.Diagnostics);
            diagnostics.AddRange(plan.Diagnostics);

            _logger.Log(new SnapshotLogEntry(
                SnapshotLogLevel.Information,
                $"Plan: {plan.CanonicalRoutes.Count} canonical routes, {plan.AliasCount} aliases, {plan.GatewayCount} prefix gateways."));

            if (HasErrors(diagnostics))
            {
                return Failed(stopwatch, diagnostics, routeResults);
            }

            progress?.Report(new SnapshotProgress(
                SnapshotProgressStage.StartingBrowser,
                $"Preparing the streaming render pipeline for {plan.RoutesToRender.Count} routes."));

            var renderResults = plan.RoutesToRender.Count == 0
                ? EmptyRenderResults(cancellationToken)
                : _renderer.RenderAsync(
                    new SnapshotRenderRequest(
                        sourceDirectory,
                        plan.RoutesToRender,
                        request.Timeouts,
                        request.Retry,
                        request.Concurrency),
                    progress,
                    cancellationToken);

            progress?.Report(new SnapshotProgress(SnapshotProgressStage.WritingArchive, "Streaming source and rendered entries into the ZIP artifact."));
            var writer = new SnapshotZipWriter(_logger, _processor);
            temporaryPath = outputPath + ".partial";
            var written = await writer.WriteAsync(
                request,
                plan,
                renderResults,
                outputPath,
                siteVersion,
                progress,
                cancellationToken).ConfigureAwait(false);

            diagnostics.AddRange(written.Diagnostics);

            foreach (var rendered in written.RenderResults)
            {
                routeResults.Add(new SnapshotRouteResult(
                    rendered.Route.Path,
                    rendered.Route.OutputPath.Value,
                    rendered.Succeeded,
                    rendered.Attempts,
                    rendered.Elapsed,
                    rendered.ErrorCode,
                    rendered.ErrorMessage));

                if (!rendered.Succeeded)
                {
                    diagnostics.Add(new SnapshotDiagnostic(
                        rendered.ErrorCode ?? SnapshotDiagnosticCodes.BrowserFailure,
                        SnapshotDiagnosticSeverity.Error,
                        rendered.ErrorMessage ?? "The browser renderer did not return a snapshot.",
                        rendered.Route.Path,
                        rendered.Route.Source,
                        rendered.Route.OutputPath.Value));
                }
            }

            foreach (var manual in plan.CanonicalRoutes.Where(route => plan.SourceEntries.Contains(route.OutputPath.Value)))
            {
                routeResults.Add(new SnapshotRouteResult(manual.Path, manual.OutputPath.Value, true, 0, TimeSpan.Zero));
            }

            if (HasErrors(diagnostics) || written.Manifest is null)
            {
                if (!request.PreservePartialArtifact && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                    temporaryPath = null;
                }

                return Failed(
                    stopwatch,
                    diagnostics,
                    routeResults,
                    request.PreservePartialArtifact ? written.TemporaryPath : null,
                    written.Manifest);
            }

            progress?.Report(new SnapshotProgress(SnapshotProgressStage.ValidatingArchive, "Validating archive entries, hashes, and route output."));
            var validator = new SnapshotArchiveValidator();
            var validation = await validator.ValidateAsync(temporaryPath, plan, written.Manifest, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(validation);

            if (HasErrors(diagnostics))
            {
                if (!request.PreservePartialArtifact)
                {
                    File.Delete(temporaryPath);
                    temporaryPath = null;
                }

                return Failed(stopwatch, diagnostics, routeResults, request.PreservePartialArtifact ? written.TemporaryPath : null, written.Manifest);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            temporaryPath = null;
            stopwatch.Stop();
            progress?.Report(new SnapshotProgress(SnapshotProgressStage.Completed, $"Created {outputPath}."));
            _logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Information, $"Snapshot artifact completed in {stopwatch.Elapsed}."));

            return new SnapshotBuildResult
            {
                Succeeded = true,
                OutputPath = outputPath,
                Diagnostics = diagnostics,
                Routes = routeResults.OrderBy(static result => result.Route, StringComparer.Ordinal).ToArray(),
                Manifest = written.Manifest,
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            if (temporaryPath is not null && !request.PreservePartialArtifact && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
        catch (Exception exception)
        {
            _logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Error, "Snapshot build failed unexpectedly.", exception));
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                exception.Message));

            if (temporaryPath is not null && !request.PreservePartialArtifact && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            return Failed(stopwatch, diagnostics, routeResults, request.PreservePartialArtifact ? temporaryPath : null);
        }
    }

    private static async IAsyncEnumerable<SnapshotRenderResult> EmptyRenderResults(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    private static void ValidateRequest(
        SnapshotBuildRequest request,
        string sourceDirectory,
        string outputPath,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                "The source directory does not exist.",
                Source: sourceDirectory));
            return;
        }

        if (!File.Exists(Path.Combine(sourceDirectory, "index.html")))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                "The source directory does not contain index.html.",
                Source: sourceDirectory));
        }

        if (!outputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                "Snapshot output must be a .zip file.",
                OutputPath: outputPath));
        }

        if (request.Retry.MaximumAttempts < 1)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                "MaximumAttempts must be at least 1."));
        }

        if (request.Concurrency is < 1)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                "Concurrency must be at least 1 when supplied."));
        }
    }

    private static string? ReadSiteVersion(string indexHtml)
    {
        var script = ScriptTagRegex().Match(indexHtml);
        if (!script.Success)
        {
            return null;
        }

        var version = SiteVersionRegex().Match(script.Value);
        return version.Success ? version.Groups[2].Value.Trim() : null;
    }

    private static bool HasErrors(IEnumerable<SnapshotDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);

    private static SnapshotBuildResult Failed(
        Stopwatch stopwatch,
        IReadOnlyList<SnapshotDiagnostic> diagnostics,
        IReadOnlyList<SnapshotRouteResult> routes,
        string? outputPath = null,
        Manifest.SnapshotManifest? manifest = null)
    {
        stopwatch.Stop();
        return new SnapshotBuildResult
        {
            Succeeded = false,
            OutputPath = outputPath,
            Diagnostics = diagnostics,
            Routes = routes,
            Manifest = manifest,
            Elapsed = stopwatch.Elapsed
        };
    }

    [GeneratedRegex("<script\\b[^>]*\\bid\\s*=\\s*([\\\"'])snapshot-protocol\\1[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex("\\bdata-site-version\\s*=\\s*([\\\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SiteVersionRegex();
}
