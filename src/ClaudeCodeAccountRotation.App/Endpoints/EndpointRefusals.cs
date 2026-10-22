using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.AspNetCore.Http;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>The refusals more than one route answers with, worded once.</summary>
internal static class EndpointRefusals
{
    /// <summary>A 409 naming the refusal and saying why, which the page shows as it is.</summary>
    public static IResult Refused(string refusal, string message) =>
        Results.Json(new SwitchRefusalView(refusal, message), statusCode: StatusCodes.Status409Conflict);

    /// <summary>A route that opens the account's mapped browser profile, for an account the roster does not name.</summary>
    public static IResult NotOnRosterForBrowser(AccountEmail email) =>
        Refused("NotOnRoster", email.Value + " is not on the roster; add it first so its browser mapping is known.");
}
