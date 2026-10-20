using System.Collections.Immutable;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>One secret as GitHub lists it: its name and when its value last changed. Never its value.</summary>
public sealed record ListedSecret(string Name, DateTimeOffset UpdatedAt);

/// <summary>Every secret one organization listed, read to the last page.</summary>
public sealed record OrganizationSecrets(string Organization, IReadOnlyList<ListedSecret> Secrets);

/// <summary>A role found in an organization's secrets, with the secret behind each half it has.</summary>
public sealed record RoleSecrets(CiRole Role, ListedSecret? Token, ListedSecret? Key)
{
    public bool HasToken => Token is not null;

    public bool HasKey => Key is not null;
}

/// <summary>
/// The CI funding roles that organizations' secret names imply: every prefix
/// with a <c>&lt;PREFIX&gt;_CLAUDE_CODE_OAUTH_TOKEN</c> or a
/// <c>&lt;PREFIX&gt;_CLAUDE_API_KEY</c> secret, and the legacy role when the bare
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c> exists. Any other secret names no role, but
/// stays findable by name, for a role the roster keeps under a literal name.
/// </summary>
public sealed class RoleCatalog
{
    private readonly ImmutableDictionary<string, ImmutableDictionary<string, ListedSecret>> _secrets;

    private RoleCatalog(
        ImmutableDictionary<CiRole, RoleSecrets> roles,
        ImmutableDictionary<string, ImmutableDictionary<string, ListedSecret>> secrets)
    {
        Roles = roles;
        _secrets = secrets;
    }

    /// <summary>The roles found, keyed by role: its prefix and its organization, without case.</summary>
    public ImmutableDictionary<CiRole, RoleSecrets> Roles { get; }

    /// <summary>The organizations whose secrets were read in full.</summary>
    public IEnumerable<string> Organizations => _secrets.Keys;

    public static RoleCatalog From(IEnumerable<OrganizationSecrets> organizations)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        Dictionary<CiRole, RoleSecrets> roles = [];
        ImmutableDictionary<string, ImmutableDictionary<string, ListedSecret>>.Builder secrets =
            ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, ListedSecret>>(StringComparer.OrdinalIgnoreCase);
        foreach (OrganizationSecrets organization in organizations)
        {
            ImmutableDictionary<string, ListedSecret>.Builder byName = ImmutableDictionary.CreateBuilder<string, ListedSecret>(StringComparer.OrdinalIgnoreCase);
            foreach (ListedSecret secret in organization.Secrets)
            {
                byName[secret.Name] = secret;
                if (Half(secret.Name) is not (string prefix, bool isKey))
                {
                    continue;
                }

                CiRole role = new(prefix, CiSecretScope.Organization, organization.Organization);
                RoleSecrets held = roles.GetValueOrDefault(role) ?? new RoleSecrets(role, null, null);
                roles[role] = isKey ? held with { Key = secret } : held with { Token = secret };
            }

            secrets[organization.Organization] = byName.ToImmutable();
        }

        return new RoleCatalog(roles.ToImmutableDictionary(), secrets.ToImmutable());
    }

    /// <summary>Whether <paramref name="role"/>'s secrets were read, so that one absent from them is gone.</summary>
    public bool Covers(CiRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.Scope == CiSecretScope.Organization && _secrets.ContainsKey(role.Owner);
    }

    /// <summary>The listed secret of that name in that organization, or null when it was not listed.</summary>
    public ListedSecret? Find(string organization, string name) =>
        _secrets.TryGetValue(organization, out ImmutableDictionary<string, ListedSecret>? byName)
            ? byName.GetValueOrDefault(name)
            : null;

    /// <summary>The role prefix a secret name is a half of, and which half, or null for any other name.</summary>
    private static (string Prefix, bool IsKey)? Half(string name)
    {
        if (string.Equals(name, CiRole.TokenSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, false);
        }

        foreach ((string suffix, bool isKey) in new[] { ("_" + CiRole.TokenSuffix, false), ("_" + CiRole.KeySuffix, true) })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return (name[..^suffix.Length], isKey);
            }
        }

        return null;
    }
}
