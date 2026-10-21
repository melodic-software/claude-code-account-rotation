using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>
/// Matching a stored key by the Admin API's <c>partial_key_hint</c>. The hint is
/// the documented example, ending <c>R2D...igAA</c>; keys are built from
/// split pieces so no fixture holds a literal key prefix.
/// </summary>
public sealed class PartialKeyHintTests
{
    private const string Prefix = "sk-ant-" + "api03-";
    private const string DocumentedHint = Prefix + "R2D...igAA";
    private const string Key = Prefix + "R2D" + "x7Qm0Lr5Wd9" + "igAA";

    private static ListedApiKey Listed(string id, string? hint) => new(id, id, "active", hint, null);

    [Fact]
    public void TheDocumentedHintMatchesAKeyWithThatPrefixAndSuffix()
    {
        ListedApiKey mine = Listed("apikey_mine", DocumentedHint);

        KeyHintMatch match = PartialKeyHint.Match(Key, [Listed("apikey_other", Prefix + "Zz9...q0Q0"), mine]);

        match.Key.ShouldBe(mine);
    }

    [Fact]
    public void ANullHintCannotBeTold()
    {
        KeyHintMatch match = PartialKeyHint.Match(Key, [Listed("apikey_legacy", null)]);

        match.Key.ShouldBeNull();
        match.Reason.ShouldNotBeNull();
    }

    [Fact]
    public void ANullHintBesideAFittingOneCannotBeTold()
    {
        KeyHintMatch match = PartialKeyHint.Match(Key, [Listed("apikey_mine", DocumentedHint), Listed("apikey_legacy", null)]);

        match.Key.ShouldBeNull();
    }

    [Fact]
    public void ZeroMatchesCannotBeTold()
    {
        KeyHintMatch match = PartialKeyHint.Match(Key, [Listed("apikey_other", Prefix + "Zz9...q0Q0")]);

        match.Key.ShouldBeNull();
        match.Reason.ShouldBe("no listed key's hint fits this key");
    }

    [Fact]
    public void AnEmptyListingCannotBeTold() =>
        PartialKeyHint.Match(Key, []).Key.ShouldBeNull();

    [Fact]
    public void TwoMatchesCannotBeTold()
    {
        KeyHintMatch match = PartialKeyHint.Match(Key, [Listed("apikey_a", DocumentedHint), Listed("apikey_b", DocumentedHint)]);

        match.Key.ShouldBeNull();
        match.Reason.ShouldBe("2 keys fit this key's hint");
    }

    [Theory]
    [InlineData("R2DigAA")]
    [InlineData("...igAA")]
    [InlineData("R2D...")]
    [InlineData("R2D...ig...AA")]
    public void AHintThatIsNotPrefixEllipsisSuffixCannotBeTold(string hint)
    {
        ArgumentNullException.ThrowIfNull(hint);
        KeyHintMatch match = PartialKeyHint.Match(Key, [Listed("apikey_odd", hint.StartsWith("...", StringComparison.Ordinal) ? hint : Prefix + hint)]);

        match.Key.ShouldBeNull();
    }

    [Fact]
    public void AKeyNoLongerThanTheHintsTwoEndsDoesNotFit()
    {
        // The two ends overlap rather than leave the redacted middle.
        KeyHintMatch match = PartialKeyHint.Match(Prefix + "R2DigAA", [Listed("apikey_mine", DocumentedHint)]);

        match.Key.ShouldBeNull();
        match.Reason.ShouldBe("no listed key's hint fits this key");
    }
}
