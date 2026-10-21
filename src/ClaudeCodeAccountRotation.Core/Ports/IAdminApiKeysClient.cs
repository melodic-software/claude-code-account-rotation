namespace ClaudeCodeAccountRotation.Core.Ports;

/// <summary>
/// One API key as the Admin API lists it: metadata only, never the key. The
/// hint is partly redacted and may be null.
/// </summary>
public sealed record ListedApiKey(string Id, string Name, string Status, string? PartialKeyHint, DateTimeOffset? ExpiresAt);

/// <summary>Why a listing could not be had.</summary>
public enum AdminApiFailureKind
{
    /// <summary>A 401 or 403: the key is not an Admin key, or the account is an individual one with no Admin API.</summary>
    Unavailable,

    /// <summary>A 429. The check ends there, with no retry.</summary>
    RateLimited,

    /// <summary>Anything else: no answer, another status, or a body that did not read.</summary>
    Failed,
}

public sealed record AdminApiFailure(AdminApiFailureKind Kind, string Message);

/// <summary>
/// The Admin API's list of the organization's API keys, read with an Admin
/// key, only when the operator clicks Check. Metadata only: this is not a
/// model API call.
/// </summary>
public interface IAdminApiKeysClient
{
    Task<Result<IReadOnlyList<ListedApiKey>, AdminApiFailure>> ListAsync(string adminKey, CancellationToken cancellationToken);
}
