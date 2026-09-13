using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Tests;

/// <summary>
/// Hosts the app in-process over a temp layout (live directory, state file,
/// profiles root, app data) with the CLI port doubled, so no test ever touches
/// the machine's real Claude Code directories.
/// </summary>
internal sealed class AppFactory : WebApplicationFactory<Program>
{
    public AppFactory()
    {
        Root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
        LiveDirectory = Path.Combine(Root, "live");
        StateFilePath = Path.Combine(Root, ".claude.json");
        ProfilesRoot = Path.Combine(Root, "profiles");
        AppData = Path.Combine(Root, "appdata");
        Directory.CreateDirectory(LiveDirectory);
        Directory.CreateDirectory(ProfilesRoot);
        Directory.CreateDirectory(AppData);
        ConfigPath = Path.Combine(AppData, "config.json");
        JsonObject configuration = new()
        {
            ["liveConfigDirectory"] = LiveDirectory,
            ["stateFilePath"] = StateFilePath,
            ["profilesRoot"] = ProfilesRoot,
            ["appDataDirectory"] = AppData,
            ["refreshLockWaitSeconds"] = 0.3,
            ["claudeExecutable"] = null,
        };
        File.WriteAllText(ConfigPath, configuration.ToJsonString());
    }

    public string Root { get; }

    public string LiveDirectory { get; }

    public string StateFilePath { get; }

    public string ProfilesRoot { get; }

    public string AppData { get; }

    public string ConfigPath { get; }

    public CannedCli Cli { get; } = new();

    /// <summary>Moved by hand, so the ten-minute login expiry is asserted without a sleep.</summary>
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The scripted <c>claude auth login</c> every login test drives.</summary>
    public LoginChildScript LoginChild { get; } = new();

    public BrowserRecorder Browser { get; } = new();

    /// <summary>
    /// What the machine's browsers are said to publish. Doubled like every other
    /// out-of-process port: the real reader would read this developer's own
    /// <c>Local State</c>, which is neither this machine's business to expose nor
    /// something a test can assert against.
    /// </summary>
    public CannedBrowserProfiles BrowserProfiles { get; } = new();

    /// <summary>Every log line the host wrote, so a test can assert what never reaches one.</summary>
    public LogSink Logs { get; } = new();

    /// <summary>
    /// The socket at the bottom of every outbound handler chain. Empty by
    /// default, so an unexpected outbound call throws rather than reaching the
    /// network or quietly answering: this is the assertion that no test in this
    /// repository talks to Anthropic. A test scripts it with
    /// <see cref="Adapters.RecordingHandler.Enqueue"/> after the host is built,
    /// and both named clients share the one queue, so a pass's token POST and its
    /// usage GET are recorded in the order they were actually sent.
    /// </summary>
    public RecordingHandler Outbound { get; } = new();

    /// <summary>
    /// Every delay the refresh engine asked for, in order, and none of them
    /// taken. The engine paces itself with an injected delegate rather than
    /// <c>Task.Delay</c> precisely so this can exist: <see cref="TestClock"/>
    /// overrides only <c>GetUtcNow</c>, so a delay driven by the container's
    /// <see cref="TimeProvider"/> would really sleep and a pass over ten accounts
    /// would take ten seconds of wall clock per test.
    /// </summary>
    public Pacer Waits { get; } = new();

    public static JsonObject AccountJson(string email) => new() { ["accountUuid"] = "uuid-" + email, ["emailAddress"] = email };

    public async Task WriteStateFileAsync(string email, CancellationToken cancellationToken)
    {
        JsonObject state = new() { ["numStartups"] = 3, ["oauthAccount"] = AccountJson(email) };
        await File.WriteAllTextAsync(StateFilePath, state.ToJsonString(), cancellationToken);
    }

    public async Task<string> ParkedProfileAsync(string email, string refreshToken, CancellationToken cancellationToken, DateTimeOffset? loginExpiresAt = null)
    {
        string folder = Path.Combine(ProfilesRoot, email);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, CredentialFiles.FileName),
            CredentialFiles.Shape(refreshToken, loginExpiresAt: loginExpiresAt).ToJsonString(),
            cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "profile.json"), AccountJson(email).ToJsonString(), cancellationToken);
        return folder;
    }

    public HttpClient CreateMutatingClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Claude-Code-Account-Rotation", "1");
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ClaudeCodeAccountRotation:ConfigPath", ConfigPath);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            // Nothing reaches the network: the factory's own handler chain stays
            // and only the socket underneath it is replaced. The lambda captures
            // the one instance rather than minting one per client, or the two
            // named clients would each hold a script of their own and a test
            // could not say what order the pass sent its requests in.
            services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => Outbound));
            // The clock the login runner already reads, now for everything the
            // container hands a TimeProvider. A refresh pass decides whether an
            // access token has expired and how long a lockout still has to run
            // from it, and a test that could not move that clock would have to
            // sleep through both. Every instant a test compares against one the
            // host produced must now come from this clock too: a wall-clock
            // instant means nothing to a host frozen four days earlier.
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            // The pacing delegate, recorded rather than taken; see Waits.
            services.Replace(ServiceDescriptor.Singleton<Func<TimeSpan, CancellationToken, Task>>(Waits.Record));
            services.Replace(ServiceDescriptor.Singleton<IClaudeCliAuthStatus>(Cli));
            // Both ports, or a removal would resolve the real CLI on this machine.
            services.Replace(ServiceDescriptor.Singleton<IClaudeCliLogout>(Cli));
            services.Replace(ServiceDescriptor.Singleton<IBrowserLauncher>(Browser));
            services.Replace(ServiceDescriptor.Singleton<IBrowserProfileReader>(BrowserProfiles));
            // The real runner over a scripted child: the URL parsing, the retry, the
            // expiry, and the profile rewrite are the code under test, not doubles.
            services.Replace(ServiceDescriptor.Singleton<ILoginSessionRunner>(provider => new ClaudeCliLoginSessionRunner(
                LoginChild.Start,
                provider.GetRequiredService<ProfileFolderStore>(),
                provider.GetRequiredService<ClaudeStateFile>(),
                provider.GetRequiredService<CredentialMutationGate>(),
                provider.GetRequiredService<IClaudeCliAuthStatus>(),
                provider.GetRequiredService<IClaudeCliLogout>(),
                Clock)));
        });
    }

    // The base Dispose(bool) routes through DisposeAsync, so the host (and with it the
    // instance lock's delete-on-close handle) is only certainly gone once this returns.
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>The pacing delegate as a recorder: it answers at once and remembers what it was asked to wait.</summary>
    internal sealed class Pacer
    {
        private readonly ConcurrentQueue<TimeSpan> _requested = new();

        public IReadOnlyCollection<TimeSpan> Requested => _requested;

        public Task Record(TimeSpan delay, CancellationToken cancellationToken)
        {
            _requested.Enqueue(delay);
            return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
        }
    }

    /// <summary>Records what the login flow asked the browser to open, and with which profile.</summary>
    internal sealed class BrowserRecorder : IBrowserLauncher
    {
        public List<(BrowserFamily Browser, string? ProfileDirectory, Uri Url)> Launched { get; } = [];

        public string? Error { get; set; }

        public Result<Unit, string> Launch(BrowserFamily browser, string? profileDirectory, Uri signInUrl)
        {
            if (Error is string error)
            {
                return Result<Unit, string>.Failure(error);
            }

            Launched.Add((browser, profileDirectory, signInUrl));
            return Result<Unit, string>.Success(Unit.Value);
        }
    }

    /// <summary>The browser profiles a test says this machine publishes.</summary>
    internal sealed class CannedBrowserProfiles : IBrowserProfileReader
    {
        public List<BrowserProfile> Found { get; } = [];

        public Task<IReadOnlyList<BrowserProfile>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrowserProfile>>(Found);
    }

    /// <summary>Collects every formatted log message the host writes.</summary>
    internal sealed class LogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Collector(_lines);

        public void Dispose() => GC.SuppressFinalize(this);

        private sealed class Collector(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                lines.Enqueue(formatter(state, exception) + " " + exception);
            }
        }
    }

    internal sealed class CannedCli : IClaudeCliAuthStatus, IClaudeCliLogout
    {
        public string? Email { get; set; }

        /// <summary>What every folder reports; "max" is the only tier the roster admits.</summary>
        public string? SubscriptionType { get; set; } = "max";

        /// <summary>Set to make every status read fail, the way an unreadable or unparsable one does.</summary>
        public string? ReadError { get; set; }

        /// <summary>Set to make every status read throw, the way a fault in the CLI adapter would.</summary>
        public Exception? ReadFault { get; set; }

        /// <summary>Every config directory <c>auth logout</c> was run under, in order.</summary>
        public List<string> LogoutCalls { get; } = [];

        public string? LogoutError { get; set; }

        public Task<Result<ClaudeAuthStatus, string>> ReadAsync(string? configDirectory, CancellationToken cancellationToken) =>
            ReadFault is Exception fault
                ? Task.FromException<Result<ClaudeAuthStatus, string>>(fault)
                : Task.FromResult(ReadError is string error
                    ? Result<ClaudeAuthStatus, string>.Failure(error)
                    : Result<ClaudeAuthStatus, string>.Success(new ClaudeAuthStatus(true, Email, "claude.ai", "Personal", SubscriptionType, null)));

        public Task<Result<Unit, string>> LogoutAsync(string configDirectory, CancellationToken cancellationToken)
        {
            LogoutCalls.Add(configDirectory);
            return Task.FromResult(LogoutError is string error
                ? Result<Unit, string>.Failure(error)
                : Result<Unit, string>.Success(Unit.Value));
        }
    }
}
