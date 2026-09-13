using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// The recovery directory on its own, without an engine driving it. Every file
/// here holds the only living copy of a credential lineage: the token endpoint
/// killed the old refresh token the moment it answered, so a sweep that gives
/// up early, applies a file to the wrong folder, or overwrites an orphan ends an
/// account's login for good.
/// </summary>
public sealed class RecoveryFilesTests
{
    private const string FileSuffix = ".credentials.json";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATornParkedFileCostsItsOwnFolderAndNotTheSweep()
    {
        // The store parses the folder's own file to compare fingerprints, so a
        // file caught mid-write throws out of the read. Before this was caught,
        // one folder in that state ended the startup sweep and left every other
        // stranded account waiting for a restart that would hit the same file.
        using RefreshHarness harness = new();
        string torn = await StrandAsync(harness, "a@example.com", "refresh-a", "refresh-a2", Token);
        string sound = await StrandAsync(harness, "b@example.com", "refresh-b", "refresh-b2", Token);
        await File.WriteAllTextAsync(Path.Combine(torn, CredentialFiles.FileName), """{"claudeAiOauth": {"refresh""", Token);

        await harness.Recovery.RestoreAllAsync(Token);

        harness.Recovery.HasRecoveryFor(torn).ShouldBeTrue();
        harness.Recovery.HasRecoveryFor(sound).ShouldBeFalse();
        (await RefreshHarness.ParkedOAuthAsync(sound, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-b2");
        harness.State.RecoveryWarnings.ShouldBe([RefreshMessages.RestoreKept()]);
    }

    [Fact]
    public async Task AnEnvelopeNamingAnotherFolderIsMovedAside()
    {
        // The file's name is what the sweep trusts when it decides which folder to
        // rewrite. An envelope that names a different one was renamed by hand or
        // was never written here; applying it would put one account's credentials
        // into another account's folder.
        using RefreshHarness harness = new();
        string owner = await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        string other = await harness.ParkAsync("b@example.com", "refresh-b", harness.Valid, Token);
        await WriteEnvelopeAsync(harness, filedUnder: "a@example.com", folder: other, expected: "refresh-b", pair: "refresh-b2", cancellationToken: Token);

        await harness.Recovery.RestoreAllAsync(Token);

        harness.Recovery.HasRecoveryFor(owner).ShouldBeFalse();
        Directory.GetFiles(StaleDirectory(harness)).Length.ShouldBe(1);
        // Neither folder was touched: not the one the file was filed under, and
        // not the one its envelope pointed at.
        (await RefreshHarness.ParkedOAuthAsync(other, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-b");
        (await RefreshHarness.ParkedOAuthAsync(owner, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-a");
        harness.State.RecoveryWarnings.ShouldBe([RefreshMessages.RestoreMisfiled()]);
    }

    [Fact]
    public async Task AStaleFileDoesNotOverwriteAnEarlierOne()
    {
        // Two orphans for one folder are two lineages, each a refresh token valid
        // for the rest of its login. Moving the second over the first would
        // destroy the record of one of them, which is the single thing this
        // directory exists to prevent.
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        // Neither envelope can apply: the folder holds a pair, but not the one
        // either rotation replaced.
        await WriteEnvelopeAsync(harness, "a@example.com", folder, expected: "refresh-older", pair: "refresh-orphan1", cancellationToken: Token);
        await harness.Recovery.RestoreAllAsync(Token);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await WriteEnvelopeAsync(harness, "a@example.com", folder, expected: "refresh-older", pair: "refresh-orphan2", cancellationToken: Token);
        await harness.Recovery.RestoreAllAsync(Token);

        harness.Recovery.HasRecoveryFor(folder).ShouldBeFalse();
        string[] stale = Directory.GetFiles(StaleDirectory(harness));
        stale.Length.ShouldBe(2);
        // Still the shape the acceptance script's recovery sweep looks for.
        stale.ShouldAllBe(path => path.EndsWith(FileSuffix, StringComparison.Ordinal));
        List<string> fingerprints = [];
        foreach (string path in stale)
        {
            JsonObject envelope = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
            fingerprints.Add(CredentialPair.FromJson(envelope["pair"]!.AsObject()).Value.Fingerprint.Sha256Hex);
        }

        fingerprints.ShouldBe(
            [
                RefreshTokenFingerprint.FromRefreshToken("refresh-orphan1").Sha256Hex,
                RefreshTokenFingerprint.FromRefreshToken("refresh-orphan2").Sha256Hex,
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task ATransientFailureThenASuccessfulRestoreLeavesNoWarning()
    {
        // The warning and the resolution have to agree on what they are about.
        // Filed under the file's path and cleared by the folder, a warning raised
        // by a failed attempt outlived the restore that fixed it and sat on the
        // page until the tool was restarted.
        using RefreshHarness harness = new();
        string folder = await StrandAsync(harness, "a@example.com", "refresh-a", "refresh-a2", Token);
        harness.Store.ThrowOnWriteWith = FaultyPairStore.Locked;
        await harness.Recovery.RestoreAllAsync(Token);
        harness.State.RecoveryWarnings.ShouldBe([RefreshMessages.RestoreKept()]);

        harness.Store.ThrowOnWriteWith = null;
        await harness.Recovery.RestoreAllAsync(Token);

        harness.Recovery.HasRecoveryFor(folder).ShouldBeFalse();
        (await RefreshHarness.ParkedOAuthAsync(folder, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-a2");
        harness.State.RecoveryWarnings.ShouldBeEmpty();
    }

    private static string StaleDirectory(RefreshHarness harness) =>
        Path.Combine(harness.AppData, RecoveryFiles.DirectoryName, RecoveryFiles.StaleDirectoryName);

    /// <summary>Parks an account and strands a rotated pair for it, the state a failed write-back leaves behind.</summary>
    private static async Task<string> StrandAsync(
        RefreshHarness harness,
        string email,
        string refreshToken,
        string rotatedRefreshToken,
        CancellationToken cancellationToken)
    {
        string folder = await harness.ParkAsync(email, refreshToken, harness.Valid, cancellationToken);
        (await harness.Recovery.WriteAsync(
            folder,
            RefreshTokenFingerprint.FromRefreshToken(refreshToken),
            CredentialFiles.Pair(rotatedRefreshToken),
            cancellationToken)).ShouldBeTrue();
        return folder;
    }

    /// <summary>
    /// Writes a recovery envelope by hand, so a test can file one under a name
    /// its own <c>folder</c> field disagrees with, or park an orphan whose
    /// compare-and-swap can never apply.
    /// </summary>
    private static async Task WriteEnvelopeAsync(
        RefreshHarness harness,
        string filedUnder,
        string folder,
        string expected,
        string pair,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(harness.AppData, RecoveryFiles.DirectoryName);
        Directory.CreateDirectory(directory);
        JsonObject envelope = new()
        {
            ["folder"] = folder,
            ["expectedFingerprint"] = RefreshTokenFingerprint.FromRefreshToken(expected).Sha256Hex,
            ["pair"] = CredentialFiles.Shape(pair),
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, filedUnder + FileSuffix),
            envelope.ToJsonString(),
            cancellationToken);
    }
}
