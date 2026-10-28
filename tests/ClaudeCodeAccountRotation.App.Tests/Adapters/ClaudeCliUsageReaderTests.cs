using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The live usage read through the CLI (#220), against a Node stand-in that
/// speaks the CLI's stream-json control protocol the way 2.1.312 does: system
/// events first, a control response for some other request, then the one for
/// <c>get_usage</c>, and an exit only once its input closes. It records the
/// arguments, working directory, environment and request it was given.
/// <para>
/// Node is found on PATH; without it these tests skip, unless
/// <c>CCAR_REQUIRE_REAL_CLI_TESTS=1</c> says they must run. The collection runs
/// alone, which is what lets one test set a process environment variable.
/// </para>
/// </summary>
[Collection(StandInScripts.Name)]
public sealed class ClaudeCliUsageReaderTests : IDisposable
{
    private const string RequireVariable = "CCAR_REQUIRE_REAL_CLI_TESTS";

    private const string Fake = """
        const fs = require("fs");
        const [mode, answerPath, recordPath] = [process.env.FAKE_MODE, process.env.FAKE_ANSWER, process.env.FAKE_RECORD];
        const record = {
          args: process.argv.slice(2),
          cwd: process.cwd(),
          configDir: process.env.CLAUDE_CONFIG_DIR ?? null,
          flag: process.env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC ?? null,
          oauth: process.env.CLAUDE_CODE_OAUTH_TOKEN ?? null,
        };
        const say = (o) => process.stdout.write(JSON.stringify(o) + "\n");
        say({ type: "system", subtype: "hook_started", hook_name: "SessionStart" });
        if (mode === "too-old") {
          process.stderr.write("error: unknown option '--safe-mode'\n");
          process.exit(1);
        }
        let buffer = "";
        let answered = false;
        process.stdin.on("data", (chunk) => {
          buffer += chunk;
          const end = buffer.indexOf("\n");
          if (answered || end < 0) return;
          answered = true;
          record.request = JSON.parse(buffer.slice(0, end));
          fs.writeFileSync(recordPath, JSON.stringify(record));
          if (mode === "hang") return;
          say({ type: "control_response", response: { subtype: "success", request_id: "someone-else", response: {} } });
          const usage = JSON.parse(fs.readFileSync(answerPath, "utf8"));
          say({ type: "control_response", response: { subtype: "success", request_id: record.request.request_id, response: usage } });
        });
        process.stdin.on("end", () => process.exit(0));
        if (mode === "hang") setInterval(() => {}, 1000);
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public ClaudeCliUsageReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (string variable in (string[])["FAKE_MODE", "FAKE_ANSWER", "FAKE_RECORD"])
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string LiveDirectory => Path.Combine(_root, "live");

    private string WorkingDirectory => Path.Combine(_root, "cli-usage");

    private string RecordPath => Path.Combine(_root, "record.json");

    private static string? OnPath(string name)
    {
        string[] names = OperatingSystem.IsWindows() ? [name + ".exe", name + ".cmd"] : [name];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in names)
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>The stand-in in <paramref name="mode"/>, answering <paramref name="usage"/> (the control response's inner object).</summary>
    private ClaudeCliUsageReader Reader(string mode, JsonObject? usage = null, TimeSpan? timeout = null)
    {
        string? node = OnPath("node");
        if (node is null)
        {
            Assert.False(Environment.GetEnvironmentVariable(RequireVariable) == "1", "node is not on PATH, and " + RequireVariable + "=1 says these tests must run");
            Assert.Skip("node is not on PATH");
        }

        string script = Path.Combine(_root, "claude.cjs");
        string answer = Path.Combine(_root, "answer.json");
        File.WriteAllText(script, Fake);
        File.WriteAllText(answer, (usage ?? new JsonObject()).ToJsonString());
        Environment.SetEnvironmentVariable("FAKE_MODE", mode);
        Environment.SetEnvironmentVariable("FAKE_ANSWER", answer);
        Environment.SetEnvironmentVariable("FAKE_RECORD", RecordPath);
        return new ClaudeCliUsageReader(
            new ClaudeExecutable(node!, [script]),
            LiveDirectory,
            Path.Combine(LiveDirectory, ".claude.json"),
            _root,
            WorkingDirectory,
            timeout ?? TimeSpan.FromSeconds(30),
            TimeProvider.System);
    }

    private JsonObject Record() => JsonNode.Parse(File.ReadAllText(RecordPath))!.AsObject();

    /// <summary>The inner <c>get_usage</c> response as 2.1.312 printed it, trimmed: the generic array, extra usage, and codenamed windows beside them.</summary>
    internal static JsonObject Usage(DateTimeOffset asOf) => JsonNode.Parse(
        """
        {
          "session": { "total_cost_usd": 0 },
          "subscription_type": "max",
          "rate_limits_available": true,
          "rate_limits": {
            "five_hour": { "utilization": 23, "resets_at": "2026-10-28T21:30:00.490598+00:00", "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null },
            "seven_day": { "utilization": 6, "resets_at": "2026-11-03T22:00:00.490650+00:00", "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null },
            "seven_day_opus": null,
            "iguana_necktie": { "utilization": 100, "resets_at": "2026-11-05T07:59:00+00:00", "limit_dollars": 250, "used_dollars": 250.2, "remaining_dollars": 0, "locked_reason": null },
            "extra_usage": { "is_enabled": false, "disabled_reason": null, "spend_limit_reached": false },
            "limits": [
              { "kind": "session", "group": "session", "percent": 23, "severity": "normal", "resets_at": "2026-10-28T21:30:00.490598+00:00", "scope": null, "is_active": true },
              { "kind": "weekly_all", "group": "weekly", "percent": 6, "severity": "normal", "resets_at": "2026-11-03T22:00:00.490650+00:00", "scope": null, "is_active": false }
            ],
            "seven_day_breakdown": { "as_of": "AS_OF", "rows": [] },
            "model_scoped": [ { "display_name": "Fable", "utilization": 0, "resets_at": "2026-11-03T22:00:00+00:00" } ]
          },
          "behaviors": null
        }
        """.Replace("AS_OF", asOf.ToString("O", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))!.AsObject();

    [Fact]
    public async Task AFreshAnswerComesBackInTheEndpointsShapeFromACleanChild()
    {
        DateTimeOffset asOf = DateTimeOffset.UtcNow;
        Environment.SetEnvironmentVariable("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "not-the-live-login");
        try
        {
            Result<LiveUsageAnswer, string> read = await Reader("answer", Usage(asOf)).ReadAsync(Token);

            read.IsSuccess.ShouldBeTrue(read.IsFailure ? read.Error : null);
            read.Value.Body["limits"]![0]!["percent"]!.GetValue<double>().ShouldBe(23);
            read.Value.Body["extra_usage"]!["is_enabled"]!.GetValue<bool>().ShouldBeFalse();
            // Only the two blocks the parser reads; codenamed windows stay behind.
            read.Value.Body.Select(static pair => pair.Key).ShouldBe(["limits", "extra_usage"], ignoreOrder: true);
            read.Value.CapturedAt.ShouldBe(asOf);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
        }

        JsonObject record = Record();
        record["args"]!.AsArray().Select(static arg => arg!.GetValue<string>()).ShouldBe(ClaudeCliUsageReader.Arguments);
        // By name rather than whole: a Windows runner's temp path may come back in its long or short form.
        string cwd = Path.TrimEndingDirectorySeparator(record["cwd"]!.GetValue<string>());
        Path.GetFileName(cwd).ShouldBe("cli-usage");
        Path.GetFileName(Path.GetDirectoryName(cwd)).ShouldBe(Path.GetFileName(_root));
        record["configDir"]!.GetValue<string>().ShouldBe(Path.GetFullPath(LiveDirectory));
        record["flag"].ShouldBeNull();
        record["oauth"].ShouldBeNull();
        JsonObject request = record["request"]!.AsObject();
        request["type"]!.GetValue<string>().ShouldBe("control_request");
        request["request"]!["subtype"]!.GetValue<string>().ShouldBe("get_usage");
        request["request"]!["skip_behaviors"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task AStaleSnapshotIsRefusedRatherThanShownAsCurrent()
    {
        Result<LiveUsageAnswer, string> read = await Reader("answer", Usage(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10))).ReadAsync(Token);

        read.IsFailure.ShouldBeTrue();
        read.Error.ShouldContain("snapshot captured at");
        read.Error.ShouldContain("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC");
    }

    [Fact]
    public async Task ACliTooOldForTheRequestIsAFailureWithItsExitCodeAndNothingItPrinted()
    {
        Result<LiveUsageAnswer, string> read = await Reader("too-old").ReadAsync(Token);

        read.IsFailure.ShouldBeTrue();
        read.Error.ShouldContain("exited with code 1 without answering");
        read.Error.ShouldNotContain("unknown option");
    }

    [Fact]
    public async Task ACliThatNeverAnswersIsKilledAtTheTimeout()
    {
        Result<LiveUsageAnswer, string> read = await Reader("hang", timeout: TimeSpan.FromSeconds(2)).ReadAsync(Token);

        read.IsFailure.ShouldBeTrue();
        read.Error.ShouldContain("did not answer within 2 s");
    }

    [Fact]
    public async Task AMissingCliIsAFailure()
    {
        ClaudeCliUsageReader reader = new(
            new ClaudeExecutable(Path.Combine(_root, "no-such-claude"), []),
            LiveDirectory,
            Path.Combine(LiveDirectory, ".claude.json"),
            _root,
            WorkingDirectory,
            TimeSpan.FromSeconds(5),
            TimeProvider.System);

        Result<LiveUsageAnswer, string> read = await reader.ReadAsync(Token);

        read.IsFailure.ShouldBeTrue();
        read.Error.ShouldBe("the CLI could not be started");
    }
}
