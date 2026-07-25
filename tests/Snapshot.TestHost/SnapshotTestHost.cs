using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Snapshot.Protocol.Archive;

namespace Snapshot.TestHost;

public enum SnapshotTestHostProfile
{
    CaseSensitive,
    Windows,
    DuplicateEtags,
    UniqueEtags,
    NoEtags
}

public sealed class SnapshotTestHostOptions
{
    public required string ArchivePath { get; init; }

    public SnapshotTestHostProfile Profile { get; init; } = SnapshotTestHostProfile.CaseSensitive;
}

public sealed class SnapshotTestHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private SnapshotTestHost(WebApplication application, Uri baseUri)
    {
        _application = application;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static async Task<SnapshotTestHost> StartAsync(
        SnapshotTestHostOptions options,
        CancellationToken cancellationToken = default)
    {
        var archivePath = Path.GetFullPath(options.ArchivePath);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(0));
        var app = builder.Build();

        app.Run(async context =>
        {
            var requested = context.Request.Path.Value ?? "/";
            var entryPath = MapRequestToEntry(requested);
            await using var archive = await SnapshotArchive.OpenAsync(archivePath, context.RequestAborted);
            var file = archive.TryGetFile(entryPath);

            if (file is null && options.Profile == SnapshotTestHostProfile.Windows)
            {
                file = archive.FindCaseInsensitiveMatches(entryPath).SingleOrDefault();
            }

            if (file is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = file.ContentType ?? "application/octet-stream";
            await using var content = await archive.OpenFileAsync(file.Path, context.RequestAborted);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, context.RequestAborted);
            var bytes = buffer.ToArray();

            switch (options.Profile)
            {
                case SnapshotTestHostProfile.DuplicateEtags:
                    context.Response.Headers.ETag = "\"snapshot-duplicate\"";
                    break;
                case SnapshotTestHostProfile.UniqueEtags:
                    context.Response.Headers.ETag = $"\"{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\"";
                    break;
            }

            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        });

        await app.StartAsync(cancellationToken);
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("Test host did not expose an address.");
        return new SnapshotTestHost(app, new Uri(address.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase)));
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
    }

    private static string MapRequestToEntry(string requestPath)
    {
        var path = requestPath.Trim('/');
        if (path.Length == 0)
        {
            return "index.html";
        }

        if (Path.HasExtension(path))
        {
            return path;
        }

        return $"{path}/index.html";
    }
}
