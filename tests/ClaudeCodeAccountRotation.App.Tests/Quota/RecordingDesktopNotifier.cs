using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>A desktop that records each notification instead of showing it, failing when told to.</summary>
internal sealed class RecordingDesktopNotifier : IDesktopNotifier
{
    private readonly Lock _mutex = new();
    private readonly List<(string Title, string Body)> _shown = [];

    public IReadOnlyList<(string Title, string Body)> Shown
    {
        get
        {
            lock (_mutex)
            {
                return [.. _shown];
            }
        }
    }

    public string? Failure { get; init; }

    public Task<Result<Unit, string>> ShowAsync(string title, string body, CancellationToken cancellationToken)
    {
        lock (_mutex)
        {
            _shown.Add((title, body));
        }

        return Task.FromResult(Failure is null ? Result<Unit, string>.Success(Unit.Value) : Result<Unit, string>.Failure(Failure));
    }
}
