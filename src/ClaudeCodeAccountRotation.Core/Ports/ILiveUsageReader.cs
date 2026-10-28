using System.Text.Json.Nodes;

namespace ClaudeCodeAccountRotation.Core.Ports;

/// <summary>
/// The usage of whichever account this side's live config directory holds,
/// read through the unmodified <c>claude</c> binary rather than by the tool's
/// own request. Only the live account is ever read this way: a parked profile
/// folder is never handed to the binary.
/// </summary>
public interface ILiveUsageReader
{
    /// <summary>
    /// The usage answer, or a one-line reason it could not be had (no CLI, a
    /// CLI too old to answer, a refused or stale answer), which the caller logs
    /// before it reads the usage endpoint instead. The reason never quotes what
    /// the child printed.
    /// </summary>
    Task<Result<LiveUsageAnswer, string>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One fresh usage answer: <paramref name="Body"/> in the usage endpoint's own
/// shape (<c>limits[]</c> and <c>extra_usage</c>), so the parser that reads the
/// endpoint reads this too, and the moment the server captured it.
/// </summary>
public sealed record LiveUsageAnswer(JsonObject Body, DateTimeOffset CapturedAt);
