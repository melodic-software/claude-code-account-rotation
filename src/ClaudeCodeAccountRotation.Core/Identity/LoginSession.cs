namespace ClaudeCodeAccountRotation.Core.Identity;

/// <summary>
/// The handle one browser-assisted login is addressed by. Opaque to the page,
/// which only echoes it back on the code and status calls.
/// </summary>
public readonly record struct LoginSessionId(string Value)
{
    public static LoginSessionId New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}

/// <summary>
/// Where a login stands. <see cref="Pending"/> covers a rejected code too: the
/// CLI keeps the prompt open, so a bad paste is a retry inside the session's
/// expiry rather than a dead session.
/// </summary>
public enum LoginSessionState
{
    Pending,
    Completed,
    Failed,
    Expired,
}

/// <summary>
/// One login in flight. Carries no token material and no submitted code: the
/// sign-in URL is the authorize URL the CLI printed with the account as its
/// <c>login_hint</c> (<see cref="SignInUrl"/>), and the message comes from
/// a fixed vocabulary rather than from the child's output.
/// </summary>
public sealed record LoginSession(
    LoginSessionId Id,
    AccountEmail Email,
    Uri? SignInUrl,
    LoginSessionState State,
    string? Message,
    DateTimeOffset ExpiresAt);
