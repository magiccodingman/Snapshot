using System.Text.Json;
using Snapshot.Cli.Commands;
using Snapshot.Cli.ConsoleOutput;
using Snapshot.Playwright;
using Snapshot.Playwright.Browser;
using Snapshot.Playwright.Setup;
using Snapshot.Protocol.Archive;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Hosting;
using Snapshot.Protocol.Routes;
using Snapshot.Protocol.Validation;

return await SnapshotCli.RunAsync(args).ConfigureAwait(false);

internal static class SnapshotCli
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var input = new CommandLine(args.Skip(1));
        var logger = new CliSnapshotLogger(input.Has("verbose"));

        try
        {
            return command switch
            {
                "build" => await BuildAsync(input, logger).ConfigureAwait(false),
                "validate" => await ValidateAsync(input).ConfigureAwait(false),
                "validate-host" => await ValidateHostAsync(input).ConfigureAwait(false),
                "inspect" => await InspectAsync(input).ConfigureAwait(false),
                "extract" => await ExtractAsync(input).ConfigureAwait(false),
                "browser" => await BrowserAsync(input, logger).ConfigureAwait(false),
                _ => Unknown(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 5;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(input.Has("verbose") ? exception : exception.Message);
            return 5;
        }
    }

    private static async Task<int> BuildAsync(CommandLine input, CliSnapshotLogger logger)
    {
        var source = RequiredPositional(input, 0, "build requires a source directory.");
        var output = input.Get("output") ?? "./snapshot-output.zip";
        var target = ParseTarget(input.Get("target-filesystem"));
        var discovery = ParseDiscovery(input.Get("route-discovery"));
        var routeTimeout = TimeSpan.FromSeconds(input.GetDouble("route-timeout") ?? 30);
        var startupTimeout = TimeSpan.FromSeconds(input.GetDouble("startup-timeout") ?? 60);
        var watchdogTimeout = TimeSpan.FromSeconds(input.GetDouble("watchdog-timeout") ?? Math.Max(40, routeTimeout.TotalSeconds + 10));
        var browserMode = ParseBrowserMode(input.Get("browser-install-mode"), input.Get("browser-executable"));

        var engine = SnapshotEngine.CreateBuilder()
            .UsePlaywright(options =>
            {
                options.Headless = !input.Has("headed");
                options.BrowserInstallMode = browserMode;
                options.BrowserExecutablePath = input.Get("browser-executable");
                options.Concurrency = input.GetInt("concurrency");
                options.InstallLinuxDependencies = input.Has("with-deps");
            }, logger)
            .Build();

        var request = new SnapshotBuildRequest
        {
            SourceDirectory = source,
            OutputPath = output,
            TargetFilesystem = target,
            Discovery = new SnapshotRouteDiscoveryOptions
            {
                Mode = discovery,
                AdditionalRoutes = input.GetMany("route")
            },
            CaseAliases = new SnapshotCaseAliasOptions
            {
                Enabled = !input.Has("no-case-aliases"),
                GenerateMissingPrefixGateways = !input.Has("no-missing-prefix-gateways"),
                MaximumAliasesPerRoute = input.GetInt("maximum-aliases-per-route")
            },
            Timeouts = new SnapshotTimeoutOptions
            {
                RouteTimeout = routeTimeout,
                StartupTimeout = startupTimeout,
                WatchdogTimeout = watchdogTimeout
            },
            Retry = new SnapshotRetryOptions { MaximumAttempts = input.GetInt("retries") ?? 3 },
            Concurrency = input.GetInt("concurrency"),
            Hosting = new SnapshotHostingOptions
            {
                Provider = input.Has("netlify") ? SnapshotHostingProvider.Netlify : SnapshotHostingProvider.Generic
            },
            PreservePartialArtifact = input.Has("preserve-partial")
        };

        var progress = new Progress<SnapshotProgress>(value =>
        {
            var count = value.Total > 0 ? $" [{value.Completed}/{value.Total}]" : string.Empty;
            Console.WriteLine($"{value.Stage,-18}{count} {value.Message}");
        });

        var result = await engine.BuildAsync(request, progress).ConfigureAwait(false);
        PrintDiagnostics(result.Diagnostics);
        Console.WriteLine();
        Console.WriteLine(result.Succeeded
            ? $"Created {result.OutputPath} in {result.Elapsed}."
            : $"Snapshot build failed after {result.Elapsed}.");
        Console.WriteLine($"Routes: {result.Routes.Count(static route => route.Succeeded)} succeeded, {result.Routes.Count(static route => !route.Succeeded)} failed.");

        if (input.Get("report") is { } reportPath)
        {
            var fullPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(result, ReportJsonOptions)).ConfigureAwait(false);
            Console.WriteLine($"Report: {fullPath}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static async Task<int> ValidateAsync(CommandLine input)
    {
        var path = RequiredPositional(input, 0, "validate requires a snapshot ZIP path.");
        await using var archive = await SnapshotArchive.OpenAsync(path).ConfigureAwait(false);
        var diagnostics = await archive.ValidateIntegrityAsync().ConfigureAwait(false);
        PrintDiagnostics(diagnostics);
        var manifest = await archive.TryReadManifestAsync().ConfigureAwait(false);
        if (manifest is null || diagnostics.Any(static diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error))
        {
            Console.Error.WriteLine("The archive failed integrity validation.");
            return 4;
        }

        Console.WriteLine($"Valid Snapshot Protocol archive: {manifest.Entries.Count} manifested files, {manifest.CanonicalRouteCount} canonical routes.");
        return 0;
    }

    private static async Task<int> ValidateHostAsync(CommandLine input)
    {
        var archivePath = RequiredPositional(input, 0, "validate-host requires a snapshot ZIP path.");
        var baseUrl = RequiredPositional(input, 1, "validate-host requires a deployed base URL.");
        await using var archive = await SnapshotArchive.OpenAsync(archivePath).ConfigureAwait(false);
        var manifest = await archive.TryReadManifestAsync().ConfigureAwait(false)
            ?? throw new InvalidDataException("snapshot-manifest.json is missing.");
        var result = await new HostedSiteValidator().ValidateAsync(new Uri(baseUrl, UriKind.Absolute), manifest).ConfigureAwait(false);
        PrintDiagnostics(result.Diagnostics);
        foreach (var route in result.Routes)
        {
            Console.WriteLine($"{route.StatusCode} {route.Route,-40} ETag={route.ETag ?? "<none>"} SHA256={route.BodySha256[..12]}");
        }
        return result.Succeeded ? 0 : 4;
    }

    private static async Task<int> InspectAsync(CommandLine input)
    {
        var path = RequiredPositional(input, 0, "inspect requires a snapshot ZIP path.");
        await using var archive = await SnapshotArchive.OpenAsync(path).ConfigureAwait(false);
        if (input.Get("file") is { } file)
        {
            Console.WriteLine(await archive.ReadTextAsync(file).ConfigureAwait(false));
            return 0;
        }

        if (input.Has("manifest"))
        {
            var manifest = await archive.TryReadManifestAsync().ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(manifest, ReportJsonOptions));
            return manifest is null ? 4 : 0;
        }

        if (input.Has("tree"))
        {
            await foreach (var entry in archive.EnumerateEntriesAsync().ConfigureAwait(false))
            {
                Console.WriteLine(entry is SnapshotArchiveDirectory ? $"[D] {entry.Path}/" : $"[F] {entry.Path}");
            }
            return 0;
        }

        await foreach (var file in archive.EnumerateFilesAsync().ConfigureAwait(false))
        {
            Console.WriteLine($"{file.UncompressedLength,12:N0}  {file.Path}");
        }
        return 0;
    }

    private static async Task<int> ExtractAsync(CommandLine input)
    {
        var archivePath = RequiredPositional(input, 0, "extract requires a snapshot ZIP path.");
        var destination = RequiredPositional(input, 1, "extract requires a destination directory.");
        await using var archive = await SnapshotArchive.OpenAsync(archivePath).ConfigureAwait(false);
        await archive.ExtractToDirectoryAsync(destination, new SnapshotExtractionOptions
        {
            TargetFilesystem = ParseTarget(input.Get("target-filesystem")),
            OverwriteExistingFiles = input.Has("overwrite"),
            Atomic = !input.Has("no-atomic")
        }).ConfigureAwait(false);
        Console.WriteLine($"Extracted to {Path.GetFullPath(destination)}.");
        return 0;
    }

    private static async Task<int> BrowserAsync(CommandLine input, CliSnapshotLogger logger)
    {
        var action = RequiredPositional(input, 0, "browser requires 'install' or 'status'.").ToLowerInvariant();
        var installer = new PlaywrightBrowserInstaller(logger);
        if (action == "install")
        {
            await installer.InstallAsync(input.Has("with-deps")).ConfigureAwait(false);
            Console.WriteLine($"Chromium installation marker: {installer.GetInstallationMarkerPath()}");
            return 0;
        }

        if (action == "status")
        {
            var valid = await installer.ValidateExistingAsync().ConfigureAwait(false);
            Console.WriteLine(valid
                ? $"Chromium launched successfully. Validation marker: {installer.GetInstallationMarkerPath()}"
                : "No complete matching Chromium installation could be launched.");
            return valid ? 0 : 3;
        }

        throw new ArgumentException("browser action must be 'install' or 'status'.");
    }

    private static SnapshotTargetFilesystem ParseTarget(string? value) => value?.ToLowerInvariant() switch
    {
        null or "case-sensitive" => SnapshotTargetFilesystem.CaseSensitive,
        "windows" => SnapshotTargetFilesystem.Windows,
        _ => throw new ArgumentException("--target-filesystem must be 'case-sensitive' or 'windows'.")
    };

    private static SnapshotRouteDiscoveryMode ParseDiscovery(string? value) => value?.ToLowerInvariant() switch
    {
        null or "sitemaps-and-explicit" => SnapshotRouteDiscoveryMode.SitemapsAndExplicit,
        "sitemaps-only" => SnapshotRouteDiscoveryMode.SitemapsOnly,
        "explicit-only" => SnapshotRouteDiscoveryMode.ExplicitOnly,
        _ => throw new ArgumentException("--route-discovery must be sitemaps-and-explicit, sitemaps-only, or explicit-only.")
    };

    private static BrowserInstallMode ParseBrowserMode(string? value, string? executable) => value?.ToLowerInvariant() switch
    {
        null when executable is not null => BrowserInstallMode.CustomExecutable,
        null or "install-if-missing" => BrowserInstallMode.InstallIfMissing,
        "require-existing" => BrowserInstallMode.RequireExisting,
        "custom-executable" => BrowserInstallMode.CustomExecutable,
        _ => throw new ArgumentException("--browser-install-mode must be install-if-missing, require-existing, or custom-executable.")
    };

    private static string RequiredPositional(CommandLine input, int index, string message) =>
        input.Positionals.Count > index ? input.Positionals[index] : throw new ArgumentException(message);

    private static void PrintDiagnostics(IEnumerable<SnapshotDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.OrderBy(static item => item.Severity).ThenBy(static item => item.Code, StringComparer.Ordinal))
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = diagnostic.Severity switch
            {
                SnapshotDiagnosticSeverity.Error => ConsoleColor.Red,
                SnapshotDiagnosticSeverity.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.DarkGray
            };
            Console.WriteLine($"{diagnostic.Code} {diagnostic.Severity}: {diagnostic.Message}");
            if (diagnostic.Route is not null) Console.WriteLine($"  Route: {diagnostic.Route}");
            if (diagnostic.Source is not null) Console.WriteLine($"  Source: {diagnostic.Source}");
            if (diagnostic.OutputPath is not null) Console.WriteLine($"  Output: {diagnostic.OutputPath}");
            if (diagnostic.Suggestion is not null) Console.WriteLine($"  Suggestion: {diagnostic.Suggestion}");
            Console.ForegroundColor = previous;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Snapshot Protocol CLI

Commands:
  snapshot build <source> [options]
  snapshot validate <artifact.zip>
  snapshot validate-host <artifact.zip> <base-url>
  snapshot inspect <artifact.zip> [--tree|--manifest|--file <path>]
  snapshot extract <artifact.zip> <directory> [options]
  snapshot browser install [--with-deps]
  snapshot browser status

Build options:
  --output <zip>                       Default: ./snapshot-output.zip
  --target-filesystem <mode>           case-sensitive (default) or windows
  --route <path>                       Repeat for explicit routes
  --route-discovery <mode>             sitemaps-and-explicit, sitemaps-only, explicit-only
  --no-case-aliases
  --no-missing-prefix-gateways
  --maximum-aliases-per-route <count>  No limit by default
  --startup-timeout <seconds>          Default: 60
  --route-timeout <seconds>            Default: 30
  --watchdog-timeout <seconds>         Default: route timeout + 10, minimum 40
  --retries <count>                    Default: 3
  --concurrency <count>                Default: half CPU count, capped at 4
  --headed
  --browser-install-mode <mode>        install-if-missing, require-existing, custom-executable
  --browser-executable <path>
  --with-deps                          Allow Playwright to install Linux dependencies
  --netlify                            Generate/merge _headers and _redirects
  --report <json-path>
  --preserve-partial
  --verbose
""");
    }
}
