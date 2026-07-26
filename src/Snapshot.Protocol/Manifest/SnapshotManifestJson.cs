using System.Text.Json;

namespace Snapshot.Protocol.Manifest;

internal static class SnapshotManifestJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
