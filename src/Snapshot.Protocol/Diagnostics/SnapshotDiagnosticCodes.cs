namespace Snapshot.Protocol.Diagnostics;

public static class SnapshotDiagnosticCodes
{
    public const string NoRoutes = "ROUTE001";
    public const string InvalidRoute = "ROUTE002";
    public const string QueryRouteUnsupported = "ROUTE003";
    public const string CaseConflict = "ROUTE004";
    public const string UppercaseRoute = "ROUTE101";
    public const string UnsafeArchivePath = "PATH001";
    public const string WindowsPathConflict = "PATH007";
    public const string ExistingFilePreserved = "OUTPUT102";
    public const string OutputCollision = "OUTPUT201";
    public const string SitemapInvalid = "SITEMAP001";
    public const string SitemapRepresentationInvalid = "SITEMAP002";
    public const string SitemapRepresentationConflict = "SITEMAP003";
    public const string SnapshotMissingCanonical = "HTML005";
    public const string SnapshotInvalid = "HTML010";
    public const string CanonicalMissing = "SEO001";
    public const string CanonicalDuplicate = "SEO002";
    public const string CanonicalInvalid = "SEO003";
    public const string CanonicalRouteMismatch = "SEO004";
    public const string InlineJsonInvalid = "PROCESS001";
    public const string InlineCssPreserved = "PROCESS101";
    public const string HtmlMinificationPreserved = "PROCESS102";
    public const string HtmlMetadataPreserved = "PROCESS103";
    public const string ProcessingInvalid = "PROCESS201";
    public const string DuplicateEtag = "HOST001";
    public const string BrowserFailure = "BROWSER001";
    public const string BrowserInstallFailure = "BROWSER002";
}
