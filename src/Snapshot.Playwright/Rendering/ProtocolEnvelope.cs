using System.Text.Json.Serialization;

namespace Snapshot.Playwright.Rendering;

internal sealed class ProtocolEnvelope
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("sessionToken")]
    public string? SessionToken { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("capturedPath")]
    public string? CapturedPath { get; init; }

    [JsonPropertyName("html")]
    public string? Html { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
