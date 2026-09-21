using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// Crash injection at every <see cref="ImportStep"/>, twice: once after the
/// journal write and once between the disk mutation and the journal write. The
/// process is killed for real; a second follower over the same roots
/// reconciles, and the outcome is asserted on disk.
/// </summary>
[Collection(OutOfProcessFollowers.Name)]
public sealed class ImportCrashInjectionTests
{
    private const string OutgoingEmail = "a@example.com";
    private const string IncomingEmail = "b@example.com";

    /// <summary>
    /// One case per <see cref="ImportStep"/> per timing. The count is asserted
    /// against the enum below, so a seventh step cannot be added without a case.
    /// </summary>
    public static IEnumerable<object[]> Cases =>
        Enum.GetValues<ImportStep>()
            .SelectMany(step => new[] { step.ToString(), step + ":before-journal" })
            .Select(injection => new object[] { injection });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void EveryImportStepHasBothInjectionCases()
    {
        Cases.Count().ShouldBe(Enum.GetValues<ImportStep>().Length * 2);
        Enum.GetValues<ImportStep>().Length.ShouldBe(6);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ACrashAtEachStepReconcilesToItsDocumentedOutcome(string injection)
    {
        using FollowerRoots roots = new();
        RefreshTokenFingerprint fa = await roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        RefreshTokenFingerprint fb = await roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        string configPath = WriteFollowerConfig(roots);
        int port = FollowerProcess.FreePort();

        bool killed = await RunUntilTheCrashAsync(roots, configPath, port, injection, fb);
        killed.ShouldBeTrue("the injected step never killed the process");

        // A fresh follower over the same roots: its reconciliation runs under the
        // gate before any request is answered, so the status read reports what it
        // found and did.
        await using FollowerProcess restarted = await FollowerProcess.StartAsync(configPath, FollowerProcess.FreePort(), failAfterStep: null, Token);
        using HttpClient client = restarted.Client();
        using HttpResponseMessage status = await client.GetAsync(new Uri("/api/import-status", UriKind.Relative), Token);
        status.IsSuccessStatusCode.ShouldBeTrue();
        JsonNode reported = JsonNode.Parse(await status.Content.ReadAsStringAsync(Token))!;

        bool swapLanded = SwapHadLanded(injection);
        reported["imported"]!.GetValue<bool>().ShouldBe(swapLanded, injection);

        if (swapLanded)
        {
            (await FollowerRoots.FingerprintOfAsync(roots.LivePath, Token)).ShouldBe(fb, injection);
            File.Exists(roots.ClaimedPath(IncomingEmail)).ShouldBeFalse(injection);
            (await FollowerRoots.FingerprintOfAsync(roots.ExportPath(OutgoingEmail), Token)).ShouldBe(fa, injection);
        }
        else
        {
            (await FollowerRoots.FingerprintOfAsync(roots.LivePath, Token)).ShouldBe(fa, injection);
            (await FollowerRoots.FingerprintOfAsync(roots.ClaimedPath(IncomingEmail), Token)).ShouldBe(fb, injection);
            File.Exists(roots.ExportPath(OutgoingEmail)).ShouldBeFalse(injection);
        }

        File.Exists(roots.StagingPath).ShouldBeFalse(injection);
        (await roots.Journal().ReadOpenAsync(Token)).ShouldBeNull(injection);
        (await roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1, injection);
        (await roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1, injection);
    }

    /// <summary>
    /// Which injections leave the swap already done. The torn case is
    /// <c>Swapped:before-journal</c>: the live file holds the incoming pair while
    /// the journal still reads <c>Exported</c>, and reconciliation must continue
    /// F6 to F8 rather than unwind a swap that already happened.
    /// </summary>
    private static bool SwapHadLanded(string injection) => injection switch
    {
        "Planned" or "Planned:before-journal" => false,
        "Staged" or "Staged:before-journal" => false,
        "Exported" or "Exported:before-journal" => false,
        _ => true,
    };

    private static async Task<bool> RunUntilTheCrashAsync(
        FollowerRoots roots,
        string configPath,
        int port,
        string injection,
        RefreshTokenFingerprint incoming)
    {
        await using FollowerProcess follower = await FollowerProcess.StartAsync(configPath, port, injection, Token);
        using HttpClient client = follower.Client();
        JsonObject request = new()
        {
            ["email"] = IncomingEmail,
            ["claimedPath"] = roots.ClaimedPath(IncomingEmail),
            ["fingerprint"] = incoming.Sha256Hex,
            ["account"] = FollowerRoots.AccountJson(IncomingEmail),
            ["exportPath"] = roots.ExportPath(OutgoingEmail),
        };

        using HttpResponseMessage? imported = await FollowerProcess.PostOrDieAsync(client, "/api/import", request, Token);
        if (follower.HasExited || imported is null)
        {
            return await follower.WaitForExitAsync(TimeSpan.FromSeconds(20), Token) && follower.ExitCode != 0;
        }

        imported.IsSuccessStatusCode.ShouldBeTrue(await imported.Content.ReadAsStringAsync(Token));
        using HttpResponseMessage? committed = await FollowerProcess.PostOrDieAsync(
            client,
            "/api/import/commit",
            new JsonObject { ["email"] = IncomingEmail },
            Token);
        _ = committed;
        return await follower.WaitForExitAsync(TimeSpan.FromSeconds(20), Token) && follower.ExitCode != 0;
    }

    internal static string WriteFollowerConfig(FollowerRoots roots)
    {
        string path = Path.Combine(roots.AppData, "config.json");
        JsonObject configuration = new()
        {
            ["role"] = "follower",
            ["liveConfigDirectory"] = roots.LiveDirectory,
            ["stateFilePath"] = roots.StateFilePath,
            ["appDataDirectory"] = roots.AppData,
            ["profilesRoot"] = roots.Store,
            ["mailbox"] = roots.Mailbox,
            ["refreshLockWaitSeconds"] = 2,
            ["claudeExecutable"] = null,
        };
        File.WriteAllText(path, configuration.ToJsonString());
        return path;
    }
}
