using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>What the operator needs to know about a found key.</summary>
public enum ApiKeyWarning
{
    Archived,
    Inactive,
    Expired,

    /// <summary>Active, and <c>expires_at</c> falls within <see cref="ApiKeyStanding.ExpiryWarning"/>.</summary>
    ExpiresSoon,
}

/// <summary>A listed key's status read against the clock.</summary>
public static class ApiKeyStanding
{
    /// <summary>
    /// How close an expiry has to be before the check warns. Basis: judgment,
    /// the same 30 days the page warns a CI token in before it lapses.
    /// </summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    /// <summary>
    /// The flag for the key, or null when it is active and not near its
    /// expiry. A passed <c>expires_at</c> is expired whatever the status says.
    /// </summary>
    public static ApiKeyWarning? Of(ListedApiKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Status switch
        {
            "archived" => ApiKeyWarning.Archived,
            "inactive" => ApiKeyWarning.Inactive,
            "expired" => ApiKeyWarning.Expired,
            _ when key.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= now => ApiKeyWarning.Expired,
            _ when key.ExpiresAt is DateTimeOffset expiresAt && expiresAt - now <= ExpiryWarning => ApiKeyWarning.ExpiresSoon,
            _ => null,
        };
    }
}
