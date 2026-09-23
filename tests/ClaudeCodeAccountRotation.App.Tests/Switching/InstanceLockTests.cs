using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

public sealed class InstanceLockTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public InstanceLockTests() => Directory.CreateDirectory(_appData);

    public static bool OnUnix => !OperatingSystem.IsWindows();

    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact]
    public void ASecondInstanceIsRefusedWithTheListenUrlAndNotTheToken()
    {
        Result<InstanceLock, string> first = InstanceLock.TryAcquire(_appData, "http://127.0.0.1:48211");
        first.IsSuccess.ShouldBeTrue();
        string urlPath = Path.Combine(_appData, InstanceLock.UrlFileName);

        using (first.Value)
        {
            string token = first.Value.Token;
            byte[] written = File.ReadAllBytes(urlPath);
            written.ShouldBe(Encoding.UTF8.GetBytes("http://127.0.0.1:48211\n" + token + "\n"));

            Result<InstanceLock, string> second = InstanceLock.TryAcquire(_appData, "http://127.0.0.1:48212");
            second.IsFailure.ShouldBeTrue();
            second.Error.ShouldContain("http://127.0.0.1:48211");
            second.Error.ShouldNotContain(token);

            first.Value.PublishListenUrl("http://127.0.0.1:48299");
            string[] lines = File.ReadAllText(urlPath).Split('\n');
            lines[0].ShouldBe("http://127.0.0.1:48299");
            lines[1].ShouldBe(token);

            Result<InstanceLock, string> afterPublish = InstanceLock.TryAcquire(_appData, "http://127.0.0.1:48213");
            afterPublish.IsFailure.ShouldBeTrue();
            afterPublish.Error.ShouldContain("http://127.0.0.1:48299");
            afterPublish.Error.ShouldNotContain(token);
        }
    }

    [Fact]
    public void ASecondInstanceDoesNotEchoAnUnreadableInstanceFile()
    {
        Result<InstanceLock, string> first = InstanceLock.TryAcquire(_appData, "http://127.0.0.1:48211");
        first.IsSuccess.ShouldBeTrue();

        using (first.Value)
        {
            string marker = "do-not-echo-" + first.Value.Token;
            File.WriteAllText(Path.Combine(_appData, InstanceLock.UrlFileName), marker);

            Result<InstanceLock, string> second = InstanceLock.TryAcquire(_appData, "http://127.0.0.1:48212");

            second.IsFailure.ShouldBeTrue();
            second.Error.ShouldContain("an unknown address");
            second.Error.ShouldNotContain(first.Value.Token);
            second.Error.ShouldNotContain("do-not-echo");
        }
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "File modes are a Unix behavior")]
    public void TheInstanceUrlIsOwnerReadWriteIncludingWhenAWiderFileAlreadyExisted()
    {
        string wider = Path.Combine(_appData, "wider");
        Directory.CreateDirectory(wider);
        string preexisting = Path.Combine(wider, InstanceLock.UrlFileName);
        File.WriteAllText(preexisting, "old\n");
        if (!OperatingSystem.IsWindows())
        {
            Widen(preexisting);
        }

        Result<InstanceLock, string> replaced = InstanceLock.TryAcquire(wider, "http://127.0.0.1:48211");
        replaced.IsSuccess.ShouldBeTrue();
        using (replaced.Value)
        {
            if (!OperatingSystem.IsWindows())
            {
                OwnerReadWrite(preexisting);
            }

            File.ReadAllText(preexisting).ShouldStartWith("http://127.0.0.1:48211\n");
        }

        string fresh = Path.Combine(_appData, "fresh");
        Result<InstanceLock, string> created = InstanceLock.TryAcquire(fresh, "http://127.0.0.1:48212");
        created.IsSuccess.ShouldBeTrue();
        using (created.Value)
        {
            if (!OperatingSystem.IsWindows())
            {
                OwnerReadWrite(Path.Combine(fresh, InstanceLock.UrlFileName));
            }
        }
    }

    [Fact]
    public void ReadRunningReturnsThePublishedUrlAndTokenAndNullOnceReleased()
    {
        Result<InstanceLock, string> acquired = InstanceLock.TryAcquire(_appData, "http://127.0.0.1:48211");
        acquired.IsSuccess.ShouldBeTrue();
        using (acquired.Value)
        {
            acquired.Value.PublishListenUrl("http://127.0.0.1:48299");
            InstanceLock.ReadRunning(_appData).ShouldBe(("http://127.0.0.1:48299", acquired.Value.Token));
        }

        InstanceLock.ReadRunning(_appData).ShouldBeNull();
        File.WriteAllText(Path.Combine(_appData, InstanceLock.UrlFileName), "http://127.0.0.1:48211\n");
        InstanceLock.ReadRunning(_appData).ShouldBeNull();
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Access control lists are a Windows behavior")]
    public void TheInstanceUrlGrantsOnlyTheCurrentUserOnWindows()
    {
        Result<InstanceLock, string> acquired = InstanceLock.TryAcquire(_appData, "http://127.0.0.1:48211");
        acquired.IsSuccess.ShouldBeTrue();
        using (acquired.Value)
        {
            if (OperatingSystem.IsWindows())
            {
                GrantedIdentities(Path.Combine(_appData, InstanceLock.UrlFileName)).ShouldBe([CurrentUserSid()]);
            }
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Widen(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void OwnerReadWrite(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

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
        if (Directory.Exists(_appData))
        {
            Directory.Delete(_appData, recursive: true);
        }
    }
}
