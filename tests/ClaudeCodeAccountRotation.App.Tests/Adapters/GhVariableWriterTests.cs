using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Endpoints;
using ClaudeCodeAccountRotation.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The kill-switch PATCH: the value of one organization variable plus the
/// visibility read back before it, never <c>gh variable set</c>, never a
/// repository or environment path, one at a time a second apart, and a 404
/// told apart from a rate limit.
/// </summary>
[Collection(StandInScripts.Name)]
public sealed class GhVariableWriterTests : IDisposable
{
    /// <summary>
    /// The 404 body GitHub returned to <c>gh api -i --method GET</c> for an
    /// organization variable in an organization that does not exist (gh 2.x,
    /// 2026-10-21), headers dropped, as gh prints it without <c>--include</c>:
    /// the body on standard output, the status on standard error, exit 1. A
    /// GET, not a PATCH: the tests never send a write to GitHub.
    /// </summary>
    private const string Recorded404Body =
        "{\"message\":\"Not Found\",\"documentation_url\":\"https://docs.github.com/rest/actions/variables#get-an-organization-variable\",\"status\":\"404\"}";

    private const string Recorded404Error = "gh: Not Found (HTTP 404)\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    private readonly GhWriteGate _gate = new(TimeProvider.System);

    public GhVariableWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _gate.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    public static bool OnUnix => !OperatingSystem.IsWindows();

    [Theory]
    [InlineData("CLAUDE_LANES_DISABLED", true, "all", "value=true", "visibility=all")]
    [InlineData("CLAUDE_REVIEW_DISABLED", false, "private", "value=false", "visibility=private")]
    [InlineData("CLAUDE_SECURITY_REVIEW_DISABLED", true, "all", "value=true", "visibility=all")]
    public void TheArgumentsAreExactlyAPatchOfTheValueWithTheReadBackVisibility(string name, bool value, string kind, string valueField, string visibilityField) =>
        GhVariableWriter.Arguments("octo", name, value, new VariableVisibility(kind, []))
            .ShouldBe(["api", "--method", "PATCH", "orgs/octo/actions/variables/" + name, "-f", valueField, "-f", visibilityField]);

    [Fact]
    public void ASelectedVariableSendsEachRepositoryIdAsAnInteger() =>
        GhVariableWriter.Arguments("octo", "CLAUDE_LANES_DISABLED", true, new VariableVisibility("selected", [1296269, 1296270]))
            .ShouldBe([
                "api", "--method", "PATCH", "orgs/octo/actions/variables/CLAUDE_LANES_DISABLED",
                "-f", "value=true", "-f", "visibility=selected",
                "-F", "selected_repository_ids[]=1296269", "-F", "selected_repository_ids[]=1296270",
            ]);

    // gh api --help: "To pass an empty array, use `key[]` without a value."
    [Fact]
    public void ASelectedVariableWithNoRepositoriesSendsAnEmptyArray() =>
        GhVariableWriter.Arguments("octo", "CLAUDE_LANES_DISABLED", false, new VariableVisibility("selected", []))
            .ShouldBe(["api", "--method", "PATCH", "orgs/octo/actions/variables/CLAUDE_LANES_DISABLED", "-f", "value=false", "-f", "visibility=selected", "-F", "selected_repository_ids[]"]);

    [Fact]
    public void NoWritableSwitchEverPutsARepositoryOrAnEnvironmentPathInTheArguments()
    {
        foreach (KillSwitch kill in KillSwitches.All.Where(static kill => kill.Writable))
        {
            foreach (bool value in new[] { true, false })
            {
                string joined = string.Join(' ', GhVariableWriter.Arguments("octo", kill.Name, value, new VariableVisibility("selected", [7])));
                joined.ShouldNotContain("variable set");
                joined.ShouldNotContain("repos/");
                joined.ShouldNotContain("environments/");
            }
        }
    }

    [Fact]
    public void ExitZeroIsDone() =>
        GhVariableWriter.Read("orgs/octo/actions/variables/CLAUDE_LANES_DISABLED", new GhExit(0, string.Empty, string.Empty)).IsSuccess.ShouldBeTrue();

    [Fact]
    public void ANotFoundIsReportedWithItsStatusAndNotAsARateLimit()
    {
        Result<Unit, GhApiFailure> read = GhVariableWriter.Read("orgs/octo/actions/variables/CLAUDE_LANES_DISABLED", new GhExit(1, Recorded404Body, Recorded404Error));

        read.Error.Status.ShouldBe(404);
        read.Error.RateLimited.ShouldBeFalse();
        read.Error.Message.ShouldContain("not set or not visible to this sign-in");
        read.Error.Message.ShouldContain("never creates it");
        read.Error.Message.ShouldNotContain("gh: Not Found");
    }

    // A primary limit answers 403 with a message naming the rate limit, a
    // secondary one 403 or 429. Basis:
    // https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api.
    [Theory]
    [InlineData("{\"message\":\"API rate limit exceeded for user ID 1.\"}", "gh: API rate limit exceeded for user ID 1. (HTTP 403)\n")]
    [InlineData("{\"message\":\"You have exceeded a secondary rate limit.\"}", "gh: You have exceeded a secondary rate limit. (HTTP 403)\n")]
    [InlineData("{\"message\":\"x\"}", "gh: x (HTTP 429)\n")]
    public void ARateLimitIsToldApart(string body, string error)
    {
        Result<Unit, GhApiFailure> read = GhVariableWriter.Read("orgs/octo/actions/variables/CLAUDE_LANES_DISABLED", new GhExit(1, body, error));

        read.Error.RateLimited.ShouldBeTrue();
        read.Error.Message.ShouldBe(GhApiReader.RateLimitMessage);
    }

    [Fact]
    public void AForbiddenThatIsNoRateLimitIsAnOrdinaryFailure()
    {
        Result<Unit, GhApiFailure> read = GhVariableWriter.Read(
            "orgs/octo/actions/variables/CLAUDE_LANES_DISABLED",
            new GhExit(1, "{\"message\":\"Must have admin rights to Repository.\"}", "gh: Must have admin rights to Repository. (HTTP 403)\n"));

        read.Error.RateLimited.ShouldBeFalse();
        read.Error.Status.ShouldBe(403);
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A404FromAStandInGhIsOneCallAndNoCreate()
    {
        string calls = Path.Combine(_root, "calls");
        string gh = await StandInAsync(calls, "printf '%s' '" + Recorded404Body + "'\nprintf 'gh: Not Found (HTTP 404)\\n' >&2\nexit 1\n");
        GhVariableWriter writer = new(gh, TimeSpan.FromSeconds(30), _gate);

        Result<Unit, GhApiFailure> set = await writer.SetOrganizationVariableAsync("octo", "CLAUDE_LANES_DISABLED", true, new VariableVisibility("all", []), TestContext.Current.CancellationToken);

        set.Error.Status.ShouldBe(404);
        (await File.ReadAllLinesAsync(calls, TestContext.Current.CancellationToken))
            .ShouldBe(["api --method PATCH orgs/octo/actions/variables/CLAUDE_LANES_DISABLED -f value=true -f visibility=all"]);
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task TwoWritesRunOneAtATimeAtLeastASecondApart()
    {
        // Each run stamps its start and end in nanoseconds on the monotonic
        // clock; the second must start at least a second after the first ended.
        string calls = Path.Combine(_root, "calls");
        string stamps = Path.Combine(_root, "stamps");
        string gh = await StandInAsync(calls, "echo \"" + StandInScripts.MonotonicNanoseconds + "\" >> '" + stamps + "'\nsleep 0.2\necho \"" + StandInScripts.MonotonicNanoseconds + "\" >> '" + stamps + "'\nexit 0\n");
        GhVariableWriter writer = new(gh, TimeSpan.FromSeconds(30), _gate);

        Result<Unit, GhApiFailure>[] both = await Task.WhenAll(
            writer.SetOrganizationVariableAsync("octo", "CLAUDE_LANES_DISABLED", true, new VariableVisibility("all", []), TestContext.Current.CancellationToken),
            writer.SetOrganizationVariableAsync("octo", "CLAUDE_REVIEW_DISABLED", true, new VariableVisibility("all", []), TestContext.Current.CancellationToken));

        both.ShouldAllBe(static set => set.IsSuccess);
        long[] at = [.. (await File.ReadAllLinesAsync(stamps, TestContext.Current.CancellationToken)).Select(static line => long.Parse(line, System.Globalization.CultureInfo.InvariantCulture))];
        at.Length.ShouldBe(4);
        TimeSpan.FromTicks((at[2] - at[1]) / 100).ShouldBeGreaterThanOrEqualTo(GhWriteGate.Spacing);
    }

    [Fact]
    public void TheComposedWritersShareOneGate()
    {
        ServiceCollection services = new();
        ClaudeCodeAccountRotation.App.Hosting.AppComposition.AddGhWriters(services, Result<string, string>.Success("gh"));
        using ServiceProvider provider = services.BuildServiceProvider();

        GhSecretWriter secrets = provider.GetRequiredService<ICiSecretWriter>().ShouldBeOfType<GhSecretWriter>();
        GhVariableWriter variables = provider.GetRequiredService<IGhVariableWriter>().ShouldBeOfType<GhVariableWriter>();

        secrets.Gate.ShouldBeSameAs(variables.Gate);
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ASecretSetThenAVariablePatchShareOneGateASecondApart()
    {
        string calls = Path.Combine(_root, "calls");
        string stamps = Path.Combine(_root, "stamps");
        string gh = await StandInAsync(calls, "echo \"" + StandInScripts.MonotonicNanoseconds + "\" >> '" + stamps + "'\ncat > /dev/null\necho \"" + StandInScripts.MonotonicNanoseconds + "\" >> '" + stamps + "'\nexit 0\n");
        GhSecretWriter secrets = new(gh, TimeSpan.FromSeconds(30), _gate);
        GhVariableWriter variables = new(gh, TimeSpan.FromSeconds(30), _gate);

        (await secrets.SetAsync(ClaudeCodeAccountRotation.Core.Accounts.CiTokenSecret.Parse("A_CLAUDE_API_KEY", null, "octo").Value, OrgSecretVisibility.All, [], "value", TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();
        (await variables.SetOrganizationVariableAsync("octo", "CLAUDE_LANES_DISABLED", true, new VariableVisibility("all", []), TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();

        long[] at = [.. (await File.ReadAllLinesAsync(stamps, TestContext.Current.CancellationToken)).Select(static line => long.Parse(line, System.Globalization.CultureInfo.InvariantCulture))];
        at.Length.ShouldBe(4);
        (await File.ReadAllLinesAsync(calls, TestContext.Current.CancellationToken))[1].ShouldStartWith("api --method PATCH");
        (at[2] - at[1]).ShouldBeGreaterThanOrEqualTo(1_000_000_000L);
    }

    /// <summary>A gh that appends its arguments to <paramref name="calls"/>, then runs <paramref name="then"/>.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private async Task<string> StandInAsync(string calls, string then)
    {
        string gh = Path.Combine(_root, "gh");
        await File.WriteAllTextAsync(gh, "#!/bin/sh\necho \"$*\" >> '" + calls + "'\n" + then, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return gh;
    }
}
