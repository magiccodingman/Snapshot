using System.Security.Cryptography;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Manifest;

namespace Snapshot.Protocol.Validation;

public sealed record HostedRouteResult(
    string Route,
    Uri Url,
    int StatusCode,
    string? ContentType,
    long? ContentLength,
    string? ETag,
    string? CacheControl,
    string BodySha256);

public sealed class HostedSiteValidationResult
{
    public required IReadOnlyList<HostedRouteResult> Routes { get; init; }

    public required IReadOnlyList<SnapshotDiagnostic> Diagnostics { get; init; }

    public bool Succeeded => Diagnostics.All(static diagnostic => diagnostic.Severity != SnapshotDiagnosticSeverity.Error);
}

public sealed class HostedSiteValidator
{
    private readonly HttpClient _httpClient;

    public HostedSiteValidator(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task<HostedSiteValidationResult> ValidateAsync(
        Uri baseUri,
        SnapshotManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(manifest);

        var diagnostics = new List<SnapshotDiagnostic>();
        var results = new List<HostedRouteResult>();
        var routes = manifest.Entries
            .Where(static entry =>
                entry.Route is not null &&
                entry.Kind is SnapshotManifestEntryKind.Snapshot or SnapshotManifestEntryKind.Source)
            .Select(static entry => entry.Route!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static route => route, StringComparer.Ordinal)
            .ToArray();

        foreach (var route in routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestUri = new Uri(baseUri, route == "/" ? "/" : route.TrimEnd('/') + '/');
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var hash = await SHA256.HashDataAsync(body, cancellationToken).ConfigureAwait(false);

            var result = new HostedRouteResult(
                route,
                requestUri,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                response.Content.Headers.ContentLength,
                response.Headers.ETag?.Tag,
                response.Headers.CacheControl?.ToString(),
                Convert.ToHexString(hash).ToLowerInvariant());
            results.Add(result);

            if (!response.IsSuccessStatusCode)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    $"Hosted route returned HTTP {(int)response.StatusCode}.",
                    route,
                    requestUri.ToString()));
            }
        }

        foreach (var etagGroup in results
                     .Where(static result => !string.IsNullOrWhiteSpace(result.ETag))
                     .GroupBy(static result => result.ETag!, StringComparer.Ordinal)
                     .Where(static group => group.Select(result => result.BodySha256).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.DuplicateEtag,
                SnapshotDiagnosticSeverity.Error,
                $"Divergent HTML bodies share ETag {etagGroup.Key}: {string.Join(", ", etagGroup.Select(static result => result.Route))}.",
                Suggestion: "Configure the hosting provider to suppress or differentiate ETags for distinct routes."));
        }

        return new HostedSiteValidationResult
        {
            Routes = results,
            Diagnostics = diagnostics
        };
    }
}
