using System.Diagnostics;
using Snapshot.Playwright;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Hosting;
using Snapshot.Protocol.Routes;
using Snapshot.TestHost;
using Xunit;

namespace Snapshot.EndToEndTests;

public sealed class SnapshotEndToEndTests
{
    [Fact]
    public async Task Blazor_site_builds_and_is_served_from_the_zip()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SNAPSHOT_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var publishDirectory = Path.Combine(Path.GetTempPath(), "snapshot-e2e", Guid.NewGuid().ToString("N"), "publish");
        var outputRoot = Path.GetDirectoryName(publishDirectory)!;
        var output = Path.Combine(outputRoot, "site.zip");
        Directory.CreateDirectory(publishDirectory);

        try
        {
            await RunProcessAsync("dotnet", [
                "publish",
                Path.Combine(repositoryRoot, "tests", "Snapshot.TestSite", "Snapshot.TestSite.csproj"),
                "-c", "Release",
                "-o", publishDirectory
            ]);

            var webRoot = Path.Combine(publishDirectory, "wwwroot");
            var engine = SnapshotEngine.CreateBuilder().UsePlaywright().Build();
            var result = await engine.BuildAsync(new SnapshotBuildRequest
            {
                SourceDirectory = webRoot,
                OutputPath = output,
                Hosting = new SnapshotHostingOptions { Provider = SnapshotHostingProvider.Netlify },
                Concurrency = 2
            });

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            await using var host = await SnapshotTestHost.StartAsync(new SnapshotTestHostOptions
            {
                ArchivePath = output,
                Profile = SnapshotTestHostProfile.CaseSensitive
            });

            using var http = new HttpClient { BaseAddress = host.BaseUri };
            var immediate = await http.GetStringAsync("/immediate/");
            var delayed = await http.GetStringAsync("/delayed/");
            var unicode = await http.GetStringAsync("/caf%C3%A9/");
            Assert.Contains("data-test-page-id=\"immediate\"", immediate, StringComparison.Ordinal);
            Assert.Contains("data-test-page-id=\"delayed\"", delayed, StringComparison.Ordinal);
            Assert.Contains("data-test-page-id=\"unicode-cafe\"", unicode, StringComparison.Ordinal);

            var failedOutput = Path.Combine(outputRoot, "never-ready.zip");
            var failed = await engine.BuildAsync(new SnapshotBuildRequest
            {
                SourceDirectory = webRoot,
                OutputPath = failedOutput,
                Discovery = new SnapshotRouteDiscoveryOptions
                {
                    Mode = SnapshotRouteDiscoveryMode.ExplicitOnly,
                    AdditionalRoutes = ["/never-ready"]
                },
                CaseAliases = new SnapshotCaseAliasOptions
                {
                    Enabled = false,
                    GenerateMissingPrefixGateways = false
                },
                Timeouts = new SnapshotTimeoutOptions
                {
                    StartupTimeout = TimeSpan.FromSeconds(30),
                    RouteTimeout = TimeSpan.FromMilliseconds(500),
                    WatchdogTimeout = TimeSpan.FromSeconds(3)
                },
                Retry = new SnapshotRetryOptions { MaximumAttempts = 1 },
                Concurrency = 1
            });

            Assert.False(failed.Succeeded);
            Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Code == "READY_TIMEOUT");
            Assert.False(File.Exists(failedOutput));
            Assert.False(File.Exists(failedOutput + ".partial"));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Snapshot.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Snapshot.sln.");
    }

    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }
}
