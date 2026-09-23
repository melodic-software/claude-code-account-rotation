using System.Text.Json.Nodes;

namespace ClaudeCodeAccountRotation.Core.Identity;

/// <summary>
/// One account's OAuth credential pair as Claude Code stores it. In-memory
/// only. The token strings are read by the credential store and the token
/// refresh client and by nothing else; no rendering of this type includes them.
/// </summary>
public sealed record CredentialPair
{
    private CredentialPair(
        JsonObject raw,
        string accessToken,
        string refreshToken,
        DateTimeOffset accessTokenExpiresAt,
        DateTimeOffset? loginExpiresAt,
        IReadOnlyList<string> scopes)
    {
        Raw = raw;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        LoginExpiresAt = loginExpiresAt;
        Scopes = scopes;
        Fingerprint = RefreshTokenFingerprint.FromRefreshToken(refreshToken);
    }

    public JsonObject Raw { get; }

    public string AccessToken { get; }

    public string RefreshToken { get; }

    public DateTimeOffset AccessTokenExpiresAt { get; }

    /// <summary>When the refresh token itself expires: the login's own lifetime.</summary>
    public DateTimeOffset? LoginExpiresAt { get; }

    public RefreshTokenFingerprint Fingerprint { get; }

    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// The failure a file returns when <c>accessToken</c> or <c>refreshToken</c>
    /// is absent or empty. Callers treat this string as the logout signal and
    /// do not log it back.
    /// </summary>
    public const string LacksTokensReason = "credential file lacks accessToken or refreshToken";

    /// <summary>
    /// Reads the file shape <c>{ "claudeAiOauth": { accessToken, refreshToken,
    /// expiresAt, refreshTokenExpiresAt?, scopes? } }</c>; epoch milliseconds for
    /// both instants, as the CLI writes them.
    /// </summary>
    public static Result<CredentialPair, string> FromJson(JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw["claudeAiOauth"] is not JsonObject oauth)
        {
            return Result<CredentialPair, string>.Failure("credential file has no claudeAiOauth object");
        }

        string? accessToken = oauth["accessToken"]?.GetValue<string>();
        string? refreshToken = oauth["refreshToken"]?.GetValue<string>();
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            return Result<CredentialPair, string>.Failure(LacksTokensReason);
        }

        if (oauth["expiresAt"] is not JsonValue expiresAtValue || !expiresAtValue.TryGetValue(out long expiresAtMilliseconds))
        {
            return Result<CredentialPair, string>.Failure("credential file lacks a numeric expiresAt");
        }

        DateTimeOffset? loginExpiresAt = oauth["refreshTokenExpiresAt"] is JsonValue loginValue && loginValue.TryGetValue(out long loginMilliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(loginMilliseconds)
            : null;

        List<string> scopes = [];
        if (oauth["scopes"] is JsonArray scopeArray)
        {
            foreach (JsonNode? scope in scopeArray)
            {
                if (scope is JsonValue scopeValue && scopeValue.TryGetValue(out string? scopeText) && scopeText is not null)
                {
                    scopes.Add(scopeText);
                }
            }
        }

        return Result<CredentialPair, string>.Success(new CredentialPair(
            raw,
            accessToken,
            refreshToken,
            DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMilliseconds),
            loginExpiresAt,
            scopes));
    }

    public override string ToString() => "CredentialPair(" + Fingerprint.Sha256Hex[..12] + ")";
}
