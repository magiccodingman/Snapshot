using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Playwright;
using Snapshot.Playwright.Browser;
using Snapshot.Playwright.Hosting;
using Snapshot.Playwright.Setup;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Build;
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

    public async IAsyncEnumerable<SnapshotRenderResult> RenderAsync(
        SnapshotRenderRequest request,
        IProgress<SnapshotProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

            var routeChannel = Channel.CreateUnbounded<SnapshotRoute>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = false,
                AllowSynchronousContinuations = false
            });
            var resultChannel = Channel.CreateBounded<SnapshotRenderResult>(new BoundedChannelOptions(Math.Max(concurrency * 2, 4))
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

            foreach (var route in request.Routes)
            {
                routeChannel.Writer.TryWrite(route);
            }
            routeChannel.Writer.Complete();

            var completed = 0;
            var workers = Enumerable.Range(0, concurrency)
                .Select(workerId => RunWorkerAsync(
                    workerId,
                    browser,
                    host.BaseUri,
                    request,
                    routeChannel.Reader,
                    resultChannel.Writer,
                    progress,
                    () => Interlocked.Increment(ref completed),
                    cancellationToken))
                .ToArray();

            var completion = CompleteResultsAsync(workers, resultChannel.Writer);
            await foreach (var result in resultChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return result;
            }
            await completion.ConfigureAwait(false);
        }
        finally
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task CompleteResultsAsync(Task[] workers, ChannelWriter<SnapshotRenderResult> output)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            output.TryComplete();
        }
        catch (Exception exception)
        {
            output.TryComplete(exception);
        }
    }

    private async Task RunWorkerAsync(
        int workerId,
        IBrowser browser,
        Uri baseUri,
        SnapshotRenderRequest request,
        ChannelReader<SnapshotRoute> routes,
        ChannelWriter<SnapshotRenderResult> results,
        IProgress<SnapshotProgress>? progress,
        Func<int> incrementCompleted,
        CancellationToken cancellationToken)
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ServiceWorkers = ServiceWorkerPolicy.Block
        }).ConfigureAwait(false);
        await using var worker = new BrowserWorker(workerId, context, baseUri, request.Timeouts, _logger);
        await worker.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var route in routes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = await worker.RenderAsync(route, request.Retry.MaximumAttempts, cancellationToken).ConfigureAwait(false);
            await results.WriteAsync(result, cancellationToken).ConfigureAwait(false);
            var completed = incrementCompleted();
            progress?.Report(new SnapshotProgress(
                SnapshotProgressStage.Rendering,
                result.Succeeded ? $"Captured {route.Path}" : $"Failed {route.Path}: {result.ErrorMessage}",
                completed,
                request.Routes.Count,
                route.Path));
        }
    }
}
