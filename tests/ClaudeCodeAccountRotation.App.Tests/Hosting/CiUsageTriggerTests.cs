using System.Net;
using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// <c>--read-ci-usage</c> against the instance file's URL and token. The
/// request it must send is the one the rate-limit stop hook sends
/// (<c>hooks/rate-limit-stop.sh</c>): a bearer token and the mutation header.
/// </summary>
public sealed class CiUsageTriggerTests
{
    [Fact]
    public async Task ItPostsTheCiRoleRouteWithTheTokenAndTheMutationHeader()
    {
        using RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Accepted));
        using HttpClient client = new(handler);

        (await CiUsageTrigger.PostAsync(client, RotationRole.Leader, ("http://127.0.0.1:48211", "secret-token"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        HttpRequestMessage sent = handler.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.RequestUri.ShouldBe(new Uri("http://127.0.0.1:48211/api/refresh/ci-roles"));
        sent.Headers.Authorization!.ToString().ShouldBe("Bearer secret-token");
        sent.Headers.GetValues("X-Claude-Code-Account-Rotation").ShouldBe(["1"]);
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
