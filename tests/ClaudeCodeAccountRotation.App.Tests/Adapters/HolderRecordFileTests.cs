using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class HolderRecordFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"), "a@example.com");

    public HolderRecordFileTests() => Directory.CreateDirectory(_folder);

    private static HolderRecord Record(SideName side) =>
        new(side, CredentialFiles.Pair("refresh-a").Fingerprint, new DateTimeOffset(2026, 9, 20, 14, 2, 0, TimeSpan.Zero));

    [Fact]
    public async Task AWrittenRecordReadsBackAsTheSameSideFingerprintAndInstant()
    {
        HolderRecord written = Record(SideName.Wsl);

        await HolderRecordFile.WriteAsync(_folder, written, TestContext.Current.CancellationToken);

        (await HolderRecordFile.ReadAsync(_folder, TestContext.Current.CancellationToken)).ShouldBe(written);
    }

    [Fact]
    public async Task ASlotWithNoRecordReadsAsNoRecord()
    {
        (await HolderRecordFile.ReadAsync(_folder, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ARecordThatIsNotJsonReadsAsNoRecordRatherThanThrowing()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, HolderRecordFile.FileName), "{ truncated", TestContext.Current.CancellationToken);

        (await HolderRecordFile.ReadAsync(_folder, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ARecordMissingAFieldReadsAsNoRecord()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, HolderRecordFile.FileName), """{"side":"wsl"}""", TestContext.Current.CancellationToken);

        (await HolderRecordFile.ReadAsync(_folder, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AWriteOverAnExistingRecordReplacesItRatherThanAppending()
    {
        await HolderRecordFile.WriteAsync(_folder, Record(SideName.Wsl), TestContext.Current.CancellationToken);

        await HolderRecordFile.WriteAsync(_folder, Record(SideName.Windows), TestContext.Current.CancellationToken);

        (await HolderRecordFile.ReadAsync(_folder, TestContext.Current.CancellationToken))!.Side.ShouldBe(SideName.Windows);
    }

    [Fact]
    public async Task DeletingARecordLeavesTheSlotWithNone()
    {
        await HolderRecordFile.WriteAsync(_folder, Record(SideName.Windows), TestContext.Current.CancellationToken);

        await HolderRecordFile.DeleteAsync(_folder, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(_folder, HolderRecordFile.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeletingARecordThatIsNotThereIsNotAFailure()
    {
        await HolderRecordFile.DeleteAsync(_folder, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(_folder, HolderRecordFile.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task ARecordCarriesNoTokenOnlyItsFingerprint()
    {
        await HolderRecordFile.WriteAsync(_folder, Record(SideName.Wsl), TestContext.Current.CancellationToken);

        string written = await File.ReadAllTextAsync(Path.Combine(_folder, HolderRecordFile.FileName), TestContext.Current.CancellationToken);

        written.ShouldNotContain("refresh-a");
        written.ShouldContain(CredentialFiles.Pair("refresh-a").Fingerprint.Sha256Hex);
    }

    public void Dispose()
    {
        string root = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
