namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>
/// A CI funding role: a secret-name prefix and the organization or repository
/// that holds its secrets. Its token secret is <c>&lt;PREFIX&gt;_CLAUDE_CODE_OAUTH_TOKEN</c>
/// and its key secret <c>&lt;PREFIX&gt;_CLAUDE_API_KEY</c>.
/// <para>
/// Two roles have a token half only. The empty prefix is the legacy role, the
/// bare <c>CLAUDE_CODE_OAUTH_TOKEN</c>, since no bare <c>CLAUDE_API_KEY</c>
/// secret exists. <see cref="TokenSecretOverride"/> keeps an earlier roster's
/// secret whose name does not end in <c>CLAUDE_CODE_OAUTH_TOKEN</c>, under that
/// literal name. Every part compares without case, the way GitHub does.
/// </para>
/// </summary>
public sealed record CiRole(string Prefix, CiSecretScope Scope, string Owner, string? TokenSecretOverride = null)
{
    public const string TokenSuffix = CiTokenSecret.DefaultName;

    public const string KeySuffix = "CLAUDE_API_KEY";

    public const string LegacyName = "legacy (review lanes)";

    /// <summary>Whether the role has a key secret beside its token secret.</summary>
    public bool HasKeyHalf => TokenSecretOverride is null && Prefix.Length > 0;

    public string TokenSecretName => TokenSecretOverride ?? (Prefix.Length == 0 ? TokenSuffix : Prefix + "_" + TokenSuffix);

    public string? KeySecretName => HasKeyHalf ? Prefix + "_" + KeySuffix : null;

    /// <summary>What the page calls the role.</summary>
    public string Name => TokenSecretOverride ?? (Prefix.Length == 0 ? LegacyName : Prefix);

    public CiTokenSecret TokenSecret => new(TokenSecretName, Scope, Owner);

    /// <summary>
    /// The role a token secret belongs to: the prefix before
    /// <c>_CLAUDE_CODE_OAUTH_TOKEN</c>, the legacy role for the bare name, and a
    /// token-only role under its literal name for anything else.
    /// </summary>
    public static CiRole FromTokenSecret(CiTokenSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.Equals(secret.Name, TokenSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return new CiRole(string.Empty, secret.Scope, secret.Owner);
        }

        const string separated = "_" + TokenSuffix;
        return secret.Name.Length > separated.Length && secret.Name.EndsWith(separated, StringComparison.OrdinalIgnoreCase)
            ? new CiRole(secret.Name[..^separated.Length], secret.Scope, secret.Owner)
            : new CiRole(string.Empty, secret.Scope, secret.Owner, secret.Name);
    }

    /// <summary>
    /// Validates a role named by its prefix, or by its token secret's name when
    /// <paramref name="tokenSecret"/> is given, held to the rules
    /// <see cref="CiTokenSecret.Parse"/> holds every secret name to.
    /// </summary>
    public static Result<CiRole, string> Parse(string? prefix, string? tokenSecret, string? repository, string? organization)
    {
        string name = tokenSecret ?? ((prefix ?? string.Empty).Trim() is { Length: > 0 } trimmed ? trimmed + "_" + TokenSuffix : TokenSuffix);
        Result<CiTokenSecret, string> secret = CiTokenSecret.Parse(name, repository, organization);
        if (secret.IsFailure)
        {
            return Result<CiRole, string>.Failure(secret.Error);
        }

        CiRole role = FromTokenSecret(secret.Value);
        return role.KeySecretName is string key && CiTokenSecret.Parse(key, repository, organization).IsFailure
            ? Result<CiRole, string>.Failure("the key secret " + key + " would be longer than GitHub allows")
            : Result<CiRole, string>.Success(role);
    }

    public bool Equals(CiRole? other) =>
        other is not null
        && Scope == other.Scope
        && string.Equals(Prefix, other.Prefix, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(TokenSecretOverride, other.TokenSecretOverride, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        Scope,
        StringComparer.OrdinalIgnoreCase.GetHashCode(Prefix),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Owner),
        TokenSecretOverride is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(TokenSecretOverride));

    public override string ToString() => Name + (Scope == CiSecretScope.Repository ? " in " : " in org ") + Owner;
}
