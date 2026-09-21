using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// Crash injection at every <see cref="ImportStep"/> of the <b>release</b>
/// direction, twice: once after the journal write and once between the disk
/// mutation and the journal write. The process is killed for real; a second
/// follower over the same roots reconciles, and the outcome is asserted on
/// disk.
/// <para>
/// A release walks the same six steps as an import, two of which have no file
/// to move, which is the point: one crash table decides both directions and the
/// case list is the same enum.
/// </para>
/// </summary>
[Collection(OutOfProcessFollowers.Name)]
public sealed class ReleaseCrashInjectionTests
{
    private const string HeldEmail = "a@example.com";

    /// <summary>One case per <see cref="ImportStep"/> per timing, the same list the import direction uses.</summary>
    public static IEnumerable<object[]> Cases =>
        Enum.GetValues<ImportStep>()
            .SelectMany(step => new[] { step.ToString(), step + ":before-journal" })
            .Select(injection => new object[] { injection });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void EveryImportStepHasBothInjectionCasesInTheReleaseDirection()
    {
        Cases.Count().ShouldBe(Enum.GetValues<ImportStep>().Length * 2);
        Enum.GetValues<ImportStep>().Length.ShouldBe(6);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ACrashAtEachStepOfAReleaseReconcilesToItsDocumentedOutcome(string injection)
    {
        using FollowerRoots roots = new();
        RefreshTokenFingerprint fa = await roots.WriteLiveAsync(HeldEmail, "refresh-a", Token);
        string configPath = ImportCrashInjectionTests.WriteFollowerConfig(roots);

        bool killed = await RunUntilTheCrashAsync(roots, configPath, FollowerProcess.FreePort(), injection, fa);
        killed.ShouldBeTrue("the injected step never killed the process");

        await using FollowerProcess restarted = await FollowerProcess.StartAsync(configPath, FollowerProcess.FreePort(), failAfterStep: null, Token);
        using HttpClient client = restarted.Client();
        using HttpResponseMessage status = await client.GetAsync(new Uri("/api/import-status", UriKind.Relative), Token);
        status.IsSuccessStatusCode.ShouldBeTrue();
        JsonNode reported = JsonNode.Parse(await status.Content.ReadAsStringAsync(Token))!;

        bool removalLanded = RemovalHadLanded(injection);
        reported["imported"]!.GetValue<bool>().ShouldBe(removalLanded, injection);

        if (removalLanded)
        {
            // The pair left this side, and the gate-verified export in the
            // mailbox is the copy the leader parks.
            File.Exists(roots.LivePath).ShouldBeFalse(injection);
            (await FollowerRoots.FingerprintOfAsync(roots.ExportPath(HeldEmail), Token)).ShouldBe(fa, injection);
            reported["liveAccountBlock"]?["emailAddress"].ShouldBeNull(injection);
        }
        else
        {
            // Nothing was given up, so the redundant copy goes and the live one stays.
            (await FollowerRoots.FingerprintOfAsync(roots.LivePath, Token)).ShouldBe(fa, injection);
            File.Exists(roots.ExportPath(HeldEmail)).ShouldBeFalse(injection);
        }

        File.Exists(roots.StagingPath).ShouldBeFalse(injection);
        (await roots.Journal().ReadOpenAsync(Token)).ShouldBeNull(injection);
        // duplicates=0, asserted the way the sweep does it: one reachable copy.
        (await roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1, injection);
    }

    /// <summary>
    /// Which injections leave the live pair already given up. The torn case is
    /// <c>Swapped:before-journal</c>: the live file is gone while the journal
    /// still reads <c>Exported</c>, and reconciliation must continue F6 to F8
    /// rather than unwind a removal that already happened and delete the only
    /// copy left.
    /// </summary>
    private static bool RemovalHadLanded(string injection) => injection switch
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
        RefreshTokenFingerprint held)
    {
        await using FollowerProcess follower = await FollowerProcess.StartAsync(configPath, port, injection, Token);
        using HttpClient client = follower.Client();
        // No claimedPath and no account block: that absence is the whole of what
        // tells the follower which way the pair is moving.
        JsonObject request = new()
        {
            ["email"] = HeldEmail,
            ["fingerprint"] = held.Sha256Hex,
            ["exportPath"] = roots.ExportPath(HeldEmail),
        };

        using HttpResponseMessage? exported = await FollowerProcess.PostOrDieAsync(client, "/api/import", request, Token);
        if (follower.HasExited || exported is null)
        {
            return await follower.WaitForExitAsync(TimeSpan.FromSeconds(20), Token) && follower.ExitCode != 0;
        }

        exported.IsSuccessStatusCode.ShouldBeTrue(await exported.Content.ReadAsStringAsync(Token));
        using HttpResponseMessage? committed = await FollowerProcess.PostOrDieAsync(
            client,
            "/api/import/commit",
            new JsonObject { ["email"] = HeldEmail },
            Token);
        _ = committed;
        return await follower.WaitForExitAsync(TimeSpan.FromSeconds(20), Token) && follower.ExitCode != 0;
    }
}
