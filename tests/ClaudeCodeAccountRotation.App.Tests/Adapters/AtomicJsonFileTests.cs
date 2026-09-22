using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public AtomicJsonFileTests() => Directory.CreateDirectory(_directory);

    public static bool OnWindows => OperatingSystem.IsWindows();

    public static bool OnUnix => !OperatingSystem.IsWindows();

    [Fact]
    public async Task ReplacesAbsentTargetByMove()
    {
        string path = Path.Combine(_directory, "state.json");

        await AtomicJsonFile.WriteAsync(path, new JsonObject { ["a"] = 1 }, TestContext.Current.CancellationToken);

        JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!["a"]!.GetValue<int>().ShouldBe(1);
        Directory.GetFiles(_directory).ShouldBe([path]);
    }

    /// <summary>
    /// A placement that fails must leave the previous bytes. Windows
    /// <c>File.Replace</c> with no backup does the opposite on Win32 1176: the
    /// destination is already gone when the call throws. This failure is the
    /// one <c>File.Move(overwrite: true)</c> has, and it is the call the
    /// Windows branch makes. The Win32 code itself is not raised on this host.
    /// </summary>
    [Fact]
    public void AFailedPlacementLeavesTheExistingBytesInPlace()
    {
        string path = Path.Combine(_directory, "state.json");
        File.WriteAllText(path, "original");
        string temporary = Path.Combine(_directory, "next.tmp");
        File.WriteAllText(temporary, "next");
        bool overwrite = false;

        Should.Throw<IOException>(() => AtomicBytesFile.MoveIntoPlace(temporary, path, (_, _, overwriteFlag) =>
        {
            overwrite = overwriteFlag;
            throw new IOException("sharing violation");
        }));

        overwrite.ShouldBeTrue();
        File.ReadAllText(path).ShouldBe("original");
        File.Exists(temporary).ShouldBeTrue();
    }

    [Fact]
    public async Task ReplacesAnExistingFileWhole()
    {
        string path = Path.Combine(_directory, "state.json");
        await File.WriteAllTextAsync(path, """{"old":true,"padding":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""", TestContext.Current.CancellationToken);

        await AtomicJsonFile.WriteAsync(path, new JsonObject { ["fresh"] = true }, TestContext.Current.CancellationToken);

        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe("""{"fresh":true}""");
        Directory.GetFiles(_directory).ShouldBe([path]);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Sharing violations are a Windows behavior")]
    public async Task RetriesWhileAnotherHandleHoldsTheTargetBriefly()
    {
        string path = Path.Combine(_directory, "state.json");
        await File.WriteAllTextAsync(path, """{"old":true}""", TestContext.Current.CancellationToken);
        TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(async () =>
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            opened.SetResult();
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await opened.Task;

        await AtomicJsonFile.WriteAsync(path, new JsonObject { ["fresh"] = true }, TestContext.Current.CancellationToken);
        await holder;

        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe("""{"fresh":true}""");
    }

    /// <summary>
    /// The files this writer creates hold credential pairs, so they are readable
    /// by their owner and by nobody else. One leg runs per machine; CI runs both.
    /// </summary>
    [Fact(SkipUnless = nameof(OnUnix), Skip = "File modes are a Unix behavior")]
    public async Task CreatesOwnerOnlyFilesOnUnix()
    {
        string path = Path.Combine(_directory, "state.json");

        await AtomicJsonFile.WriteAsync(path, new JsonObject { ["a"] = 1 }, TestContext.Current.CancellationToken);

        // The guard is for the platform analyzer; the attribute keeps the leg off Windows.
        if (!OperatingSystem.IsWindows())
        {
            UnixMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Directory.GetFiles(_directory).ShouldBe([path]);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Access control lists are a Windows behavior")]
    public async Task CreatesOwnerOnlyFilesOnWindows()
    {
        string path = Path.Combine(_directory, "state.json");

        await AtomicJsonFile.WriteAsync(path, new JsonObject { ["a"] = 1 }, TestContext.Current.CancellationToken);

        if (OperatingSystem.IsWindows())
        {
            // Inherited rules included: the assertion fails if inheritance is left on.
            GrantedIdentities(path).ShouldBe([CurrentUserSid()]);
        }

        Directory.GetFiles(_directory).ShouldBe([path]);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static UnixFileMode UnixMode(string path) => File.GetUnixFileMode(path);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string CurrentUserSid() => WindowsIdentity.GetCurrent().User!.Value;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> GrantedIdentities(string path) =>
    [
        .. new FileInfo(path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference.Value),
    ];

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
