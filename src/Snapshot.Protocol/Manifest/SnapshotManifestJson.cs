using System.Text.Json;

namespace Snapshot.Protocol.Manifest;

public static class SnapshotManifestJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
