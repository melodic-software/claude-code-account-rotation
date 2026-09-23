using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Dashboard;

/// <summary>
/// Remembers the one transition the live credential file can make from a parsed
/// pair to a file that lacks tokens. The dashboard refresh and the state-file
/// repair already read that file; this records what those reads find and does
/// not poll on its own.
/// <para>
/// A missing file and a half-written file are not the transition. A later read
/// that still lacks tokens does not log again and does not move the recorded
/// time. A later read that parses a pair clears the record, so a following
/// disappearance can be recorded again.
/// </para>
/// </summary>
internal sealed partial class CliLogoutMonitor(
    ClaudeStateFile stateFile,
    SwitchOptions options,
    TimeProvider timeProvider,
    ILogger<CliLogoutMonitor> logger)
{
    private readonly object _gate = new();
    private bool _sawPair;
    private bool _logged;
    private DateTimeOffset? _loggedOutAt;

    /// <summary>When the logout was recorded, or null when none stands.</summary>
    public DateTimeOffset? LoggedOutAt
    {
        get
        {
            lock (_gate)
            {
                return _loggedOutAt;
            }
        }
    }

    /// <summary>Whether <paramref name="exception"/> is the lacks-tokens failure. The message also names the file, so callers do not log it.</summary>
    public static bool IsTokenAbsence(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Message.Contains(CredentialPair.LacksTokensReason, StringComparison.Ordinal);
    }

    /// <summary>A live read parsed a pair. Clears a recorded logout.</summary>
    public void ObservePair()
    {
        lock (_gate)
        {
            _sawPair = true;
            _loggedOutAt = null;
            _logged = false;
        }
    }

    /// <summary>
    /// A live read failed because the file lacks tokens. Records the time and
    /// logs once when a pair was parsed before this, naming the account when
    /// the state file or the owner record still has an address.
    /// </summary>
    public async Task ObserveTokenAbsenceAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_sawPair)
            {
                return;
            }

            if (_loggedOutAt is null)
            {
                _loggedOutAt = timeProvider.GetUtcNow();
            }

            if (_logged)
            {
                return;
            }
        }

        string? email = await AccountAsync(cancellationToken);
        lock (_gate)
        {
            // A pair parsed while the address was being read, and cleared the record.
            if (_loggedOutAt is null || _logged)
            {
                return;
            }

            _logged = true;
        }

        if (email is null)
        {
            LogLoggedOut();
        }
        else
        {
            LogLoggedOutOf(email);
        }
    }

    /// <summary>
    /// The card sentence for a recorded logout, measured against
    /// <paramref name="now"/>. Same UTC day, the time of day; any other day, the
    /// date with it, because a time alone reads as today.
    /// </summary>
    public string? Sentence(DateTimeOffset now)
    {
        if (LoggedOutAt is not DateTimeOffset loggedOutAt)
        {
            return null;
        }

        DateTime utc = loggedOutAt.UtcDateTime;
        string clock = utc.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (loggedOutAt.UtcDateTime.Date == now.UtcDateTime.Date)
        {
            return "The CLI logged out at " + clock + ".";
        }

        string date = utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return "The CLI logged out at " + clock + " on " + date + ".";
    }

    private async Task<string?> AccountAsync(CancellationToken cancellationToken)
    {
        string? fromState = await StateEmailAsync(cancellationToken);
        return fromState ?? await OwnerEmailAsync(cancellationToken);
    }

    private async Task<string?> StateEmailAsync(CancellationToken cancellationToken)
    {
        try
        {
            OAuthAccountBlock? block = await stateFile.ReadAccountBlockAsync(cancellationToken);
            return block?.Email?.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<string?> OwnerEmailAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(options.AppDataDirectory, "state", "live-owner.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = await SharedFileReader.ReadAllBytesAsync(path, cancellationToken);
            if (JsonNode.Parse(bytes) is not JsonObject record
                || record["email"] is not JsonValue value
                || !value.TryGetValue(out string? email)
                || email is null)
            {
                return null;
            }

            return AccountEmail.Parse(email).Match(static parsed => parsed.Value, static _ => (string?)null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "the CLI logged out of {Account}")]
    private partial void LogLoggedOutOf(string account);

    [LoggerMessage(Level = LogLevel.Error, Message = "the CLI logged out")]
    private partial void LogLoggedOut();
}
