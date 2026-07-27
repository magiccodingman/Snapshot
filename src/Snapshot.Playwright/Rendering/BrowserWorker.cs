using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Protocol;
using Snapshot.Protocol.Routes;

namespace Snapshot.Playwright.Rendering;

internal sealed class BrowserWorker : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string BridgeScript = """
        window.addEventListener("message", event => {
          const data = event?.data;
          const isExecutorResponse = data?.type === "snapshot:result" || data?.type === "snapshot:error";
          if (event.source === window && isExecutorResponse) {
            window.snapshotProtocolEmit(JSON.stringify(data));
          }
        });
        """;
    private readonly int _workerId;
    private readonly IBrowserContext _context;
    private readonly Uri _baseUri;
    private readonly SnapshotTimeoutOptions _timeouts;
    private readonly ISnapshotLogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtocolEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _consoleErrors = new();
    private readonly ConcurrentQueue<string> _failedRequests = new();
    private IPage? _page;
    private string _sessionToken = string.Empty;

    public BrowserWorker(int workerId, IBrowserContext context, Uri baseUri, SnapshotTimeoutOptions timeouts, ISnapshotLogger logger)
    {
        _workerId = workerId;
        _context = context;
        _baseUri = baseUri;
        _timeouts = timeouts;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => ResetPageAsync(cancellationToken);

    public async Task<SnapshotRenderResult> RenderAsync(SnapshotRoute route, int maximumAttempts, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastCode = null;
        string? lastMessage = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_page is null || _page.IsClosed || attempt > 1) await ResetPageAsync(cancellationToken).ConfigureAwait(false);
            while (_consoleErrors.TryDequeue(out _)) { }
            while (_failedRequests.TryDequeue(out _)) { }
            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<ProtocolEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = completion;
            try
            {
                await _page!.EvaluateAsync("message => window.postMessage(message, window.location.origin)", new
                {
                    type = SnapshotProtocolConstants.NavigateMessage,
                    sessionToken = _sessionToken,
                    requestId,
                    targetPath = route.Path,
                    timeoutMs = (long)_timeouts.RouteTimeout.TotalMilliseconds
                }).ConfigureAwait(false);
                var envelope = await completion.Task.WaitAsync(_timeouts.WatchdogTimeout, cancellationToken).ConfigureAwait(false);
                if (envelope.Type == SnapshotProtocolConstants.ResultMessage && envelope.Html is not null)
                {
                    if (!string.Equals(envelope.CapturedPath, route.Path, StringComparison.Ordinal))
                    {
                        lastCode = "ROUTE_MISMATCH";
                        lastMessage = $"Expected {route.Path} but the client captured {envelope.CapturedPath}.";
                    }
                    else
                    {
                        stopwatch.Stop();
                        return new SnapshotRenderResult(route, true, envelope.Html, attempt, stopwatch.Elapsed, ConsoleErrors: _consoleErrors.ToArray(), FailedRequests: _failedRequests.ToArray());
                    }
                }
                else
                {
                    lastCode = envelope.Code ?? SnapshotDiagnosticCodes.BrowserFailure;
                    lastMessage = envelope.Message ?? "The Snapshot Protocol client returned an unknown failure.";
                }
            }
            catch (TimeoutException)
            {
                lastCode = "BROWSER_WATCHDOG_TIMEOUT";
                lastMessage = $"Browser worker {_workerId} did not receive a protocol response within {_timeouts.WatchdogTimeout}.";
            }
            catch (PlaywrightException exception)
            {
                lastCode = SnapshotDiagnosticCodes.BrowserFailure;
                lastMessage = exception.Message;
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
            _logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Warning, $"Worker {_workerId} failed {route.Path} on attempt {attempt}/{maximumAttempts}: {lastMessage}"));
        }
        stopwatch.Stop();
        return new SnapshotRenderResult(route, false, null, maximumAttempts, stopwatch.Elapsed, lastCode ?? SnapshotDiagnosticCodes.BrowserFailure, lastMessage ?? "Snapshot rendering failed.", _consoleErrors.ToArray(), _failedRequests.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_page is not null && !_page.IsClosed) await _page.CloseAsync().ConfigureAwait(false);
    }

    private async Task ResetPageAsync(CancellationToken cancellationToken)
    {
        if (_page is not null && !_page.IsClosed) await _page.CloseAsync().ConfigureAwait(false);
        _sessionToken = Guid.NewGuid().ToString("N");
        _page = await _context.NewPageAsync().ConfigureAwait(false);
        await _page.ExposeFunctionAsync("snapshotProtocolEmit", (string payload) => Receive(payload)).ConfigureAwait(false);
        await _page.AddInitScriptAsync(BridgeScript).ConfigureAwait(false);
        _page.Console += (_, message) =>
        {
            if (message.Type == "error") _consoleErrors.Enqueue(message.Text);
            _logger.Log(new SnapshotLogEntry(message.Type == "error" ? SnapshotLogLevel.Error : SnapshotLogLevel.Trace, $"Browser {_workerId}: {message.Text}"));
        };
        _page.RequestFailed += (_, request) => _failedRequests.Enqueue($"{request.Method} {request.Url}: {request.Failure}");
        var activationUri = new Uri(_baseUri, $"?{SnapshotProtocolConstants.ActivationQueryKey}={Uri.EscapeDataString(_sessionToken)}");
        await _page.GotoAsync(activationUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = (float)_timeouts.StartupTimeout.TotalMilliseconds }).ConfigureAwait(false);

        var protocolScriptPresent = await _page.EvaluateAsync<bool>("() => document.getElementById('snapshot-protocol') !== null").ConfigureAwait(false);
        if (!protocolScriptPresent)
        {
            var legacyClientPresent = await _page.EvaluateAsync<bool>("""
                () => Array.from(document.scripts).some(script =>
                  /truthorigin-snapshot|truthseo-snapshot|truth-snapshot/i.test(`${script.id} ${script.src}`))
                """).ConfigureAwait(false);
            var legacyDetail = legacyClientPresent
                ? " A legacy TruthOrigin/TruthSEO snapshot client was detected, but it is not compatible with the new Snapshot Protocol messages."
                : string.Empty;
            throw new InvalidOperationException(
                "The published application does not contain <script id=\"snapshot-protocol\">." + legacyDetail +
                " Add the new Snapshot Protocol browser client to index.html and rebuild/publish the application before creating a snapshot.");
        }

        try
        {
            await _page.WaitForFunctionAsync("() => window.__snapshotProtocolReady === true", null, new PageWaitForFunctionOptions { Timeout = (float)_timeouts.StartupTimeout.TotalMilliseconds }).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var details = BuildStartupDetails();
            throw new InvalidOperationException(
                $"The Snapshot Protocol browser client was present but did not announce readiness within {_timeouts.StartupTimeout}.{details}",
                exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private string BuildStartupDetails()
    {
        var console = _consoleErrors.Take(3).ToArray();
        var requests = _failedRequests.Take(3).ToArray();
        var parts = new List<string>();
        if (console.Length > 0) parts.Add($" Browser console errors: {string.Join(" | ", console)}");
        if (requests.Length > 0) parts.Add($" Failed requests: {string.Join(" | ", requests)}");
        return parts.Count == 0
            ? " Check that snapshot-protocol.js loaded successfully and did not throw during startup."
            : string.Concat(parts);
    }

    private void Receive(string payload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(payload, JsonOptions);
            if (envelope?.RequestId is not null && string.Equals(envelope.SessionToken, _sessionToken, StringComparison.Ordinal) && _pending.TryGetValue(envelope.RequestId, out var completion)) completion.TrySetResult(envelope);
        }
        catch (JsonException exception)
        {
            _logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Warning, "Ignored an invalid Snapshot Protocol browser message.", exception));
        }
    }
}