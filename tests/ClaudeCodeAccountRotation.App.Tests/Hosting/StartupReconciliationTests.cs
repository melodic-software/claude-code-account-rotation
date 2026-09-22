using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// Startup puts back any credential pair a refresh rotated and could not write.
/// The sweep runs before the first request and must never be able to stop the
/// tool: an operator locked out of the page by the very file that explains the
/// problem has no way back in.
/// <para>
/// Startup also loads the usage cache, which is what makes the first dashboard
/// read after a restart show numbers at all.
/// </para>
/// </summary>
public sealed class StartupReconciliationTests
{
    [Fact]
    public async Task AMalformedRecoveryFileDoesNotStopTheAppAndBecomesAWarning()
    {
        await using AppFactory factory = new();
        string recovery = Path.Combine(factory.AppData, "recovery");
        Directory.CreateDirectory(recovery);
        await File.WriteAllTextAsync(
            Path.Combine(recovery, "a@example.com.credentials.json"),
            "{ this is not a credential envelope",
            TestContext.Current.CancellationToken);

        using HttpClient client = factory.CreateClient();
        HttpResponseMessage health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        health.IsSuccessStatusCode.ShouldBeTrue();
        factory.Services.GetRequiredService<QuotaState>().RecoveryWarnings.ShouldNotBeEmpty();
        // Moved aside rather than deleted: an unreadable file may still be the
        // only copy of something, so it is kept where the operator can find it.
        File.Exists(Path.Combine(recovery, "a@example.com.credentials.json")).ShouldBeFalse();
        Directory.GetFiles(Path.Combine(recovery, "stale")).Length.ShouldBe(1);
    }

    [Fact]
    public async Task ATornParkedCredentialFileDoesNotStopTheAppAndIsLeftInPlace()
    {
        // Startup reconciliation reads every parked pair. A torn file used to
        // throw out of StartAsync and the host never bound. The file is not a
        // duplicate that can be proved, so it is not quarantined either.
        await using AppFactory factory = new();
        string folder = await factory.ParkedProfileAsync("a@example.com", "refresh-a", TestContext.Current.CancellationToken);
        string credentials = Path.Combine(folder, CredentialFiles.FileName);
        await File.WriteAllTextAsync(credentials, "{", TestContext.Current.CancellationToken);

        using HttpClient client = factory.CreateClient();
        HttpResponseMessage health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        health.IsSuccessStatusCode.ShouldBeTrue();
        (await File.ReadAllTextAsync(credentials, TestContext.Current.CancellationToken)).ShouldBe("{");
        factory.Logs.Lines.ShouldContain(line => line.Contains("unreadable credential file", StringComparison.Ordinal));
        string quarantine = Path.Combine(factory.AppData, "quarantine");
        if (Directory.Exists(quarantine))
        {
            Directory.GetFiles(quarantine, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task AHeldMutationGateDoesNotStopStartup()
    {
        await using AppFactory factory = new();
        StrongBox<IDisposable?> permit = new();
        factory.Overrides = services =>
        {
            services.Replace(ServiceDescriptor.Singleton(_ =>
            {
                CredentialMutationGate gate = new();
                permit.Value = gate.AcquireAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
                return gate;
            }));
        };

        try
        {
            using HttpClient client = factory.CreateClient();
            HttpResponseMessage health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

            health.IsSuccessStatusCode.ShouldBeTrue();
            factory.Logs.Lines.ShouldContain(line => line.Contains("startup reconciliation did not run", StringComparison.Ordinal));
        }
        finally
        {
            permit.Value?.Dispose();
        }
    }

    [Fact]
    public async Task ACacheFilePresentAtStartPopulatesTheCardAsCached()
    {
        // The operator restarts the tool to install a build. Without this the
        // first dashboard read after a restart shows ten unknown cards and only a
        // fresh pass, which costs the rate window, brings the numbers back.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync("live@example.com", TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(Parked, "refresh-a", TestContext.Current.CancellationToken);
        DateTimeOffset captured = factory.Clock.GetUtcNow().AddHours(-2);
        string state = Path.Combine(factory.AppData, "state");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(
            Path.Combine(state, "usage-cache.json"),
            // The window this figure belongs to has not reset yet, so the card
            // keeps the percentage instead of blanking the row.
            Cached(captured, factory.Clock.GetUtcNow().AddHours(3)).ToJsonString(),
            TestContext.Current.CancellationToken);

        using HttpClient client = factory.CreateClient();
        JsonElement dashboard = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/dashboard", UriKind.Relative),
            TestContext.Current.CancellationToken);

        JsonElement card = dashboard.GetProperty("accounts").EnumerateArray()
            .Single(account => account.GetProperty("email").GetString() == Parked);
        JsonElement usage = card.GetProperty("usage");
        usage.GetProperty("source").GetString().ShouldBe("cached");
        // The instant the numbers were read, not the instant they were loaded.
        usage.GetProperty("capturedAt").GetDateTimeOffset().ShouldBe(captured);
        usage.GetProperty("limits")[0].GetProperty("percent").GetDouble().ShouldBe(43);
    }

    private const string Parked = "a@example.com";

    private static JsonObject Cached(DateTimeOffset capturedAt, DateTimeOffset resetsAt) => new()
    {
        [Parked] = new JsonObject
        {
            ["capturedAt"] = capturedAt.ToString("o", CultureInfo.InvariantCulture),
            ["source"] = "OnDemandRefresh",
            ["limits"] = new JsonArray(new JsonObject
            {
                ["rawKind"] = "session",
                ["kind"] = "Session",
                ["percent"] = 43,
                ["resetsAt"] = resetsAt.ToString("o", CultureInfo.InvariantCulture),
                ["isActive"] = true,
            }),
            ["extraUsage"] = null,
        },
    };
}
