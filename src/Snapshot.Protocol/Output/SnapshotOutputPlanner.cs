using System.Text;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Paths;
using Snapshot.Protocol.Routes;

namespace Snapshot.Protocol.Output;

public sealed class SnapshotOutputPlanner
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public SnapshotOutputPlan Create(
        string sourceDirectory,
        IReadOnlyList<SnapshotRoute> routes,
        SnapshotTargetFilesystem targetFilesystem,
        SnapshotCaseAliasOptions aliasOptions,
        IReadOnlyList<SnapshotDiagnostic> discoveryDiagnostics)
    {
        var diagnostics = new List<SnapshotDiagnostic>(discoveryDiagnostics);
        var sourceEntries = EnumerateSourceEntries(sourceDirectory);
        var generated = new List<SnapshotGeneratedEntry>();
        var routesToRender = new List<SnapshotRoute>();
        var ownership = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var sourceEntry in sourceEntries)
        {
            ownership[sourceEntry] = "developer source file";
        }

        if (targetFilesystem == SnapshotTargetFilesystem.Windows)
        {
            ValidateWindowsCompatibility(routes, sourceEntries, diagnostics);
        }

        foreach (var route in routes)
        {
            var outputPath = route.OutputPath.Value;
            if (sourceEntries.Contains(outputPath))
            {
                diagnostics.Add(Preserved(route.Path, outputPath, "canonical snapshot"));
                continue;
            }

            if (!TryReserve(outputPath, $"canonical route {route.Path}", ownership, diagnostics, route.Path))
            {
                continue;
            }

            routesToRender.Add(route);
            generated.Add(new SnapshotGeneratedEntry(outputPath, SnapshotGeneratedEntryKind.Snapshot, route.Path));
        }

        var canonicalPaths = new HashSet<string>(routes.Select(static route => route.Path), StringComparer.Ordinal);
        var gatewayRoutes = aliasOptions.GenerateMissingPrefixGateways
            ? BuildMissingPrefixGateways(routes, canonicalPaths)
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (gatewayRoute, children) in gatewayRoutes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var archivePath = SnapshotArchivePath.FromRoute(gatewayRoute).Value;
            if (sourceEntries.Contains(archivePath))
            {
                diagnostics.Add(Preserved(gatewayRoute, archivePath, "missing-prefix gateway"));
                continue;
            }

            if (!TryReserve(archivePath, $"prefix gateway {gatewayRoute}", ownership, diagnostics, gatewayRoute))
            {
                continue;
            }

            generated.Add(new SnapshotGeneratedEntry(
                archivePath,
                SnapshotGeneratedEntryKind.PrefixGateway,
                gatewayRoute,
                Content: BuildGatewayHtml(gatewayRoute, children)));
        }

        var physicalAliasesEnabled = aliasOptions.Enabled && targetFilesystem == SnapshotTargetFilesystem.CaseSensitive;
        if (physicalAliasesEnabled)
        {
            var aliasSources = routes.Select(static route => route.Path)
                .Concat(gatewayRoutes.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static route => route, StringComparer.Ordinal)
                .ToArray();

            foreach (var canonicalRoute in aliasSources)
            {
                var aliases = GenerateCaseVariants(canonicalRoute, aliasOptions.Strategy)
                    .Where(alias => !alias.Equals(canonicalRoute, StringComparison.Ordinal))
                    .ToArray();

                if (aliasOptions.MaximumAliasesPerRoute is { } maximum && aliases.Length > maximum)
                {
                    diagnostics.Add(new SnapshotDiagnostic(
                        SnapshotDiagnosticCodes.OutputCollision,
                        SnapshotDiagnosticSeverity.Error,
                        $"Route {canonicalRoute} generated {aliases.Length} aliases, exceeding the configured maximum of {maximum}.",
                        canonicalRoute));
                    continue;
                }

                foreach (var alias in aliases)
                {
                    var archivePath = SnapshotArchivePath.FromRoute(alias).Value;
                    if (sourceEntries.Contains(archivePath))
                    {
                        diagnostics.Add(Preserved(alias, archivePath, $"case alias for {canonicalRoute}"));
                        continue;
                    }

                    if (!TryReserve(archivePath, $"case alias {alias} -> {canonicalRoute}", ownership, diagnostics, alias))
                    {
                        continue;
                    }

                    generated.Add(new SnapshotGeneratedEntry(
                        archivePath,
                        SnapshotGeneratedEntryKind.CaseAlias,
                        alias,
                        canonicalRoute,
                        BuildAliasHtml(canonicalRoute)));
                }
            }
        }

        return new SnapshotOutputPlan
        {
            SourceDirectory = sourceDirectory,
            CanonicalRoutes = routes,
            RoutesToRender = routesToRender,
            GeneratedEntries = generated,
            SourceEntries = sourceEntries,
            Diagnostics = diagnostics
        };
    }

    public static IEnumerable<string> GenerateCaseVariants(
        string route,
        SnapshotCaseAliasStrategy strategy = SnapshotCaseAliasStrategy.SingleSegment)
    {
        if (route == "/")
        {
            yield break;
        }

        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in strategy switch
                 {
                     SnapshotCaseAliasStrategy.SingleSegment => GenerateSingleSegmentVariants(segments),
                     SnapshotCaseAliasStrategy.Exhaustive => GenerateExhaustiveVariants(segments),
                     _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported case alias strategy.")
                 })
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> GenerateSingleSegmentVariants(IReadOnlyList<string> segments)
    {
        yield return '/' + string.Join('/', segments);

        for (var index = 0; index < segments.Count; index++)
        {
            foreach (var variant in GetSegmentVariants(segments[index]))
            {
                if (variant.Equals(segments[index], StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = segments.ToArray();
                candidate[index] = variant;
                yield return '/' + string.Join('/', candidate);
            }
        }
    }

    private static IEnumerable<string> GenerateExhaustiveVariants(IReadOnlyList<string> segments)
    {
        var variants = segments.Select(GetSegmentVariants).ToArray();
        var current = new string[segments.Count];

        return Expand(0);

        IEnumerable<string> Expand(int index)
        {
            if (index == variants.Length)
            {
                yield return '/' + string.Join('/', current);
                yield break;
            }

            foreach (var variant in variants[index])
            {
                current[index] = variant;
                foreach (var candidate in Expand(index + 1))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string[] GetSegmentVariants(string segment)
    {
        if (segment.Length == 0)
        {
            return [segment];
        }

        var lower = segment.ToLowerInvariant();
        var firstLower = char.ToLowerInvariant(segment[0]) + segment[1..];
        var titleLower = char.ToUpperInvariant(lower[0]) + lower[1..];
        var upper = segment.ToUpperInvariant();

        return new[] { segment, firstLower, lower, titleLower, upper }
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<string> EnumerateSourceEntries(string sourceDirectory)
    {
        return Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/'))
            .Select(SnapshotArchivePath.Normalize)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildMissingPrefixGateways(
        IReadOnlyList<SnapshotRoute> routes,
        IReadOnlySet<string> canonicalPaths)
    {
        var gateways = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var route in routes)
        {
            var segments = route.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var length = 1; length < segments.Length; length++)
            {
                var prefix = '/' + string.Join('/', segments.Take(length));
                if (canonicalPaths.Contains(prefix))
                {
                    continue;
                }

                var child = '/' + string.Join('/', segments.Take(length + 1));
                if (!gateways.TryGetValue(prefix, out var children))
                {
                    children = new HashSet<string>(StringComparer.Ordinal);
                    gateways[prefix] = children;
                }

                children.Add(child);
            }
        }

        return gateways.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static bool TryReserve(
        string archivePath,
        string owner,
        IDictionary<string, string> ownership,
        ICollection<SnapshotDiagnostic> diagnostics,
        string? route)
    {
        if (ownership.TryGetValue(archivePath, out var existing))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.OutputCollision,
                SnapshotDiagnosticSeverity.Error,
                $"Output path {archivePath} is claimed by both {existing} and {owner}.",
                route,
                OutputPath: archivePath));
            return false;
        }

        ownership[archivePath] = owner;
        return true;
    }

    private static SnapshotDiagnostic Preserved(string route, string path, string plannedRole) =>
        new(
            SnapshotDiagnosticCodes.ExistingFilePreserved,
            SnapshotDiagnosticSeverity.Info,
            $"Existing developer file was preserved instead of generating a {plannedRole}.",
            route,
            OutputPath: path);

    private static string BuildAliasHtml(string canonicalRoute)
    {
        var canonical = EnsureTrailingSlash(canonicalRoute);
        var escaped = System.Net.WebUtility.HtmlEncode(canonical);
        var js = canonical.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"<!doctype html><meta charset=\"utf-8\"><meta name=\"robots\" content=\"noindex,follow\"><link rel=\"canonical\" href=\"{escaped}\"><meta http-equiv=\"refresh\" content=\"0;url={escaped}\"><a href=\"{escaped}\">Continue</a><script>location.replace(\"{js}\")</script>";
    }

    private static string BuildGatewayHtml(string gatewayRoute, IReadOnlyList<string> children)
    {
        var title = gatewayRoute.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Home";
        var builder = new StringBuilder();
        builder.Append("<!doctype html><meta charset=\"utf-8\"><meta name=\"robots\" content=\"noindex,follow\"><title>")
            .Append(System.Net.WebUtility.HtmlEncode(title))
            .Append("</title><nav><a href=\"/\">Home</a>");

        foreach (var child in children)
        {
            builder.Append("<a href=\"")
                .Append(System.Net.WebUtility.HtmlEncode(EnsureTrailingSlash(child)))
                .Append("\">")
                .Append(System.Net.WebUtility.HtmlEncode(child.Split('/').Last()))
                .Append("</a>");
        }

        builder.Append("</nav>");
        return builder.ToString();
    }

    private static string EnsureTrailingSlash(string route) => route == "/" || route.EndsWith('/') ? route : route + '/';

    private static void ValidateWindowsCompatibility(
        IReadOnlyList<SnapshotRoute> routes,
        IReadOnlySet<string> sourceEntries,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        foreach (var group in routes.GroupBy(static route => route.OutputPath.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Select(static route => route.OutputPath.Value).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.WindowsPathConflict,
                    SnapshotDiagnosticSeverity.Error,
                    $"Routes cannot coexist using Windows path semantics: {string.Join(", ", group.Select(static route => route.Path))}."));
            }
        }

        foreach (var group in sourceEntries.GroupBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Distinct(StringComparer.Ordinal).Count() > 1)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.WindowsPathConflict,
                    SnapshotDiagnosticSeverity.Error,
                    $"Source entries cannot coexist when extracted using Windows path semantics: {string.Join(", ", group)}."));
            }
        }

        foreach (var route in routes)
        {
            foreach (var segment in route.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var stem = segment.Split('.')[0];
                if (WindowsReservedNames.Contains(stem) || segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || segment.EndsWith(' ') || segment.EndsWith('.'))
                {
                    diagnostics.Add(new SnapshotDiagnostic(
                        SnapshotDiagnosticCodes.WindowsPathConflict,
                        SnapshotDiagnosticSeverity.Error,
                        $"Route segment '{segment}' is not safe for ordinary Windows extraction or hosting.",
                        route.Path));
                }
            }
        }
    }
}
