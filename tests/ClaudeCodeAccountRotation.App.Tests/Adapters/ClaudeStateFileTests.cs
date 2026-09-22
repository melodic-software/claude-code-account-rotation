using System.Text;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class ClaudeStateFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public ClaudeStateFileTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, ".claude.json");
    }

    private static OAuthAccountBlock Account(string email) =>
        OAuthAccountBlock.FromJson(new JsonObject
        {
            ["accountUuid"] = Guid.NewGuid().ToString(),
            ["emailAddress"] = email,
            ["organizationRateLimitTier"] = "default_claude_max_20x",
        });

    /// <summary>
    /// A state file shaped like the real one: a large projects map with
    /// non-ASCII strings, the account block in the middle, more keys after it,
    /// and the CLI's two-space indentation. Over 90 KB.
    /// </summary>
    private static string LargeStateFile(string email)
    {
        StringBuilder builder = new();
        builder.Append("{\n  \"numStartups\": 412,\n  \"projects\": {\n");
        for (int index = 0; index < 400; index++)
        {
            builder.Append("    \"/srv/dev/repos/projekt-").Append(index).Append("\": {\n");
            builder.Append("      \"allowedTools\": [\"Bash(ls:*)\", \"Read\"],\n");
            builder.Append("      \"lastPrompt\": \"Größe prüfen — 日本語 テスト ✓ ").Append(new string('x', 120)).Append("\",\n");
            builder.Append("      \"hasTrustDialogAccepted\": true\n");
            builder.Append("    },\n");
        }

        builder.Append("    \"/srv/dev/last\": { \"hasTrustDialogAccepted\": false }\n  },\n");
        builder.Append("  \"oauthAccount\": {\n    \"accountUuid\": \"old-uuid\",\n    \"emailAddress\": \"").Append(email).Append("\",\n    \"organizationRateLimitTier\": \"default_claude_max_20x\"\n  },\n");
        builder.Append("  \"cachedChangelog\": \"# Changelog\\n\\n## 2.1.261\\n\\n- Fixes\\n\",\n");
        builder.Append("  \"fallbackAvailableWarningThreshold\": 0.5\n}\n");
        return builder.ToString();
    }

    [Fact]
    public async Task ReadsTheAccountBlock()
    {
        await File.WriteAllTextAsync(_path, LargeStateFile("a@example.com"), TestContext.Current.CancellationToken);
        ClaudeStateFile stateFile = new(_path);

        OAuthAccountBlock? block = await stateFile.ReadAccountBlockAsync(TestContext.Current.CancellationToken);

        block!.Email.ShouldBe(AccountEmail.Parse("a@example.com").Value);
        block.OrganizationRateLimitTier.ShouldBe("default_claude_max_20x");
    }

    [Fact]
    public async Task ReadReturnsNullWhenTheFileHasNoAccountBlock()
    {
        await File.WriteAllTextAsync(_path, """{"numStartups": 1}""", TestContext.Current.CancellationToken);

        (await new ClaudeStateFile(_path).ReadAccountBlockAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>
    /// The same file with the account block written first, so a truncation can land
    /// past a whole <c>oauthAccount</c> value.
    /// </summary>
    private static string AccountFirstStateFile(string email)
    {
        JsonObject whole = JsonNode.Parse(LargeStateFile(email))!.AsObject();
        JsonObject reordered = new() { ["oauthAccount"] = whole["oauthAccount"]!.DeepClone() };
        foreach (KeyValuePair<string, JsonNode?> property in whole.Where(property => property.Key != "oauthAccount"))
        {
            reordered[property.Key] = property.Value?.DeepClone();
        }

        return reordered.ToJsonString();
    }

    /// <summary>
    /// A rewrite cut off inside a property that follows a whole <c>oauthAccount</c>
    /// value. The block alone reads perfectly; only the rest of the document says
    /// the file is half written.
    /// </summary>
    private static byte[] TruncatedPastTheAccountBlock()
    {
        byte[] whole = Encoding.UTF8.GetBytes(AccountFirstStateFile("a@example.com"));
        byte[] prefix = whole[..(whole.Length / 2)];
        // The account block has to be whole in these bytes, or the truncation proves nothing.
        Encoding.UTF8.GetString(prefix).ShouldContain("\"organizationRateLimitTier\":\"default_claude_max_20x\"}");
        return prefix;
    }

    [Fact]
    public async Task ReadRefusesAnEmptyFileRatherThanReportingNoAccount()
    {
        // Claude Code truncates this file before rewriting it, so a read can land on
        // zero bytes. Reporting that as "no account block" would tell a caller
        // planning a switch that nobody is logged in.
        await File.WriteAllBytesAsync(_path, [], TestContext.Current.CancellationToken);

        InvalidDataException failure = await Should.ThrowAsync<InvalidDataException>(
            async () => await new ClaudeStateFile(_path).ReadAccountBlockAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(_path);
    }

    [Fact]
    public async Task ReadRefusesAFileCaughtHalfWritten()
    {
        byte[] whole = Encoding.UTF8.GetBytes(LargeStateFile("a@example.com"));
        await File.WriteAllBytesAsync(_path, whole[..40], TestContext.Current.CancellationToken);

        InvalidDataException failure = await Should.ThrowAsync<InvalidDataException>(
            async () => await new ClaudeStateFile(_path).ReadAccountBlockAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(_path);
    }

    [Fact]
    public async Task PatchRefusesAFileCaughtHalfWrittenRatherThanPatchingOverIt()
    {
        byte[] whole = Encoding.UTF8.GetBytes(LargeStateFile("a@example.com"));
        await File.WriteAllBytesAsync(_path, whole[..40], TestContext.Current.CancellationToken);

        InvalidDataException failure = await Should.ThrowAsync<InvalidDataException>(
            async () => await new ClaudeStateFile(_path).PatchAccountBlockAsync(Account("b@example.com"), TestContext.Current.CancellationToken));

        // Named, so this discriminates the torn-read refusal from AppendProperty's
        // older "not a JSON object" guard, which these bytes would also trip.
        failure.Message.ShouldContain(_path);
        (await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken)).ShouldBe(whole[..40]);
        Directory.GetFiles(_directory).ShouldBe([_path]);
    }

    [Fact]
    public async Task ReadRefusesAFileCutOffAfterTheAccountBlock()
    {
        await File.WriteAllBytesAsync(_path, TruncatedPastTheAccountBlock(), TestContext.Current.CancellationToken);

        InvalidDataException failure = await Should.ThrowAsync<InvalidDataException>(
            async () => await new ClaudeStateFile(_path).ReadAccountBlockAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(_path);
    }

    [Fact]
    public async Task ReadRefusesBytesLeftAfterAWholeDocument()
    {
        // The other half of the same guard: the root object closes, so the span is
        // sound, and everything after it says these bytes are not one document.
        await File.WriteAllTextAsync(_path, AccountFirstStateFile("a@example.com") + "{\"numStartups\": 1}", TestContext.Current.CancellationToken);

        InvalidDataException failure = await Should.ThrowAsync<InvalidDataException>(
            async () => await new ClaudeStateFile(_path).ReadAccountBlockAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(_path);
    }

    [Fact]
    public async Task PatchRefusesAFileCutOffAfterTheAccountBlockRatherThanSplicingIntoIt()
    {
        // The span is sound, so the splice would succeed and write the half of the
        // document that was read back over the whole file.
        byte[] prefix = TruncatedPastTheAccountBlock();
        await File.WriteAllBytesAsync(_path, prefix, TestContext.Current.CancellationToken);

        InvalidDataException failure = await Should.ThrowAsync<InvalidDataException>(
            async () => await new ClaudeStateFile(_path).PatchAccountBlockAsync(Account("b@example.com"), TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(_path);
        (await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken)).ShouldBe(prefix);
        Directory.GetFiles(_directory).ShouldBe([_path]);
    }

    [Fact]
    public async Task PatchChangesOnlyTheAccountSpan()
    {
        string original = LargeStateFile("a@example.com");
        await File.WriteAllTextAsync(_path, original, TestContext.Current.CancellationToken);
        original.Length.ShouldBeGreaterThan(90_000);
        ClaudeStateFile stateFile = new(_path);

        await stateFile.PatchAccountBlockAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        string patched = await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken);
        int originalStart = original.IndexOf("\"oauthAccount\": ", StringComparison.Ordinal) + "\"oauthAccount\": ".Length;
        int originalEnd = original.IndexOf(",\n  \"cachedChangelog\"", StringComparison.Ordinal);
        int patchedEnd = patched.IndexOf(",\n  \"cachedChangelog\"", StringComparison.Ordinal);
        patched[..originalStart].ShouldBe(original[..originalStart]);
        patched[patchedEnd..].ShouldBe(original[originalEnd..]);
        JsonNode.Parse(patched)!["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
        JsonNode.Parse(patched)!["projects"]!.AsObject().Count.ShouldBe(401);
        Directory.GetFiles(_directory).ShouldBe([_path]);
    }

    [Fact]
    public async Task PatchSurvivesAnExternalRewriteBetweenReadAndPatch()
    {
        await File.WriteAllTextAsync(_path, LargeStateFile("a@example.com"), TestContext.Current.CancellationToken);
        ClaudeStateFile stateFile = new(_path);
        _ = await stateFile.ReadAccountBlockAsync(TestContext.Current.CancellationToken);

        // A session rewrites an unrelated key after the tool's read.
        JsonNode external = JsonNode.Parse(await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken))!;
        external["numStartups"] = 413;
        await File.WriteAllTextAsync(_path, external.ToJsonString(), TestContext.Current.CancellationToken);

        await stateFile.PatchAccountBlockAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        JsonNode result = JsonNode.Parse(await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken))!;
        result["numStartups"]!.GetValue<int>().ShouldBe(413);
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
    }

    [Fact]
    public async Task PatchAddsTheBlockWhenTheFileHasNone()
    {
        await File.WriteAllTextAsync(_path, "{\n  \"numStartups\": 1\n}\n", TestContext.Current.CancellationToken);

        await new ClaudeStateFile(_path).PatchAccountBlockAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        JsonNode result = JsonNode.Parse(await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken))!;
        result["numStartups"]!.GetValue<int>().ShouldBe(1);
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
    }

    [Fact]
    public async Task PatchRetriesWhenTheFileChangesBetweenTheReadAndTheReplace()
    {
        // The CLI's own rewrites often keep the file's length (a counter ticking up) and
        // can land inside one timestamp tick, so the change detector must compare bytes,
        // not a length and a last-write time. The seam fires after the tool's read and
        // before its replace; the rewrite it performs is invisible to a stamp check.
        await File.WriteAllTextAsync(_path, LargeStateFile("a@example.com"), TestContext.Current.CancellationToken);
        DateTime originalWrite = File.GetLastWriteTimeUtc(_path);
        int hookCalls = 0;
        ClaudeStateFile stateFile = new(_path, beforeReplace: async cancellationToken =>
        {
            if (hookCalls++ == 0)
            {
                string text = await File.ReadAllTextAsync(_path, cancellationToken);
                await File.WriteAllTextAsync(_path, text.Replace("\"numStartups\": 412", "\"numStartups\": 413", StringComparison.Ordinal), cancellationToken);
                File.SetLastWriteTimeUtc(_path, originalWrite);
            }
        });

        await stateFile.PatchAccountBlockAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        JsonNode result = JsonNode.Parse(await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken))!;
        result["numStartups"]!.GetValue<int>().ShouldBe(413);
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
        hookCalls.ShouldBe(2);
        Directory.GetFiles(_directory).ShouldBe([_path]);
    }

    [Fact]
    public async Task PatchCreatesAMissingFileWithOnlyTheAccountBlock()
    {
        File.Exists(_path).ShouldBeFalse();

        await new ClaudeStateFile(_path).PatchAccountBlockAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        JsonObject result = JsonNode.Parse(await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken))!.AsObject();
        result.Count.ShouldBe(1);
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
        result.ContainsKey("hasCompletedOnboarding").ShouldBeFalse();
        Directory.GetFiles(_directory).ShouldBe([_path]);
    }

    [Fact]
    public async Task PatchRecordingOnboardingCreatesAMissingFileWithOnlyTheAccountAndTheFlag()
    {
        File.Exists(_path).ShouldBeFalse();

        await new ClaudeStateFile(_path).PatchAccountBlockAndOnboardingAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        string text = await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken);
        JsonObject result = JsonNode.Parse(text)!.AsObject();
        result.Count.ShouldBe(2);
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
        result["hasCompletedOnboarding"]!.GetValue<bool>().ShouldBeTrue();
        result.ContainsKey("lastOnboardingVersion").ShouldBeFalse();
        text.Contains("lastOnboardingVersion", StringComparison.Ordinal).ShouldBeFalse();
        Directory.GetFiles(_directory).ShouldBe([_path]);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"true\"")]
    [InlineData("null")]
    [InlineData("0")]
    public async Task PatchRecordingOnboardingReplacesAFlagThatIsNotBooleanTrue(string literal)
    {
        const string anchor = "\"lastOnboardingVersion\": \"2.1.278\",\n  \"oauthAccount\": ";
        string original = "{\n  \"hasCompletedOnboarding\": " + literal + ",\n  " + anchor + "{\"emailAddress\": \"a@example.com\"}\n}\n";
        await File.WriteAllTextAsync(_path, original, TestContext.Current.CancellationToken);

        await new ClaudeStateFile(_path).PatchAccountBlockAndOnboardingAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        string patched = await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken);
        patched.StartsWith("{\n  \"hasCompletedOnboarding\": true,\n  ", StringComparison.Ordinal).ShouldBeTrue();
        patched.Contains(anchor, StringComparison.Ordinal).ShouldBeTrue();
        int flag = patched.IndexOf("hasCompletedOnboarding", StringComparison.Ordinal);
        patched.IndexOf("hasCompletedOnboarding", flag + 1, StringComparison.Ordinal).ShouldBe(-1);
        JsonObject result = JsonNode.Parse(patched)!.AsObject();
        result["hasCompletedOnboarding"]!.GetValue<bool>().ShouldBeTrue();
        result["lastOnboardingVersion"]!.GetValue<string>().ShouldBe("2.1.278");
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
    }

    [Fact]
    public async Task PatchRecordingOnboardingRetriesBeforeDecidingTheFlag()
    {
        // The first read has no flag, so a decision taken only then would append a
        // second hasCompletedOnboarding onto the file the session writes in between.
        await File.WriteAllTextAsync(_path, "{\n  \"numStartups\": 1\n}\n", TestContext.Current.CancellationToken);
        int hookCalls = 0;
        ClaudeStateFile stateFile = new(_path, beforeReplace: async cancellationToken =>
        {
            if (hookCalls++ == 0)
            {
                await File.WriteAllTextAsync(
                    _path,
                    "{\n  \"numStartups\": 2,\n  \"hasCompletedOnboarding\": true,\n  \"lastOnboardingVersion\": \"9.9.9\"\n}\n",
                    cancellationToken);
            }
        });

        await stateFile.PatchAccountBlockAndOnboardingAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        string patched = await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken);
        const string preserved = "\"hasCompletedOnboarding\": true,\n  \"lastOnboardingVersion\": \"9.9.9\"";
        patched.Contains(preserved, StringComparison.Ordinal).ShouldBeTrue();
        int flag = patched.IndexOf("hasCompletedOnboarding", StringComparison.Ordinal);
        patched.IndexOf("hasCompletedOnboarding", flag + 1, StringComparison.Ordinal).ShouldBe(-1);
        JsonObject result = JsonNode.Parse(patched)!.AsObject();
        result["numStartups"]!.GetValue<int>().ShouldBe(2);
        result["hasCompletedOnboarding"]!.GetValue<bool>().ShouldBeTrue();
        result["lastOnboardingVersion"]!.GetValue<string>().ShouldBe("9.9.9");
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
        hookCalls.ShouldBe(2);
        Directory.GetFiles(_directory).ShouldBe([_path]);
    }

    [Theory]
    [InlineData("{\"oauthAccount\": {\"emailAddress\": \"a@example.com\"},}")]
    [InlineData("{\"oauthAccount\": {\"emailAddress\": \"a@example.com\"}, }")]
    [InlineData("{\n  \"oauthAccount\": {\"emailAddress\": \"a@example.com\"},\n}\n")]
    public async Task PatchRecordingOnboardingReusesARootTrailingComma(string original)
    {
        // The reader allows a comma between the last value and the root brace.
        // Inserting another one there writes ", ," and the file no longer parses.
        await File.WriteAllTextAsync(_path, original, TestContext.Current.CancellationToken);

        await new ClaudeStateFile(_path).PatchAccountBlockAndOnboardingAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        string patched = await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken);
        patched.Contains(",,", StringComparison.Ordinal).ShouldBeFalse();
        JsonObject result = JsonNode.Parse(patched)!.AsObject();
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
        result["hasCompletedOnboarding"]!.GetValue<bool>().ShouldBeTrue();
        result.ContainsKey("lastOnboardingVersion").ShouldBeFalse();
    }

    [Fact]
    public async Task PatchRecordingOnboardingAppendsBothPropertiesAfterARootTrailingComma()
    {
        await File.WriteAllTextAsync(_path, "{\n  \"numStartups\": 1,\n}\n", TestContext.Current.CancellationToken);

        await new ClaudeStateFile(_path).PatchAccountBlockAndOnboardingAsync(Account("b@example.com"), TestContext.Current.CancellationToken);

        string patched = await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken);
        patched.Contains(",,", StringComparison.Ordinal).ShouldBeFalse();
        JsonObject result = JsonNode.Parse(patched)!.AsObject();
        result["numStartups"]!.GetValue<int>().ShouldBe(1);
        result["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
        result["hasCompletedOnboarding"]!.GetValue<bool>().ShouldBeTrue();
        result.ContainsKey("lastOnboardingVersion").ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
