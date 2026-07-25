namespace Snapshot.Protocol.Protocol;

public static class SnapshotProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const string ScriptId = "snapshot-protocol";
    public const string ActivationQueryKey = "snapshot-protocol";
    public const string RestoreRouteQueryKey = "snapshot-route";
    public const string ReadyElementName = "snapshot-ready";
    public const string ProcessedAttributeName = "data-snapshot-processed";
    public const string SiteVersionMetaName = "snapshot:site-version";
    public const string RouteMetaName = "snapshot:route";
    public const string ClientReadyMessage = "snapshot:client-ready";
    public const string NavigateMessage = "snapshot:navigate";
    public const string ResultMessage = "snapshot:result";
    public const string ErrorMessage = "snapshot:error";
    public const string ManifestFileName = "snapshot-manifest.json";
}
