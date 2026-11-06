using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Endpoints;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// After a key push, KEY_CREDIT_RESET_AT gets the push's UTC time, so the CI
/// key-credit gate tries the new key on the next run: one PATCH per
/// organization of a variable that exists, with the visibility read just
/// before, never a create, and never a change to the push's answer.
/// </summary>
public sealed partial class CiTokenRoleEndpointTests
{
    private const string ResetPath = "orgs/octo/actions/variables/" + KeyCreditReset.Name;

    /// <summary>"Get an organization variable", documented shape: https://docs.github.com/en/rest/actions/variables#get-an-organization-variable.</summary>
    private static Result<JsonNode, GhApiFailure> ResetVariable(string visibility) => Result<JsonNode, GhApiFailure>.Success(new JsonObject
    {
        ["name"] = KeyCreditReset.Name,
        ["value"] = "2026-09-01T00:00:00Z",
        ["created_at"] = "2026-09-01T00:00:00Z",
        ["updated_at"] = "2026-09-01T00:00:00Z",
        ["visibility"] = visibility,
    });

    /// <summary>The account backing both roles' tokens, so Push sends its key to both key secrets.</summary>
    private static async Task<(AppFactory Factory, PathReader Reader)> BothRolesAsync()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        DateOnly before = new(2026, 6, 1);
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(_prUnblock, before, null).ClaimingToken(_prVerify, before, null));
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_API_KEY", "PR_VERIFY_CLAUDE_API_KEY");
        return (factory, reader);
    }

    [Fact]
    public async Task AKeyPushStampsTheResetOncePerOrganizationWithTheVisibilityReadBack()
    {
        (AppFactory factory, PathReader reader) = await BothRolesAsync();
        await using AppFactory host = factory;
        reader.Answers[ResetPath] = ResetVariable("private");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Secrets.Sets.Count.ShouldBe(2);
        // The factory clock reads 2026-09-07 12:00 UTC.
        factory.Variables.Calls.ShouldBe([("octo", KeyCreditReset.Name, "2026-09-07T12:00:00Z", "private")]);
        reader.Paths.Count(static path => path == ResetPath).ShouldBe(1);
    }

    [Fact]
    public async Task AnAbsentResetVariableIsLoggedNeverCreatedAndThePushStillSucceeds()
    {
        (AppFactory factory, PathReader reader) = await BothRolesAsync();
        await using AppFactory host = factory;
        reader.Answers[ResetPath] = PathReader.NotFound();
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Secrets.Sets.Count.ShouldBe(2);
        factory.Variables.Calls.ShouldBeEmpty();
        factory.Logs.Lines.ShouldContain(static line => line.Contains(KeyCreditReset.Name + " is not set in org octo", StringComparison.Ordinal) && line.Contains("never creates it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedVisibilityReadOrPatchLeavesThePushAnswerAsItWas()
    {
        foreach (bool patchFails in new[] { false, true })
        {
            (AppFactory factory, PathReader reader) = await BothRolesAsync();
            await using AppFactory host = factory;
            if (patchFails)
            {
                reader.Answers[ResetPath] = ResetVariable("all");
                factory.Variables.Answer = Result<Unit, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 403", RateLimited: false, 403));
            }
            else
            {
                reader.Answers[ResetPath] = Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 500", RateLimited: false, 500));
            }

            using HttpClient client = factory.CreateMutatingClient();

            using HttpResponseMessage pushed = await PushAsync(client);

            pushed.StatusCode.ShouldBe(HttpStatusCode.OK, patchFails.ToString());
            JsonNode answer = JsonNode.Parse(await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
            answer["message"]!.GetValue<string>().ShouldEndWith(" from " + Email + ".");
            answer["failed"]!.GetValue<int>().ShouldBe(0);
            factory.Variables.Calls.Count.ShouldBe(patchFails ? 1 : 0);
            factory.Logs.Lines.ShouldContain(static line => line.Contains(KeyCreditReset.Name + " in org octo not reset: GitHub answered HTTP", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task APushStoppedByARateLimitOrSettingNothingLeavesTheResetAlone()
    {
        foreach (string error in new[] { GhSecretWriter.RateLimitMessage, "gh secret set exited with code 1" })
        {
            (AppFactory factory, PathReader reader) = await BothRolesAsync();
            await using AppFactory host = factory;
            reader.Answers[ResetPath] = ResetVariable("all");
            factory.Secrets.SetError = error;
            using HttpClient client = factory.CreateMutatingClient();

            using HttpResponseMessage pushed = await PushAsync(client);

            factory.Variables.Calls.ShouldBeEmpty(error);
            reader.Paths.ShouldNotContain(ResetPath);
        }
    }

    [Fact]
    public async Task AssigningAKeyHalfStampsTheReset()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_API_KEY");
        reader.Answers[ResetPath] = ResetVariable("all");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { prefix = "PR_UNBLOCK", organization = "octo" }, "key");

        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        factory.Secrets.Sets.Select(static set => set.Secret.Name).ShouldBe(["PR_UNBLOCK_CLAUDE_API_KEY"]);
        factory.Variables.Calls.ShouldBe([("octo", KeyCreditReset.Name, "2026-09-07T12:00:00Z", "all")]);
    }

    [Fact]
    public async Task AssigningATokenHalfAloneLeavesTheResetAlone()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN");
        reader.Answers[ResetPath] = ResetVariable("all");
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage pasted = await PasteTokenAsync(client, _pastedToken))
        {
            pasted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { prefix = "PR_UNBLOCK", organization = "octo" }, "token");

        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        factory.Secrets.Sets.Select(static set => set.Secret.Name).ShouldBe(["PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN"]);
        factory.Variables.Calls.ShouldBeEmpty();
        reader.Paths.ShouldNotContain(ResetPath);
    }

    // The gate (ci-workflows check-key-credit gate.cjs RESET_AT) accepts
    // exactly this shape; any other text is no reset there.
    [Fact]
    public void TheStampIsUtcToTheSecondInTheShapeTheGateAccepts()
    {
        string stamp = KeyCreditReset.Stamp(new DateTimeOffset(2026, 11, 6, 7, 8, 9, 123, TimeSpan.FromHours(-5)));

        stamp.ShouldBe("2026-11-06T12:08:09Z");
        Regex.IsMatch(stamp, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,3})?Z$", RegexOptions.None, TimeSpan.FromSeconds(1)).ShouldBeTrue();
    }
}
