using System.Text.Json;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>A live read's answer, and the server's capture time when the CLI gave it.</summary>
internal sealed record LiveRead(Result<JsonDocument, UsageReadFailure> Read, DateTimeOffset? CapturedAt);

/// <summary>
/// One read of the live account's usage, the same on either side: through the
/// CLI first, and through the usage endpoint with the live access token when
/// the CLI gives no fresh answer, with the reason logged. The CLI reads
/// whichever account is live when it runs, so its answer counts only when this
/// side's state file names <c>expected</c> both before and after it ran;
/// otherwise the endpoint is read with the token of the pair the caller already
/// chose, which belongs to that account whatever is live now.
/// <para>
/// The fallback runs under the caller's one budget reservation. A CLI that
/// sent its own request and then failed (a rate limit it does not report, a
/// timeout) makes that turn two requests; every other failure sent nothing.
/// </para>
/// </summary>
internal static partial class LiveUsageRead
{
    public static async Task<LiveRead> ReadAsync(
        ILiveUsageReader? cli,
        IUsageEndpointClient endpoint,
        string accessToken,
        AccountEmail expected,
        Func<CancellationToken, Task<AccountEmail?>> liveAccount,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(liveAccount);
        if (cli is not null)
        {
            string reason;
            if (await liveAccount(cancellationToken) != expected)
            {
                reason = "the live directory no longer holds that account";
            }
            else
            {
                Result<LiveUsageAnswer, string> answer = await cli.ReadAsync(cancellationToken);
                if (answer.IsFailure)
                {
                    reason = answer.Error;
                }
                else if (await liveAccount(cancellationToken) != expected)
                {
                    reason = "the live account changed while the CLI ran, so its figures may be another account's";
                }
                else
                {
                    return new LiveRead(
                        Result<JsonDocument, UsageReadFailure>.Success(JsonSerializer.SerializeToDocument(answer.Value.Body)),
                        answer.Value.CapturedAt);
                }
            }

            LogFellBack(logger, expected.Value, reason);
        }

        return new LiveRead(await endpoint.ReadUsageAsync(accessToken, cancellationToken), CapturedAt: null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "the usage of the live account {Account} was not read through the CLI: {Reason}; reading the usage endpoint instead")]
    private static partial void LogFellBack(ILogger logger, string account, string reason);
}
