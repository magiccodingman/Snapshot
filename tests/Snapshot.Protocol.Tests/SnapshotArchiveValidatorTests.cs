using Snapshot.Protocol.Validation;
using Xunit;

namespace Snapshot.Protocol.Tests;

public sealed class SnapshotArchiveValidatorTests
{
    [Fact]
    public void ReadinessElementDetectionUsesHtmlSemanticsNotScriptText()
    {
        const string scriptLiteral = """
            <!doctype html>
            <html>
            <body>
              <main>Captured content</main>
              <script>
                const template = `<snapshot-ready><title>Late route</title></snapshot-ready>`;
              </script>
            </body>
            </html>
            """;
        const string liveElement = """
            <!doctype html>
            <html>
            <body>
              <snapshot-ready><title>Unconsumed</title></snapshot-ready>
            </body>
            </html>
            """;

        Assert.False(SnapshotArchiveValidator.ContainsReadinessElement(scriptLiteral));
        Assert.True(SnapshotArchiveValidator.ContainsReadinessElement(liveElement));
    }
}
