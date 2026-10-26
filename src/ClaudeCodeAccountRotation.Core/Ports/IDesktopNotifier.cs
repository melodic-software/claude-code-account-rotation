namespace ClaudeCodeAccountRotation.Core.Ports;

/// <summary>
/// A notification on this machine's desktop. Out of process, so a port. A
/// failure carries the reason; nothing about an alert depends on it showing.
/// </summary>
public interface IDesktopNotifier
{
    Task<Result<Unit, string>> ShowAsync(string title, string body, CancellationToken cancellationToken);
}
