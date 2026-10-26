using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// The owner's rule for a desktop notification: it names the role, the account
/// and the best spare account, and each threshold fires once per window reset.
/// A desktop notification changes state outside this process, so the calls the
/// fake records are the behavior under test.
/// </summary>
public sealed class UsageAlertNotifierTests : IDisposable
{
    private static readonly AccountEmail _ci = new("ci@example.com");
    private static readonly AccountEmail _spare = new("spare@example.com");
    private static readonly DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _reset = _now.AddDays(3);

    private readonly string _appData = Path.Combine(Path.GetTempPath(), "ccar-notifier-" + Guid.NewGuid().ToString("N"));

    private static Roster Roster() => new(
    [
        new RosterEntry(_ci, Alias: "work").ClaimingToken(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 1), setAt: null),
        new RosterEntry(_spare),
    ]);

    private static IReadOnlyList<UsageLimit> Limits(AccountEmail email) => email == _ci
        ? [new UsageLimit("weekly_all", LimitKind.WeeklyAll, "weekly", 72, null, _reset, null, IsActive: true)]
        : [new UsageLimit("weekly_all", LimitKind.WeeklyAll, "weekly", 10, null, _reset, null, IsActive: true)];

    private static IReadOnlyList<UsageAlert> Alerts() => UsageAlerts.Evaluate(Roster(), Limits, _now);

    private UsageAlertNotifier Notifier(RecordingDesktopNotifier desktop) =>
        new(_appData, desktop, NullLogger<UsageAlertNotifier>.Instance);

    [Fact]
    public async Task ANotificationNamesTheRoleTheAccountAndTheBestSpare()
    {
        RecordingDesktopNotifier desktop = new();

        await Notifier(desktop).NotifyAsync(Alerts(), Roster(), Limits, _now, TestContext.Current.CancellationToken);

        (string title, string body) = desktop.Shown.Single();
        title.ShouldBe("work: 7-day usage at 72%");
        body.ShouldBe("Backs CI role AUTOMATIONS. Past the 70% warning. Best spare: spare@example.com (10% used).");
    }

    private static Func<AccountEmail, IReadOnlyList<UsageLimit>> FiveHour(double percent, DateTimeOffset resets) => email => email == _ci
        ? [new UsageLimit("session", LimitKind.Session, "session", percent, null, resets, null, IsActive: true)]
        : [new UsageLimit("weekly_all", LimitKind.WeeklyAll, "weekly", 10, null, _reset, null, IsActive: true)];

    private async Task<IReadOnlyList<(string Title, string Body)>> NotifyFiveHourAsync(double percent, DateTimeOffset resets, DateTimeOffset now)
    {
        RecordingDesktopNotifier desktop = new();
        Func<AccountEmail, IReadOnlyList<UsageLimit>> limits = FiveHour(percent, resets);
        await Notifier(desktop).NotifyAsync(UsageAlerts.Evaluate(Roster(), limits, now), Roster(), limits, now, TestContext.Current.CancellationToken);
        return desktop.Shown;
    }

    [Theory]
    [InlineData(55, "Past the 50% warning. ")]
    [InlineData(80, "Past the 75% critical threshold. ")]
    public async Task AFiveHourNotificationNamesTheFiveHourThreshold(double percent, string threshold) =>
        (await NotifyFiveHourAsync(percent, _now.AddHours(2), _now)).Single().Body.ShouldContain(threshold);

    [Fact]
    public async Task AFiveHourLatchWrittenByTwoNineStillLoadsAndHoldsItsWindow()
    {
        // 2.9.x latched a 5-hour warning at 70%; the file names the level, not the percentage.
        DateTimeOffset resets = _now.AddHours(2);
        Directory.CreateDirectory(_appData);
        await File.WriteAllTextAsync(
            Path.Combine(_appData, UsageAlertNotifier.FileName),
            "[{\"account\":\"ci@example.com\",\"window\":\"5-hour\",\"level\":\"warning\",\"resetsAt\":\"" + resets.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}]",
            TestContext.Current.CancellationToken);

        (await NotifyFiveHourAsync(60, resets, _now)).ShouldBeEmpty();
        (await NotifyFiveHourAsync(80, resets, _now)).Single().Body.ShouldContain("Past the 75% critical threshold. ");
        (await NotifyFiveHourAsync(55, resets.AddHours(5), resets.AddMinutes(1))).Single().Body.ShouldContain("Past the 50% warning. ");
    }

    [Fact]
    public async Task TheSameThresholdIsNotNotifiedAgainEvenAfterARestart()
    {
        RecordingDesktopNotifier desktop = new();
        await Notifier(desktop).NotifyAsync(Alerts(), Roster(), Limits, _now, TestContext.Current.CancellationToken);

        await Notifier(desktop).NotifyAsync(Alerts(), Roster(), Limits, _now.AddMinutes(15), TestContext.Current.CancellationToken);

        desktop.Shown.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ANotificationThatFailsToShowDoesNotThrow()
    {
        RecordingDesktopNotifier desktop = new() { Failure = "no desktop" };

        await Notifier(desktop).NotifyAsync(Alerts(), Roster(), Limits, _now, TestContext.Current.CancellationToken);

        desktop.Shown.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ALatchThatCannotBeWrittenStillShowsTheNotification()
    {
        // A read-only directory refuses the write with UnauthorizedAccessException, not IOException.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes make the directory read-only.");
            return;
        }

        Directory.CreateDirectory(_appData);
        File.SetUnixFileMode(_appData, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        RecordingDesktopNotifier desktop = new();

        await Notifier(desktop).NotifyAsync(Alerts(), Roster(), Limits, _now, TestContext.Current.CancellationToken);

        desktop.Shown.Count.ShouldBe(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_appData))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_appData, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(_appData, recursive: true);
        }
    }
}
