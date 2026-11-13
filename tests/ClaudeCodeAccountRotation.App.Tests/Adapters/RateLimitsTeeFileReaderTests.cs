using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class RateLimitsTeeFileReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _teePath;

    public RateLimitsTeeFileReaderTests()
    {
        Directory.CreateDirectory(_directory);
        _teePath = Path.Combine(_directory, "rate-limits.json");
    }

    [Fact]
    public async Task AMissingFileIsAnAbsentSnapshot()
    {
        RateLimitsTeeFileReader reader = new(_teePath);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task TornJsonIsAnAbsentSnapshot()
    {
        await WriteAsync("""{"captured_at":"2026-09-07T15:33:52Z","rate_limits":{"five_ho""");
        RateLimitsTeeFileReader reader = new(_teePath);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AFileWithoutRateLimitsIsAnAbsentSnapshot()
    {
        await WriteAsync("""{"captured_at":"2026-09-07T15:33:52Z","session_id":"s-1"}""");
        RateLimitsTeeFileReader reader = new(_teePath);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ParsesBothWindowsAndTheSessionId()
    {
        await WriteAsync(Tee(email: null));
        RateLimitsTeeFileReader reader = new(_teePath);

        StatuslineSnapshot snapshot = (await reader.ReadAsync(TestContext.Current.CancellationToken))!;

        snapshot.CapturedAt.ShouldBe(DateTimeOffset.Parse("2026-09-07T15:33:52Z", System.Globalization.CultureInfo.InvariantCulture));
        snapshot.SessionId.ShouldBe("00000000-0000-4000-8000-000000000001");
        snapshot.FiveHourPercent.ShouldBe(69);
        snapshot.FiveHourResetsAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1788800400));
        snapshot.SevenDayPercent.ShouldBe(43);
        snapshot.SevenDayResetsAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1789315200));
    }

    [Fact]
    public async Task ParsesAccountEmailWhenPresent()
    {
        await WriteAsync(Tee("Dev.A@example.com"));
        RateLimitsTeeFileReader reader = new(_teePath);

        StatuslineSnapshot snapshot = (await reader.ReadAsync(TestContext.Current.CancellationToken))!;

        snapshot.Account!.Value.Value.ShouldBe("dev.a@example.com");
    }

    [Fact]
    public async Task AnOlderWriterWithoutAnAccountKeyLeavesTheSnapshotUnattributed()
    {
        await WriteAsync(Tee(email: null));
        RateLimitsTeeFileReader reader = new(_teePath);

        (await reader.ReadAsync(TestContext.Current.CancellationToken))!.Account.ShouldBeNull();
    }

    [Theory]
    // The reader contract calls every value here untrusted: an object where a
    // string belongs, and an unparsable address, both leave it unattributed.
    [InlineData("""{"uuid":"u-1"}""")]
    [InlineData("\"not-an-email\"")]
    public async Task AnUntrustworthyAccountValueLeavesTheSnapshotUnattributed(string accountValue)
    {
        await WriteAsync("""{"captured_at":"2026-09-07T15:33:52Z","rate_limits":{"five_hour":{"used_percentage":69,"resets_at":1788800400}},"account":"""
            + accountValue
            + "}");
        RateLimitsTeeFileReader reader = new(_teePath);

        (await reader.ReadAsync(TestContext.Current.CancellationToken))!.Account.ShouldBeNull();
    }

    [Fact]
    public async Task AnAbsurdPercentageLeavesThatWindowUnknownAndKeepsTheOther()
    {
        await WriteAsync("""
            {"captured_at":"2026-09-07T15:33:52Z","rate_limits":{"five_hour":{"used_percentage":4200,"resets_at":1788800400},"seven_day":{"used_percentage":43,"resets_at":1789315200}}}
            """);
        RateLimitsTeeFileReader reader = new(_teePath);

        StatuslineSnapshot snapshot = (await reader.ReadAsync(TestContext.Current.CancellationToken))!;

        snapshot.FiveHourPercent.ShouldBeNull();
        snapshot.SevenDayPercent.ShouldBe(43);
    }

    [Theory]
    // DateTimeOffset.FromUnixTimeSeconds throws outside its own range, and this
    // number comes from a file another process writes. Fail open per window.
    [InlineData(1000000000000000000L)]
    [InlineData(-1000000000000000000L)]
    public async Task AnOutOfRangeResetTimeLeavesThatWindowsInstantUnknown(long absurd)
    {
        await WriteAsync(Tee("dev.a@example.com", fiveHourResetsAt: absurd));
        RateLimitsTeeFileReader reader = new(_teePath);

        StatuslineSnapshot snapshot = (await reader.ReadAsync(TestContext.Current.CancellationToken))!;

        snapshot.FiveHourResetsAt.ShouldBeNull();
        snapshot.FiveHourPercent.ShouldBe(69);
        snapshot.SevenDayResetsAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1789315200));
        snapshot.Account!.Value.Value.ShouldBe("dev.a@example.com");
    }

    [Fact]
    public async Task AnOutOfRangeCapturedAtIsAnAbsentSnapshot()
    {
        await WriteAsync("""{"captured_at":1000000000000000000,"rate_limits":{"five_hour":{"used_percentage":69}}}""");
        RateLimitsTeeFileReader reader = new(_teePath);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task APathThatCannotBeOpenedIsAnAbsentSnapshot()
    {
        // A real file that exists and cannot be opened. A directory would not do:
        // File.Exists is false for one, so the old exists-then-open check would
        // have short-circuited and this test would pass without the fix.
        await WriteAsync(Tee("dev.a@example.com"));
        DenyReading(_teePath);
        try
        {
            File.Exists(_teePath).ShouldBeTrue();
            // Pinned: this is the exception type the widened catch names.
            Should.Throw<UnauthorizedAccessException>(() => new FileStream(_teePath, FileMode.Open, FileAccess.Read));
            RateLimitsTeeFileReader reader = new(_teePath);

            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
        }
        finally
        {
            // Before Dispose deletes the directory, or the cleanup fails too.
            AllowReading(_teePath);
        }
    }

    internal static void DenyReading(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsReadDeny(path, deny: true);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
        }
    }

    internal static void AllowReading(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsReadDeny(path, deny: false);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetWindowsReadDeny(string path, bool deny)
    {
        FileInfo file = new(path);
        System.Security.AccessControl.FileSecurity security = file.GetAccessControl();
        System.Security.AccessControl.FileSystemAccessRule rule = new(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.Read,
            System.Security.AccessControl.AccessControlType.Deny);
        if (deny)
        {
            security.AddAccessRule(rule);
        }
        else
        {
            security.RemoveAccessRuleAll(rule);
        }

        file.SetAccessControl(security);
    }

    /// <summary>The tee file as rate-limits 0.8.0 writes it.</summary>
    internal static string Tee(string? email, long fiveHourResetsAt = 1788800400, long sevenDayResetsAt = 1789315200)
    {
        JsonObject tee = new()
        {
            ["captured_at"] = "2026-09-07T15:33:52Z",
            ["session_id"] = "00000000-0000-4000-8000-000000000001",
            ["session_name"] = "a session",
            ["rate_limits"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["used_percentage"] = 69, ["resets_at"] = fiveHourResetsAt },
                ["seven_day"] = new JsonObject { ["used_percentage"] = 43, ["resets_at"] = sevenDayResetsAt },
            },
        };
        if (email is not null)
        {
            tee["account"] = new JsonObject { ["email"] = email };
        }

        return tee.ToJsonString();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Task WriteAsync(string content) => File.WriteAllTextAsync(_teePath, content, TestContext.Current.CancellationToken);
}
