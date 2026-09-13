using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.Core.Tests.Switching;

public sealed class SwitchPlannerTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-planner-tests");
    private static readonly string _liveDirectory = Path.Combine(_root, "live");
    private static readonly string _profilesRoot = Path.Combine(_root, "profiles");

    private static OAuthAccountBlock Account(string email) =>
        OAuthAccountBlock.FromJson(new JsonObject { ["emailAddress"] = email, ["organizationRateLimitTier"] = "default_claude_max_20x" });

    private static CredentialPair Pair(string refreshToken, DateTimeOffset? loginExpiresAt = null) =>
        CredentialPair.FromJson(new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = "access-" + refreshToken,
                ["refreshToken"] = refreshToken,
                ["expiresAt"] = _now.AddHours(8).ToUnixTimeMilliseconds(),
                ["refreshTokenExpiresAt"] = (loginExpiresAt ?? _now.AddDays(28)).ToUnixTimeMilliseconds(),
            },
        }).Value;

    private static SwitchPlanningInput Baseline()
    {
        CredentialPair livePair = Pair("refresh-a");
        AccountEmail incoming = AccountEmail.Parse("b@example.com").Value;
        return new SwitchPlanningInput(
            Live: new LiveAccountState(
                _liveDirectory,
                Path.Combine(_root, ".claude.json"),
                Account("a@example.com"),
                HasCredentials: true,
                livePair.Fingerprint,
                FreshLockFileName: null),
            Target: new ParkedProfile(incoming, Path.Combine(_profilesRoot, "b@example.com"), HasCredentials: true, Account("b@example.com")),
            LiveCredentials: livePair,
            TargetCredentials: Pair("refresh-b"),
            Policy: ManagedLoginPolicy.None,
            JournalOpen: false,
            LiveFingerprintOwner: AccountEmail.Parse("a@example.com").Value,
            ProfilesRoot: _profilesRoot,
            Now: _now);
    }

    [Fact]
    public void RefusesWhenTheManagedPolicyCouldNotBeRead()
    {
        SwitchPlanningInput input = Baseline() with { Policy = new ManagedLoginPolicy(null, "HKLM (unreadable: access denied)", Unreadable: true) };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.ManagedPolicyUnreadable);
    }

    [Fact]
    public void PlansParkingTheLiveAccountAndUnparkingTheTarget()
    {
        SwitchPlan plan = SwitchPlanner.Plan(Baseline()).Value;

        plan.Outgoing.ShouldBe(AccountEmail.Parse("a@example.com").Value);
        plan.OutgoingFolderPath.ShouldBe(Path.Combine(_profilesRoot, "a@example.com"));
        plan.Incoming.ShouldBe(AccountEmail.Parse("b@example.com").Value);
        plan.IncomingFolderPath.ShouldBe(Path.Combine(_profilesRoot, "b@example.com"));
        plan.IncomingAccount.Email.ShouldBe(plan.Incoming);
    }

    [Fact]
    public void ALiveDirectoryWithoutCredentialsYieldsNothingToPark()
    {
        SwitchPlanningInput input = Baseline();
        input = input with
        {
            Live = input.Live with { HasCredentials = false, Fingerprint = null },
            LiveCredentials = null,
            LiveFingerprintOwner = null,
        };

        SwitchPlan plan = SwitchPlanner.Plan(input).Value;

        plan.Outgoing.ShouldBeNull();
        plan.OutgoingFolderPath.ShouldBeNull();
    }

    [Fact]
    public void RefusesWhenTheLivePairHasNoAccountIdentity()
    {
        // The pair exists but the state file names nobody: there is no folder to park it under.
        SwitchPlanningInput input = Baseline();
        input = input with { Live = input.Live with { Account = null }, LiveFingerprintOwner = null };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);
    }

    [Fact]
    public void RefusesWhenTheTargetFolderIsTheLiveDirectory()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { Target = input.Target with { FolderPath = _liveDirectory + Path.DirectorySeparatorChar } };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.TargetIsLiveDirectory);
    }

    [Fact]
    public void RefusesWhenTheTargetHasNoCredentials()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { Target = input.Target with { HasCredentials = false }, TargetCredentials = null };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.TargetHasNoCredentials);
    }

    [Fact]
    public void RefusesWhenTheTargetHasNoAccountBlock()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { Target = input.Target with { Account = null } };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.TargetHasNoAccountBlock);
    }

    [Fact]
    public void RefusesWhenTheTargetIsAlreadyLive()
    {
        SwitchPlanningInput input = Baseline();
        input = input with
        {
            Live = input.Live with { Account = Account("B@Example.com") },
            LiveFingerprintOwner = AccountEmail.Parse("b@example.com").Value,
        };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.AlreadyOnTarget);
    }

    [Fact]
    public void RefusesWhenTheTargetSharesTheLiveRefreshToken()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { TargetCredentials = Pair("refresh-a") };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.SharesLiveRefreshToken);
    }

    [Fact]
    public void RefusesWhileAFreshLockFileSitsBesideTheLivePair()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { Live = input.Live with { FreshLockFileName = "something.lock" } };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.RefreshLockPresent);
    }

    [Fact]
    public void RefusesWhenTheTargetIsStrandedInRecovery()
    {
        // The parked file still on disk is the pair the failed write-back was meant
        // to replace, and its refresh token died the moment the token endpoint
        // answered. Moving it to live would put a dead pair where the CLI reads,
        // and no restore could apply afterwards. The refusal outranks the expiry
        // check, which would otherwise report the wrong reason for the same folder.
        SwitchPlanningInput input = Baseline() with
        {
            TargetHasRecoveryFile = true,
            TargetCredentials = Pair("refresh-b", loginExpiresAt: _now.AddMinutes(-1)),
        };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.TargetStrandedInRecovery);
    }

    [Fact]
    public void RefusesWhenTheTargetLoginHasExpired()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { TargetCredentials = Pair("refresh-b", loginExpiresAt: _now.AddMinutes(-1)) };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.TargetLoginExpired);
    }

    [Fact]
    public void RefusesUnderADeviceManagedLoginPolicy()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { Policy = new ManagedLoginPolicy("org-uuid", "managed-settings.json") };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.SwitchingBlockedByManagedPolicy);
    }

    [Fact]
    public void RefusesWhileAnEarlierSwitchIsUnreconciled()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { JournalOpen = true };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);
    }

    [Fact]
    public void RefusesWhenTheStateFileNamesAnAccountOtherThanTheLivePairsOwner()
    {
        SwitchPlanningInput input = Baseline();
        input = input with { LiveFingerprintOwner = AccountEmail.Parse("c@example.com").Value };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);
    }

    [Fact]
    public void RefusalsAreCheckedInTheSpikesOrder()
    {
        SwitchPlanningInput input = Baseline();
        input = input with
        {
            Target = input.Target with { HasCredentials = false, Account = null },
            TargetCredentials = null,
            Policy = new ManagedLoginPolicy("org-uuid", "managed-settings.json"),
        };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.TargetHasNoCredentials);
    }

    [Fact]
    public void AnUnverifiedLiveIdentityOutranksEveryOtherAnswer()
    {
        // The state file already names the target, but with a switch unreconciled that
        // claim cannot be trusted, so the refusal is the unverified identity, not AlreadyOnTarget.
        SwitchPlanningInput input = Baseline();
        input = input with { Live = input.Live with { Account = Account("b@example.com") }, JournalOpen = true };

        SwitchPlanner.Plan(input).Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);
    }
}
