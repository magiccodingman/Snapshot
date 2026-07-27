using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Snapshot.Protocol.Metadata;

public static class SnapshotRepresentationMetadata
{
    public const string XmlNamespaceUri = "urn:snapshot-protocol:rendering:1";
    public const string XmlPrefix = "render";
    public const string RepresentationElementName = "representation";

    public const string StaticPrerendered = "static-prerendered";
    public const string LegacyPrerendered = "prerendered";
    public const string ClientRendered = "client-rendered";

    public const string RenderingModeMetaName = "rendering-mode";
    public const string RenderingModeMetaContent = StaticPrerendered;
    public const string SnapshotProtocolMetaName = "snapshot-protocol";
    public const string SnapshotProtocolMetaContent = "1";

    public static bool IsStaticPrerendered(string? value) =>
        value is not null &&
        (value.Trim().Equals(StaticPrerendered, StringComparison.OrdinalIgnoreCase) ||
         value.Trim().Equals(LegacyPrerendered, StringComparison.OrdinalIgnoreCase));

    public static bool IsClientRendered(string? value) =>
        value is not null && value.Trim().Equals(ClientRendered, StringComparison.OrdinalIgnoreCase);
}

public sealed record SnapshotHtmlMetadataResult(
    string Html,
    bool Changed,
    string? FailureReason = null);

public static class SnapshotHtmlMetadataAnnotator
{
    private static readonly (string Name, string Content)[] RequiredMetadata =
    [
        (SnapshotRepresentationMetadata.RenderingModeMetaName, SnapshotRepresentationMetadata.RenderingModeMetaContent),
        (SnapshotRepresentationMetadata.SnapshotProtocolMetaName, SnapshotRepresentationMetadata.SnapshotProtocolMetaContent)
    ];

    public static SnapshotHtmlMetadataResult Annotate(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        try
        {
            var parser = new HtmlParser();
            var document = parser.ParseDocument(html);
            var root = document.DocumentElement;
            var head = document.QuerySelector("head");
            if (root is null || head is null)
            {
                return new SnapshotHtmlMetadataResult(html, false, "The HTML document did not contain a usable <html> and <head> structure.");
            }

            var originalFingerprint = CreateSemanticFingerprint(document);
            var changed = false;

            foreach (var (name, content) in RequiredMetadata)
            {
                var matches = document.QuerySelectorAll("meta[name]")
                    .Where(element => string.Equals(element.GetAttribute("name")?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (matches.Length > 1)
                {
                    return new SnapshotHtmlMetadataResult(
                        html,
                        false,
                        $"The document already contains multiple <meta name=\"{name}\"> declarations.");
                }

                if (matches.Length == 1)
                {
                    var existingContent = matches[0].GetAttribute("content")?.Trim();
                    if (!string.Equals(existingContent, content, StringComparison.OrdinalIgnoreCase))
                    {
                        return new SnapshotHtmlMetadataResult(
                            html,
                            false,
                            $"The document already declares {name}={existingContent ?? "<missing>"}, which conflicts with {content}.");
                    }

                    continue;
                }

                var meta = document.CreateElement("meta");
                meta.SetAttribute("name", name);
                meta.SetAttribute("content", content);
                head.AppendChild(meta);
                changed = true;
            }

            if (!changed)
            {
                return new SnapshotHtmlMetadataResult(html, false);
            }

            var serialized = SerializeDocument(document);
            var verification = parser.ParseDocument(serialized);
            if (!HasRequiredMetadataExactlyOnce(verification))
            {
                return new SnapshotHtmlMetadataResult(html, false, "The metadata declarations did not survive HTML serialization exactly once.");
            }

            var verifiedFingerprint = CreateSemanticFingerprint(verification);
            if (!originalFingerprint.Equals(verifiedFingerprint, StringComparison.Ordinal))
            {
                return new SnapshotHtmlMetadataResult(html, false, "HTML semantics changed during the metadata round trip.");
            }

            return new SnapshotHtmlMetadataResult(serialized, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new SnapshotHtmlMetadataResult(html, false, $"HTML metadata annotation failed safely: {exception.Message}");
        }
    }

    private static bool HasRequiredMetadataExactlyOnce(IDocument document)
    {
        foreach (var (name, content) in RequiredMetadata)
        {
            var matches = document.QuerySelectorAll("meta[name]")
                .Where(element => string.Equals(element.GetAttribute("name")?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1 ||
                !string.Equals(matches[0].GetAttribute("content")?.Trim(), content, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string CreateSemanticFingerprint(IDocument document)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendNode(document, hash);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendNode(INode node, IncrementalHash hash)
    {
        if (node is IElement element && IsManagedMetadata(element))
        {
            return;
        }

        Append(hash, ((int)node.NodeType).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, node.NodeName);

        if (node is IElement current)
        {
            foreach (var attribute in current.Attributes
                         .OrderBy(static attribute => attribute.NamespaceUri ?? string.Empty, StringComparer.Ordinal)
                         .ThenBy(static attribute => attribute.Name, StringComparer.Ordinal))
            {
                Append(hash, attribute.NamespaceUri ?? string.Empty);
                Append(hash, attribute.Name);
                Append(hash, attribute.Value);
            }
        }
        else if (node is ICharacterData characterData)
        {
            Append(hash, characterData.Data);
        }
        else if (node is IDocumentType documentType)
        {
            Append(hash, documentType.Name);
            Append(hash, documentType.PublicIdentifier ?? string.Empty);
            Append(hash, documentType.SystemIdentifier ?? string.Empty);
        }

        foreach (var child in node.ChildNodes)
        {
            AppendNode(child, hash);
        }
    }

    private static bool IsManagedMetadata(IElement element)
    {
        if (!element.LocalName.Equals("meta", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = element.GetAttribute("name")?.Trim();
        return RequiredMetadata.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static string SerializeDocument(IDocument document)
    {
        var root = document.DocumentElement ?? throw new InvalidDataException("The parsed HTML did not contain a document element.");
        var doctype = document.Doctype is null
            ? string.Empty
            : $"<!doctype {document.Doctype.Name}>\n";
        return doctype + root.OuterHtml;
    }
}
