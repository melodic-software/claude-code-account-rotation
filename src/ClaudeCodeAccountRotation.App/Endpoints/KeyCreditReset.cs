using System.Globalization;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// After a key push, the organization variable <see cref="Name"/> gets the
/// current UTC time, so the CI key-credit gate (ci-workflows
/// <c>check-key-credit</c>, input <c>reset-at</c>) drops the credit stops
/// before it and the next lane run tries the new key at once. Like the kill
/// switches, it is a PATCH of a variable that already exists, sending back the
/// visibility read just before; nothing is written when that read fails, and
/// the variable is never created. Whatever happens here, the push it follows
/// keeps its result: this only logs.
/// </summary>
internal static partial class KeyCreditReset
{
    public const string Name = "KEY_CREDIT_RESET_AT";

    /// <summary>ISO-8601 UTC to the second, the shape the gate accepts.</summary>
    public static string Stamp(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// One PATCH per organization in <paramref name="organizations"/>, read
    /// and written one at a time; a rate limit stops the rest.
    /// </summary>
    public static async Task MarkAsync(
        IEnumerable<string> organizations,
        IGhApiReader reader,
        IGhVariableWriter writer,
        TimeProvider clock,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        foreach (string organization in organizations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Not the request's token: the key is already set, so a page that
            // goes away must not leave the reset half done.
            Result<VariableVisibility, GhApiFailure> visibility = await reader.GetOrganizationVariableVisibilityAsync(organization, Name, CancellationToken.None);
            if (visibility.IsFailure)
            {
                if (visibility.Error.Status == 404 && !visibility.Error.RateLimited)
                {
                    LogAbsent(logger, Name, organization);
                }
                else
                {
                    LogNotSet(logger, Name, organization, visibility.Error.Message);
                }

                if (visibility.Error.RateLimited)
                {
                    return;
                }

                continue;
            }

            string stamp = Stamp(clock.GetUtcNow());
            Result<Unit, GhApiFailure> set = await writer.SetOrganizationVariableAsync(organization, Name, stamp, visibility.Value, CancellationToken.None);
            if (set.IsFailure)
            {
                LogNotSet(logger, Name, organization, set.Error.Message);
                if (set.Error.RateLimited)
                {
                    return;
                }

                continue;
            }

            LogSet(logger, Name, organization, stamp);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} in org {Organization} set to {Stamp}")]
    private static partial void LogSet(ILogger logger, string name, string organization, string stamp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name} is not set in org {Organization} or not visible to this sign-in, so the key-credit backoff was not reset; the tool never creates it")]
    private static partial void LogAbsent(ILogger logger, string name, string organization);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name} in org {Organization} not reset: {Reason}")]
    private static partial void LogNotSet(ILogger logger, string name, string organization, string reason);
}
