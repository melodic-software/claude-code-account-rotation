using System.Net;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// <c>--read-ci-usage</c> against the instance file's URL and token. The
/// request it must send is the one the rate-limit stop hook sends
/// (<c>hooks/rate-limit-stop.sh</c>): a bearer token and the mutation header,
/// here with <c>?wait=true</c> so the answer is the finished table.
/// </summary>
public sealed class CiUsageTriggerTests
{
    /// <summary>
    /// A finished answer in the route's wire shape: camel-cased the way the host
    /// writes every record, one account backing two roles, read this pass, with
    /// a known 5-hour row, a 7-day row whose window has reset, and an unknown
    /// scoped row.
    /// </summary>
    private const string Finished = """
        {
          "finished": true,
          "accounts": [
            {
              "index": 2,
              "roles": ["AUTOMATIONS", "REVIEW"],
              "email": "ci@example.com",
              "refresh": { "state": "read", "message": "Read just now.", "retryAt": null },
              "usage": {
                "source": "refresh",
                "capturedAt": "2026-10-27T12:00:00+00:00",
                "limits": [
                  { "kind": "session", "label": "5-hour", "percent": 42.5, "resetsAt": "2026-10-27T14:30:00+00:00", "severity": null, "source": null, "capturedAt": null, "known": true, "windowReset": false },
                  { "kind": "weekly_all", "label": "7-day", "percent": null, "resetsAt": "2026-10-20T09:00:00+00:00", "severity": null, "source": null, "capturedAt": null, "known": true, "windowReset": true },
                  { "kind": "weekly_scoped", "label": "scoped", "percent": null, "resetsAt": null, "severity": null, "source": null, "capturedAt": null, "known": false, "windowReset": false }
                ],
                "credits": null
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task ItPostsTheCiRoleRouteWithTheTokenAndTheMutationHeaderAndWaitsForTheTable()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, Finished));
        using HttpClient client = new(handler);

        CiUsageReport report = (await CiUsageTrigger.PostAsync(client, RotationRole.Leader, ("http://127.0.0.1:48211", "secret-token"), TestContext.Current.CancellationToken)).Value;

        HttpRequestMessage sent = handler.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.RequestUri.ShouldBe(new Uri("http://127.0.0.1:48211/api/refresh/ci-roles?wait=true"));
        sent.Headers.Authorization!.ToString().ShouldBe("Bearer secret-token");
        sent.Headers.GetValues("X-Claude-Code-Account-Rotation").ShouldBe(["1"]);
        CiUsageRow row = report.Accounts.Single();
        row.Index.ShouldBe(2);
        row.Roles.ShouldBe(["AUTOMATIONS", "REVIEW"]);
        row.Usage.Limits[0].Percent.ShouldBe(42.5);
    }

    [Fact]
    public async Task TheTablePrintsNoEmailUnlessAskedFor()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, Finished));
        using HttpClient client = new(handler);
        CiUsageReport report = (await CiUsageTrigger.PostAsync(client, RotationRole.Leader, ("http://127.0.0.1:48211", "t"), TestContext.Current.CancellationToken)).Value;

        string table = CiUsageTrigger.Format(report, emails: false);

        table.ShouldNotContain("ci@example.com");
        table.ShouldNotContain("@");
        table.ShouldBe(
            "#  Roles               Read  Usage" + Environment.NewLine
            + "2  AUTOMATIONS,REVIEW  read  5-hour 42.5% resets 2026-10-27 14:30Z, 7-day reset, scoped unknown");
    }

    [Fact]
    public async Task EmailsAddTheEmailColumn()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, Finished));
        using HttpClient client = new(handler);
        CiUsageReport report = (await CiUsageTrigger.PostAsync(client, RotationRole.Leader, ("http://127.0.0.1:48211", "t"), TestContext.Current.CancellationToken)).Value;

        CiUsageTrigger.Format(report, emails: true).ShouldBe(
            "#  Roles               Email           Read  Usage" + Environment.NewLine
            + "2  AUTOMATIONS,REVIEW  ci@example.com  read  5-hour 42.5% resets 2026-10-27 14:30Z, 7-day reset, scoped unknown");
    }

    [Fact]
    public void NoCiRoleAccountSaysSo()
    {
        CiUsageTrigger.Format(new CiUsageReport(Finished: true, []), emails: false).ShouldBe("No account on the roster backs a CI role.");
    }

    [Fact]
    public async Task APassStillRunningWhenTheInstanceStopsWaitingIsATimeout()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.Accepted, """{"finished":false,"accounts":[]}"""));
        using HttpClient client = new(handler);

        (await CiUsageTrigger.PostAsync(client, RotationRole.Leader, ("http://127.0.0.1:48211", "t"), TestContext.Current.CancellationToken)).Error
            .ShouldBe("the read started but had not finished after 3 minutes; it is still running, and the dashboard shows each account as it lands");
    }

    [Fact]
    public async Task AnInstanceThatIgnoresTheWaitIsNotReportedAsATimeout()
    {
        // A release before this one answers the route at once with its 202 body.
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.Accepted, """{"started":true}"""));
        using HttpClient client = new(handler);

        (await CiUsageTrigger.PostAsync(client, RotationRole.Leader, ("http://127.0.0.1:48211", "t"), TestContext.Current.CancellationToken)).Error
            .ShouldBe("the instance started the read but does not wait for it; upgrade it to this release to get the table");
    }

    [Fact]
    public async Task ARefusalCarriesTheInstancesMessage()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.Conflict, """{"refusal":"RefreshInProgress","message":"A refresh is already running"}"""));
        using HttpClient client = new(handler);

        (await CiUsageTrigger.PostAsync(client, RotationRole.Leader, ("http://127.0.0.1:48211", "t"), TestContext.Current.CancellationToken)).Error
            .ShouldBe("the instance refused the read (409): A refresh is already running");
    }

    [Fact]
    public async Task NoRunningInstanceSendsNothing()
    {
        using RecordingHandler handler = new();
        using HttpClient client = new(handler);

        (await CiUsageTrigger.PostAsync(client, RotationRole.Leader, null, TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheFollowerRefusesAndSendsNothing()
    {
        using RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.NotFound));
        using HttpClient client = new(handler);

        (await CiUsageTrigger.PostAsync(client, RotationRole.Follower, ("http://127.0.0.1:48212", "t"), TestContext.Current.CancellationToken)).Error
            .ShouldBe("this is the follower, which runs no usage read; run --read-ci-usage with the leader's executable");
        handler.Requests.ShouldBeEmpty();
    }
}
