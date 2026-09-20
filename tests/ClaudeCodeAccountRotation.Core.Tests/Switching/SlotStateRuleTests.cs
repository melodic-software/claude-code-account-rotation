using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.Core.Tests.Switching;

/// <summary>
/// One fact per row of the reconciliation table in the cross-OS rotation design
/// record (section 9.4), read from the Windows side, plus the two precedence
/// facts the table's ordering implies.
/// </summary>
public sealed class SlotStateRuleTests
{
    private static readonly DateTimeOffset _since = new(2026, 9, 20, 14, 2, 0, TimeSpan.Zero);

    private static HolderRecord Record(SideName side) =>
        new(side, RefreshTokenFingerprint.FromRefreshToken("refresh-" + side.Value), _since);

    [Fact]
    public void ASlotFileWithNoRecordIsParked()
    {
        SlotStateRule.Resolve(slotFileExists: true, record: null, SideName.Windows, transitFileExists: false)
            .ShouldBe(SlotState.Parked);
    }

    [Fact]
    public void ASlotFileWithARecordNamingThisSideIsStillParked()
    {
        // The pair is in the slot, so nobody holds it; the record is stale and the
        // caller drops it. The rule only says what the files mean.
        SlotStateRule.Resolve(slotFileExists: true, Record(SideName.Windows), SideName.Windows, transitFileExists: false)
            .ShouldBe(SlotState.Parked);
    }

    [Fact]
    public void NoSlotFileNoRecordAndNoMailboxFileIsNeverLoggedIn()
    {
        SlotStateRule.Resolve(slotFileExists: false, record: null, SideName.Windows, transitFileExists: false)
            .ShouldBe(SlotState.NeverLoggedIn);
    }

    [Fact]
    public void ARecordNamingThisSideIsHeldHere()
    {
        // Whether the recorded fingerprint matches this side's live pair or its
        // rotation is the caller's own check; the rule reads the side alone.
        SlotStateRule.Resolve(slotFileExists: false, Record(SideName.Windows), SideName.Windows, transitFileExists: false)
            .ShouldBe(SlotState.HeldHere);
    }

    [Fact]
    public void ARecordNamingAnotherSideIsHeldElsewhere()
    {
        SlotStateRule.Resolve(slotFileExists: false, Record(SideName.Wsl), SideName.Windows, transitFileExists: false)
            .ShouldBe(SlotState.HeldElsewhere);
    }

    [Fact]
    public void AMailboxFileNamingTheAccountIsInTransit()
    {
        SlotStateRule.Resolve(slotFileExists: false, record: null, SideName.Windows, transitFileExists: true)
            .ShouldBe(SlotState.InTransit);
    }

    [Fact]
    public void ASlotHoldingAPairOutranksARecordNamingAnotherSide()
    {
        SlotStateRule.Resolve(slotFileExists: true, Record(SideName.Wsl), SideName.Windows, transitFileExists: false)
            .ShouldBe(SlotState.Parked);
    }

    [Fact]
    public void ATransitFileOutranksEverything()
    {
        SlotStateRule.Resolve(slotFileExists: true, Record(SideName.Wsl), SideName.Windows, transitFileExists: true)
            .ShouldBe(SlotState.InTransit);
    }
}
