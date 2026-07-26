using Snapshot.Playwright.Hosting;
using Snapshot.Protocol.Abstractions;
using Xunit;

namespace Snapshot.EndToEndTests;

public sealed class SnapshotLocalHostTests
{
    [Fact]
    public async Task Starts_on_dynamic_ipv4_loopback_port_and_serves_the_site()
    {
        var sourceDirectory = Path.Combine(Path.GetTempPath(), "snapshot-local-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "index.html"), "<!doctype html><title>Snapshot local host</title><main>reachable</main>");

        try
        {
            await using var host = await SnapshotLocalHost.StartAsync(
                sourceDirectory,
                NullSnapshotLogger.Instance,
                CancellationToken.None);

            Assert.Equal("127.0.0.1", host.BaseUri.Host);
            Assert.True(host.BaseUri.Port > 0);

            using var http = new HttpClient { BaseAddress = host.BaseUri };
            var html = await http.GetStringAsync("/");
            Assert.Contains("<main>reachable</main>", html, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }
}
