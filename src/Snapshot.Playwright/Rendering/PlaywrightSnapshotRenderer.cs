using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Playwright;
using Snapshot.Playwright.Browser;
using Snapshot.Playwright.Hosting;
using Snapshot.Playwright.Setup;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Routes;

namespace Snapshot.Playwright.Rendering;

public sealed class PlaywrightSnapshotRenderer : ISnapshotRenderer
{
    private readonly PlaywrightSnapshotOptions _options;
    private readonly ISnapshotLogger _logger;

    public PlaywrightSnapshotRenderer(PlaywrightSnapshotOptions options, ISnapshotLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SnapshotRenderResult>> RenderAsync(
        SnapshotRenderRequest request,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installer = new PlaywrightBrowserInstaller(_logger);
        await installer.EnsureInstalledAsync(_options, cancellationToken).ConfigureAwait(false);
        await using var host = await SnapshotLocalHost.StartAsync(request.SourceDirectory, _logger, cancellationToken).ConfigureAwait(false);

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless,
            ExecutablePath = _options.BrowserInstallMode == BrowserInstallMode.CustomExecutable ? _options.BrowserExecutablePath : null
        }).ConfigureAwait(false);

        try
        {
            var concurrency = request.Concurrency ?? _options.Concurrency ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            concurrency = Math.Clamp(concurrency, 1, Math.Max(1, request.Routes.Count));
            var channel = Channel.CreateBounded<SnapshotRoute>(new BoundedChannelOptions(Math.Max(concurrency * 2, 4))
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            var results = new ConcurrentDictionary<string, SnapshotRenderResult>(StringComparer.Ordinal);
            var completed = 0;

            var workers = Enumerable.Range(0, concurrency)
                .Select(workerId => RunWorkerAsync(workerId, browser, host.BaseUri, request, channel.Reader, results, progress, () => Interlocked.Increment(ref completed), cancellationToken))
                .ToArray();

            foreach (var route in request.Routes)
            {
                await channel.Writer.WriteAsync(route, cancellationToken).ConfigureAwait(false);
            }

            channel.Writer.Complete();
            await Task.WhenAll(workers).ConfigureAwait(false);

            return request.Routes.Select(route => results.TryGetValue(route.Path, out var result)
                    ? result
                    : new SnapshotRenderResult(route, false, null, 0, TimeSpan.Zero, SnapshotDiagnosticCodes.BrowserFailure, "No browser worker returned a result."))
                .ToArray();
        }
        finally
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task RunWorkerAsync(
        int workerId,
        IBrowser browser,
        Uri baseUri,
        SnapshotRenderRequest request,
        ChannelReader<SnapshotRoute> routes,
        ConcurrentDictionary<string, SnapshotRenderResult> results,
        IProgress<SnapshotProgress>? progress,
        Func<int> incrementCompleted,
        CancellationToken cancellationToken)
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ServiceWorkers = ServiceWorkerPolicy.Block
        }).ConfigureAwait(false);

        var worker = new BrowserWorker(workerId, context, baseUri, request.Timeouts, _logger);
        await worker.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var route in routes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = await worker.RenderAsync(route, request.Retry.MaximumAttempts, cancellationToken).ConfigureAwait(false);
            results[route.Path] = result;
            var completed = incrementCompleted();
            progress?.Report(new SnapshotProgress(
                SnapshotProgressStage.Rendering,
                result.Succeeded ? $"Captured {route.Path}" : $"Failed {route.Path}: {result.ErrorMessage}",
                completed,
                request.Routes.Count,
                route.Path));
        }

        await worker.DisposeAsync().ConfigureAwait(false);
    }

}
