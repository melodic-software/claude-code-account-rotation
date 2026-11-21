using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Ports;

/// <summary>
/// Logs one parked account in by driving the unmodified CLI under that
/// account's own config directory. Out of process, so a port.
/// <para>
/// The one-time code travels to the child's standard input and nowhere else: it
/// is never an argument, never a log line, and never part of a returned
/// message.
/// </para>
/// </summary>
public interface ILoginSessionRunner
{
    /// <summary>
    /// Starts a login for <paramref name="email"/> under
    /// <paramref name="folderPath"/> and returns once the sign-in URL has been
    /// captured. A failure carries the reason; no session is left running.
    /// </summary>
    Task<Result<LoginSession, string>> StartAsync(AccountEmail email, string folderPath, CancellationToken cancellationToken);

    /// <summary>
    /// Feeds the one-time code to the child and returns where the session
    /// stands once the CLI has answered: completed, or still pending with the
    /// rejection message so the operator can paste again.
    /// </summary>
    Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken);

    /// <summary>Where a session stands, or null when no session has that id.</summary>
    LoginSession? Status(LoginSessionId id);

    /// <summary>
    /// Whether a login is in flight against <paramref name="folderPath"/>. A
    /// login owns its folder for the whole of its ten-minute window: the child
    /// writes a credential pair into it at a moment nothing here chooses, so
    /// every other operation that would move, delete, or log out what is in that
    /// folder asks this first and refuses rather than racing the child.
    /// </summary>
    bool IsRunningAgainst(string folderPath);
}
