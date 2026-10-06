using System.Text.RegularExpressions;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>Where a GitHub Actions secret lives: one repository, or one organization.</summary>
public enum CiSecretScope
{
    Repository,
    Organization,
}

/// <summary>
/// The GitHub Actions secret an account's <c>claude setup-token</c> token was
/// written to: its name and the repository (<c>owner/name</c>) or organization
/// that holds it. Names and owners compare without case, the way GitHub does.
/// <para>
/// Only these three values are stored. The token itself never is: it travels
/// from the CLI's output to <c>gh</c>'s standard input and nowhere else.
/// </para>
/// </summary>
public sealed partial record CiTokenSecret(string Name, CiSecretScope Scope, string Owner)
{
    /// <summary>The name the Claude Code GitHub Action reads by default.</summary>
    public const string DefaultName = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary>
    /// Validates a secret target by GitHub's own rules: a name of letters, digits
    /// and underscores that starts with neither a digit nor <c>GITHUB_</c>, and
    /// exactly one of a repository (<c>owner/name</c>) or an organization login.
    /// The checks also keep every value safe as a single <c>gh</c> argument.
    /// </summary>
    public static Result<CiTokenSecret, string> Parse(string? name, string? repository, string? organization)
    {
        string trimmedName = (name ?? string.Empty).Trim();
        if (!SecretNamePattern().IsMatch(trimmedName) || trimmedName.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase))
        {
            return Result<CiTokenSecret, string>.Failure(
                "a secret name holds only letters, digits and underscores, starts with neither a digit nor GITHUB_, and is at most 100 characters");
        }

        string? repo = string.IsNullOrWhiteSpace(repository) ? null : repository.Trim();
        string? org = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim();
        if ((repo is null) == (org is null))
        {
            return Result<CiTokenSecret, string>.Failure("name exactly one of a repository (owner/name) or an organization");
        }

        if (repo is not null)
        {
            return RepositoryPattern().IsMatch(repo)
                ? Result<CiTokenSecret, string>.Success(new CiTokenSecret(trimmedName, CiSecretScope.Repository, repo))
                : Result<CiTokenSecret, string>.Failure("a repository is written owner/name");
        }

        return OwnerPattern().IsMatch(org!)
            ? Result<CiTokenSecret, string>.Success(new CiTokenSecret(trimmedName, CiSecretScope.Organization, org!))
            : Result<CiTokenSecret, string>.Failure("an organization is its GitHub login: letters, digits and single hyphens");
    }

    /// <summary>Whether both name the same secret, which is the one an account can back at a time.</summary>
    public bool SameSecretAs(CiTokenSecret? other) =>
        other is not null
        && Scope == other.Scope
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name + (Scope == CiSecretScope.Repository ? " in " : " in org ") + Owner;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,99}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretNamePattern();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}/[A-Za-z0-9._-]{1,100}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OwnerPattern();
}
