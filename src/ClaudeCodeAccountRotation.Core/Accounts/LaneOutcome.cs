using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>
/// What the tool reads from one lane-outcome record: its <paramref name="Class"/>
/// (null on a success or a legacy line with none) and the secret name the lane
/// ran on.
/// </summary>
public sealed record LaneOutcome(string? Class, string Credential);

/// <summary>
/// The one place a lane-outcome record is parsed. ci-workflows owns the
/// record, v1: an annotation message holds a human sentence, then the marker
/// <c>lane-outcome </c> and one JSON object, then any free text. Older
/// ci-workflows writes only <c>key=value</c> tokens (<c>class=</c>,
/// <c>credential=</c>) in the sentence, and the contract keeps them there, so
/// both are read: the JSON first, the tokens when it cannot be read. Unknown
/// fields and class values are ignored; another <c>v</c> is unreadable JSON.
/// </summary>
public static partial class LaneOutcomes
{
    public const string Marker = "lane-outcome ";

    /// <summary>The classes that are limit stops: a subscription limit and a key's credit stop.</summary>
    public static readonly IReadOnlySet<string> LimitClasses = new HashSet<string>(StringComparer.Ordinal) { "rate-limit", "credit-stop" };

    /// <summary>
    /// Free text the lane appends, which may quote another record; nothing
    /// from this phrase on is read.
    /// </summary>
    private const string SdkResult = "Last SDK result:";

    /// <summary>
    /// The record in <paramref name="message"/>, or null when it holds none:
    /// no readable JSON and no legacy <c>credential=</c> token, or a credential
    /// that does not match the emitter's credential pattern.
    /// </summary>
    public static LaneOutcome? Parse(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        int end = message.IndexOf(SdkResult, StringComparison.Ordinal);
        string text = end < 0 ? message : message[..end];
        int marker = text.IndexOf(Marker, StringComparison.Ordinal);
        return (marker < 0 ? null : FromJson(text[(marker + Marker.Length)..])) ?? FromTokens(text);
    }

    /// <summary>Exactly one JSON value at the start of <paramref name="tail"/>, whatever follows it.</summary>
    private static LaneOutcome? FromJson(string tail)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(tail));
            using var document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int v) || v != 1
                || !root.TryGetProperty("credential", out JsonElement credential)
                || credential.ValueKind != JsonValueKind.Object
                || Text(credential, "secret") is not string secret
                || !SecretName().IsMatch(secret))
            {
                return null;
            }

            return new LaneOutcome(Text(root, "class"), secret);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The legacy whole tokens <c>class=</c> and <c>credential=</c>; a
    /// credential loses trailing <c>)</c>, <c>.</c>, <c>,</c> and <c>;</c>,
    /// which a sentence around it may add.
    /// </summary>
    private static LaneOutcome? FromTokens(string text)
    {
        string? kind = null;
        string? credential = null;
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("class=", StringComparison.Ordinal))
            {
                kind = token["class=".Length..];
            }
            else if (token.StartsWith("credential=", StringComparison.Ordinal))
            {
                credential = token["credential=".Length..].TrimEnd(')', '.', ',', ';');
            }
        }

        return credential is not null && SecretName().IsMatch(credential) ? new LaneOutcome(kind, credential) : null;
    }

    /// <summary>
    /// The credential pattern of the record's emitter, ci-workflows
    /// <c>report-lane-outcome</c> (<c>CREDENTIAL_PATTERN</c> in its
    /// <c>classify.cjs</c>), which its JSON Schema also gives <c>credential.secret</c>.
    /// </summary>
    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();
}
