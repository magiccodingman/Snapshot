using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Snapshot.Protocol.Abstractions;

namespace Snapshot.Playwright.Hosting;

internal sealed class SnapshotLocalHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private SnapshotLocalHost(WebApplication application, Uri baseUri)
    {
        _application = application;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static async Task<SnapshotLocalHost> StartAsync(string sourceDirectory, ISnapshotLogger logger, CancellationToken cancellationToken)
    {
        var options = new WebApplicationOptions { ContentRootPath = sourceDirectory, WebRootPath = sourceDirectory, EnvironmentName = "Production" };
        var builder = WebApplication.CreateSlimBuilder(options);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(0));
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions { ServeUnknownFileTypes = true });
        app.MapFallback(async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(Path.Combine(sourceDirectory, "index.html"), cancellationToken).ConfigureAwait(false);
        });
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault() ?? throw new InvalidOperationException("The local snapshot host did not expose a listening address.");
        var baseUri = new Uri(address.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase));
        logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Information, $"Serving the static application from {baseUri}."));
        return new SnapshotLocalHost(app, baseUri);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
