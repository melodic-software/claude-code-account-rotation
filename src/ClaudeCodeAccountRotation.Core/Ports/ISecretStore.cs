using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Ports;

/// <summary>The keys an account can have in the OS store.</summary>
public enum SecretKind
{
    ConsoleApiKey,
    AdminKey,
}

/// <summary>
/// The operating system's own protected store for an account's Console and
/// Admin API keys: Credential Manager on Windows, the Secret Service on Linux.
/// Kept on this machine only, and never in a file of this tool's: a platform
/// with no usable store refuses to keep a key rather than fall back to one.
/// Out of process, so a port.
/// </summary>
public interface ISecretStore
{
    /// <summary>The store's name as the page says it, as in "Stored in Credential Manager".</summary>
    string DisplayName => "the OS store";

    /// <summary>Whether a key could be kept here now, or the cause when it cannot.</summary>
    Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="value"/>, replacing any key of the same kind for the account.</summary>
    Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken);

    /// <summary>The stored key, or null when none is stored.</summary>
    Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the key. Nothing stored is success. A store that cannot be
    /// reached is a failure, because the key may still be in it; the caller
    /// decides, from what the roster records, whether that matters.
    /// </summary>
    Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken);
}

/// <summary>Names shared by every store, so one key has the same name on each platform.</summary>
public static class SecretNames
{
    public const string Product = "claude-code-account-rotation";

    public static string Kind(SecretKind kind) => kind switch
    {
        SecretKind.ConsoleApiKey => "console-api-key",
        SecretKind.AdminKey => "admin-key",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary><c>claude-code-account-rotation:&lt;kind&gt;:&lt;email&gt;</c>.</summary>
    public static string Target(SecretKind kind, AccountEmail email) => Product + ":" + Kind(kind) + ":" + email.Value;
}
