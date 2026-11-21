namespace ClaudeCodeAccountRotation.Core.Ports;

/// <summary>
/// Logs a folder out by running the CLI's own <c>auth logout</c> under that
/// folder before the folder is deleted. Whether <c>claude auth logout</c>
/// revokes a claude.ai refresh token server-side is not documented, so the
/// token may stay valid until it expires. Out of process, so a port.
/// </summary>
public interface IClaudeCliLogout
{
    /// <summary>
    /// Logs out of <paramref name="configDirectory"/>. A failure carries the
    /// diagnostic text; the caller decides whether to proceed without it.
    /// </summary>
    Task<Result<Unit, string>> LogoutAsync(string configDirectory, CancellationToken cancellationToken);
}
