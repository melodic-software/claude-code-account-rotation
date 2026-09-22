using System.Net;
using System.Reflection;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using ClaudeCodeAccountRotation.App.Adapters.Peers;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Endpoints;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>
/// The composition root: configuration, validation, the single-instance lock,
/// every service with its explicit type, Kestrel on loopback, and the routes.
/// </summary>
internal static class AppComposition
{
    private const string ConfigPathSettingKey = "ClaudeCodeAccountRotation:ConfigPath";
    private const string ConfigFileName = "config.json";
    private static readonly TimeSpan _cliTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The host's simple console formatter prints no timestamp when
    /// <see cref="Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions.TimestampFormat"/>
    /// is left null, which is its default. A trailing space keeps the stamp
    /// off the level word. UTC, so two machines do not have to share a zone
    /// before a line can be ordered.
    /// </summary>
    internal const string ConsoleTimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";

    /// <summary>
    /// Selects the simple formatter and stamps every console line. Setting the
    /// formatter name is required: while it is null the provider copies the
    /// deprecated console options, whose timestamp is also null, over the
    /// simple formatter's own options.
    /// </summary>
    internal static void ConfigureConsoleTimestamp(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.AddSimpleConsole(static options =>
        {
            options.TimestampFormat = ConsoleTimestampFormat;
            options.UseUtcTimestamp = true;
        });
    }

    public static string Version =>
        typeof(AppComposition).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppComposition).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static async Task<Result<Unit, string>> ComposeAsync(WebApplicationBuilder builder, StartupArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(arguments);
        ConfigureConsoleTimestamp(builder.Logging);

        ClaudeCodeAccountRotationConfiguration defaults = ConfigurationDefaults.ForCurrentUser();
        string configPath = arguments.ConfigPath
            ?? builder.Configuration[ConfigPathSettingKey]
            ?? Path.Combine(defaults.AppDataDirectory, ConfigFileName);
        string fullConfigPath = Path.GetFullPath(configPath);
        bool created = !File.Exists(fullConfigPath);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(configPath, defaults, cancellationToken);
        if (loaded.IsFailure)
        {
            return Result<Unit, string>.Failure(loaded.Error);
        }

        if (created)
        {
            await Console.Out.WriteLineAsync("Created configuration at " + fullConfigPath);
        }

        // Port 0 is this launch only: the file keeps a real listenPort, which is
        // what validation checks, and the socket asks the operating system for one.
        ClaudeCodeAccountRotationConfiguration configuration = arguments.Port is int port && port != 0
            ? loaded.Value with { ListenPort = port }
            : loaded.Value;
        Result<Unit, string> verdict = ConfigurationValidator.Validate(
            configuration,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ConfigurationValidator.VolumeOf,
            Environment.GetEnvironmentVariable);
        if (verdict.IsFailure)
        {
            return Result<Unit, string>.Failure("configuration refused (" + configPath + "): " + verdict.Error);
        }

        int bindPort = arguments.Port == 0 ? 0 : configuration.ListenPort;
        string listenUrl = "http://127.0.0.1:" + bindPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Result<InstanceLock, string> instance = InstanceLock.TryAcquire(configuration.AppDataDirectory, listenUrl);
        if (instance.IsFailure)
        {
            return Result<Unit, string>.Failure(instance.Error);
        }

        IServiceCollection services = builder.Services;
        // Factory-registered so the container owns it and releases the lock file at shutdown.
        InstanceLock acquired = instance.Value;
        services.AddSingleton(_ => acquired);
        services.AddSingleton(configuration);
        services.AddSingleton(new LoopbackOrigins(configuration.ListenPort));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SwitchOptions(
            configuration.LiveConfigDirectory,
            configuration.StateFilePath,
            configuration.ProfilesRoot,
            configuration.AppDataDirectory,
            configuration.RefreshLockWaitBound,
            MutationGateTimeout: TimeSpan.Zero,
            configuration.Mailbox,
            Environment.GetEnvironmentVariable(CrashInjection.EnvironmentVariableName),
            // Whitespace is off, not on. A caller that sets the variable to an
            // empty string means "no injection", and a launcher that exports it
            // unconditionally is the ordinary way to write one; reading that as
            // "on" turns every later run into a corrupted export.
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CrashInjection.CorruptExportEnvironmentVariableName))));
        // A pass is a credential operation, not a request: the account in flight
        // must be allowed to finish or its rotated pair is stranded. The worst
        // case is one gated refresh unit — a 2 s wait for the mutation gate, a
        // 20 s token POST, three write attempts each wrapping AtomicBytesFile's
        // 2 s transient retry, and 1.75 s of pacing between them — which is about
        // 30 s. The framework's default 30 s would cut that unit off at its worst
        // moment, so the drain is given 45 s. Set before the role split so the
        // follower gets the same drain: its import releases the refresh lock from
        // Dispose when the container shuts down.
        services.Configure<Microsoft.Extensions.Hosting.HostOptions>(
            static options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
        if (configuration.Role == RotationRole.Follower)
        {
            ComposeFollower(services, configuration);
            ListenOn(builder, bindPort);
            services.Configure<HostFilteringOptions>(static options => options.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"]);
            return Result<Unit, string>.Success(Unit.Value);
        }

        // Registered as itself as well as behind the port: the coordinator needs
        // the mailbox operations, which are the leader's alone and deliberately
        // not on ICredentialPairStore, since a follower has no store to claim in.
        services.AddSingleton(new FileSystemCredentialPairStore(configuration.LiveConfigDirectory, configuration.ProfilesRoot, TimeProvider.System));
        services.AddSingleton<ICredentialPairStore>(static provider => provider.GetRequiredService<FileSystemCredentialPairStore>());
        services.AddSingleton(new ClaudeStateFile(configuration.StateFilePath));
        services.AddSingleton(provider => new ProfileFolderStore(
            configuration.ProfilesRoot,
            provider.GetRequiredService<ILogger<ProfileFolderStore>>()));
        services.AddSingleton(new RateLimitGuardTeeFileReader(configuration.StatuslineTeePath));
        // Through the factory so the container owns the gate this file disposes.
        services.AddSingleton(_ => new RosterFile(configuration.AppDataDirectory));
        services.AddSingleton<IBrowserLauncher>(new ChromiumFamilyBrowserLauncher(configuration.BrowserExecutables));
        services.AddSingleton<IBrowserProfileReader>(new ChromiumLocalStateProfileReader());

        AddOutboundClients(services, AnthropicEndpoints.UserAgent(configuration.UserAgentProductToken, Version));
        services.AddSingleton(new SwitchJournal(configuration.AppDataDirectory));
        services.AddSingleton<CredentialMutationGate>();
        services.AddSingleton(provider => new SharedStoreSlots(
            configuration.ProfilesRoot,
            configuration.SharedStore,
            provider.GetRequiredService<CredentialMutationGate>(),
            provider.GetRequiredService<ILoginSessionRunner>(),
            provider.GetRequiredService<ILogger<SharedStoreSlots>>()));
        services.AddSingleton(ManagedLoginPolicyReader.ForCurrentMachine());
        Result<ClaudeExecutable, string> cli = ClaudeExecutableLocator.Locate(
            configuration.ClaudeExecutable,
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows(),
            Environment.SystemDirectory);
        AddCli(services, cli);
        // The login child factory: a real process, or a start that reports why no
        // CLI could be resolved rather than throwing at the first login.
        LoginChildFactory loginChild = cli.IsSuccess
            ? ProcessLoginChild.Factory(cli.Value)
            : (_, _) => Result<ILoginChild, string>.Failure(cli.Error);
        services.AddSingleton<ILoginSessionRunner>(provider => new ClaudeCliLoginSessionRunner(
            loginChild,
            provider.GetRequiredService<ProfileFolderStore>(),
            provider.GetRequiredService<ClaudeStateFile>(),
            provider.GetRequiredService<CredentialMutationGate>(),
            provider.GetRequiredService<IClaudeCliAuthStatus>(),
            provider.GetRequiredService<IClaudeCliLogout>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ClaudeCliLoginSessionRunner>>()));
        services.AddSingleton<LiveDirectorySwitch>();
        ComposePeers(services, configuration);
        services.AddSingleton(new WslSwitchJournal(configuration.AppDataDirectory));
        services.AddSingleton<WslSwitch>();
        services.AddSingleton<DashboardState>();
        services.AddSingleton<DashboardAssembler>();
        AddRefresh(services);
        services.AddHostedService<InstanceLockHolder>();
        services.AddHostedService<StartupReconciliation>();
        services.AddHostedService<StateFileWatcher>();
        // The worker is registered twice over one instance because the refresh
        // routes call TryStart on it: AddHostedService alone registers it as an
        // IHostedService and nothing else, so the route could not resolve the very
        // object holding the channel its 202 writes to.
        services.AddSingleton<QuotaRefreshWorker>();
        services.AddHostedService(static provider => provider.GetRequiredService<QuotaRefreshWorker>());
        // No checks registered: liveness only. MapRoutes maps the /healthz route this serves.
        services.AddHealthChecks();

        // Loopback only: the page is a local control surface, never a network service.
        ListenOn(builder, bindPort);
        // The framework's own host filter, ahead of everything of ours: a request
        // carrying a rebound name is refused before the pipeline reaches a route.
        // Set here rather than left to configuration, whose default is "*".
        services.Configure<HostFilteringOptions>(static options => options.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"]);
        return Result<Unit, string>.Success(Unit.Value);
    }

    /// <summary>
    /// Prints the dashboard URL once Kestrel has bound, and opens nothing.
    /// Port 0 has no number until then; the instance file is rewritten with
    /// the address that was actually taken.
    /// </summary>
    public static void AnnounceDashboard(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            string? url = LoopbackDashboardUrl(app);
            if (url is null)
            {
                return;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? bound) && bound.Port > 0)
            {
                // Before the URL is printed, so a page opened from that line posts
                // to the port that was bound, including a launch that asked for 0.
                app.Services.GetRequiredService<LoopbackOrigins>().UseBoundPort(bound.Port);
            }

            app.Services.GetRequiredService<InstanceLock>().PublishListenUrl(url);
            // Printed and not opened. The operator chooses when to visit the page.
            Console.Out.WriteLine("Dashboard: " + url);
        });
    }

    public static void MapRoutes(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<LoopbackHostMiddleware>();

        // The framework's liveness probe with no checks registered: the body is the
        // status word and the middleware writes the no-store cache headers itself.
        app.MapHealthChecks("/healthz");

        // The follower serves four import routes, the shutdown route, and a dashboard
        // the leader reads, and nothing else. The roster, login, switch and refresh
        // routes are not mapped at all, so R1's "the WSL side exposes no add, no login,
        // no roster write" is a 404 from the router rather than a check inside a
        // handler that a later edit could forget.
        if (app.Services.GetRequiredService<ClaudeCodeAccountRotationConfiguration>().Role == RotationRole.Follower)
        {
            ImportEndpoints.Map(app);
            ShutdownEndpoints.Map(app);
            return;
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new EmbeddedFileProvider(typeof(AppComposition).Assembly, "ClaudeCodeAccountRotation.App.wwwroot"),
        });
        app.MapGet("/", static () => Results.Content(EmbeddedPage.IndexHtml, "text/html; charset=utf-8"));
        DashboardEndpoints.Map(app);
        SwitchEndpoints.Map(app);
        SideEndpoints.Map(app);
        RosterEndpoints.Map(app);
        RefreshEndpoints.Map(app);
        LoginEndpoints.Map(app);
        ShutdownEndpoints.Map(app);
    }

    /// <summary>
    /// Loopback only. Port 0 is a dynamic bind, which <c>ListenLocalhost</c> refuses,
    /// so that launch takes one IPv4 loopback socket and the printed URL is its port.
    /// </summary>
    private static void ListenOn(WebApplicationBuilder builder, int port)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (port == 0)
            {
                kestrel.Listen(IPAddress.Loopback, 0);
                return;
            }

            kestrel.ListenLocalhost(port);
        });
    }

    private static string? LoopbackDashboardUrl(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature? addresses = server.Features.Get<IServerAddressesFeature>();
        if (addresses is null)
        {
            return null;
        }

        foreach (string address in addresses.Addresses)
        {
            if (address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
            {
                return address;
            }
        }

        return null;
    }

    /// <summary>
    /// The other sides of this machine, from <c>peers[]</c>. An empty list
    /// registers an empty registry: the coordinator resolves, refuses every
    /// side as offline, and the page shows no side — which is this lane's
    /// whole rollback.
    /// </summary>
    private static void ComposePeers(IServiceCollection services, ClaudeCodeAccountRotationConfiguration configuration)
    {
        foreach (PeerConfiguration peer in configuration.Peers ?? [])
        {
            // The follower refuses to start until its mailbox exists, and the
            // only other creator is the first claim — which cannot happen until
            // a follower is running. Creating it here is what the follower's own
            // refusal already promises ("the leader creates it in the store
            // under .transit/"), and an empty directory holds nothing, so a side
            // removed from peers[] leaves only that behind.
            Directory.CreateDirectory(FileSystemCredentialPairStore.MailboxPath(configuration.ProfilesRoot, peer.Side));
            services.AddHttpClient(PeerClientName(peer.Side), client =>
            {
                client.BaseAddress = peer.BaseAddress;
                // Above the coordinator's own 60 s import bound, so the bound
                // that fires is the one whose failure the crash table is
                // written for rather than the client's generic one.
                client.Timeout = WslSwitch.ImportTimeout + TimeSpan.FromSeconds(30);
            });
        }

        services.AddSingleton(provider => new PeerRegistry(
        [
            .. (configuration.Peers ?? []).Select(peer => new Peer(
                new HttpPeerRotationInstance(
                    peer.Side,
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(PeerClientName(peer.Side))),
                peer.Launch is null
                    ? null
                    : new WslDistributionPeerHost(peer.Side, peer.Launch, provider.GetRequiredService<ILogger<WslDistributionPeerHost>>()),
                peer.StorePathFromPeer)),
        ]));
    }

    private static string PeerClientName(SideName side) => "peer-" + side.Value;

    /// <summary>
    /// The follower's container: a live directory, a journal, and the staged
    /// import. No profiles root, no roster, no login runner, no refresh engine,
    /// no browser — the WSL side holds one live pair and takes another when the
    /// leader hands it one.
    /// </summary>
    private static void ComposeFollower(IServiceCollection services, ClaudeCodeAccountRotationConfiguration configuration)
    {
        services.AddSingleton(new ClaudeStateFile(configuration.StateFilePath));
        services.AddSingleton<CredentialMutationGate>();
        // The follower reads its own tee and nothing else about usage: it makes
        // no usage request of its own, and this file is what the leader's card
        // for an account this side holds is built from (design 12).
        services.AddSingleton(new RateLimitGuardTeeFileReader(configuration.StatuslineTeePath));
        services.AddSingleton(new ImportJournal(configuration.AppDataDirectory));
        services.AddSingleton(provider => new StagedImportCredentialPairStore(
            configuration.LiveConfigDirectory,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new ImportReconciler(
            provider.GetRequiredService<SwitchOptions>(),
            provider.GetRequiredService<StagedImportCredentialPairStore>(),
            provider.GetRequiredService<ImportJournal>(),
            provider.GetRequiredService<ClaudeStateFile>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ImportReconciler>>()));
        services.AddSingleton(provider => new FollowerImport(
            provider.GetRequiredService<SwitchOptions>(),
            provider.GetRequiredService<StagedImportCredentialPairStore>(),
            provider.GetRequiredService<ImportJournal>(),
            provider.GetRequiredService<ImportReconciler>(),
            provider.GetRequiredService<ClaudeStateFile>(),
            provider.GetRequiredService<CredentialMutationGate>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<FollowerImport>>()));
        services.AddHostedService<InstanceLockHolder>();
        services.AddHealthChecks();
    }

    /// <summary>
    /// The two outbound calls the tool ever makes, both on demand and never on a
    /// timer, each through the factory so no captive HttpClient outlives DNS.
    /// The factory's logging handler names every header at Trace and redacts
    /// every value unless told otherwise, which is what keeps a bearer token out
    /// of a log file. Naming headers to redact would *narrow* that default, so
    /// this deliberately names none. Each client gets its own primary handler
    /// from <see cref="OutboundPrimaryHandler"/>, which refuses redirects so a
    /// 307 or 308 cannot replay a refresh-token POST. Registered here rather
    /// than inline so a test exercises the same wiring the app runs.
    /// </summary>
    internal static void AddOutboundClients(IServiceCollection services, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The clock comes from the container, not from TimeProvider.System: a
        // Retry-After given as an absolute date is turned into a wait against it,
        // and a test that moves the clock must be able to move that too. TryAdd,
        // so the composition root's own registration (or a test's replacement of
        // it) stands and a caller wiring only these two clients still gets a clock.
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(nameof(AnthropicUsageEndpointClient))
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundPrimaryHandler.Create())
            .AddTypedClient<IUsageEndpointClient>((http, provider) => new AnthropicUsageEndpointClient(http, userAgent, provider.GetRequiredService<TimeProvider>()));
        services.AddHttpClient(nameof(ClaudeOAuthTokenRefreshClient))
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundPrimaryHandler.Create())
            .AddTypedClient<ITokenRefreshClient>((http, provider) => new ClaudeOAuthTokenRefreshClient(http, userAgent, provider.GetRequiredService<TimeProvider>()));
    }

    /// <summary>
    /// The refresh engine and everything it needs that is not already registered.
    /// <para>
    /// Two of these are delegates rather than the services themselves. The typed
    /// clients are transient (a singleton capturing one would hold a handler past
    /// its rotation), so the engine resolves a fresh one per call through a
    /// factory. The pacing delegate exists because <c>TestClock</c> overrides only
    /// <c>GetUtcNow</c>: a <c>Task.Delay</c> driven by a <see cref="TimeProvider"/>
    /// would really sleep in a test, so production hands the engine the real delay
    /// and a test hands it a recorder.
    /// </para>
    /// </summary>
    private static void AddRefresh(IServiceCollection services)
    {
        services.AddSingleton<QuotaState>();
        services.AddSingleton<UsageSnapshotCache>();
        services.AddSingleton<RecoveryFiles>();
        services.AddSingleton(provider => new RefreshBudget(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Func<IUsageEndpointClient>>(static provider => provider.GetRequiredService<IUsageEndpointClient>);
        services.AddSingleton<Func<ITokenRefreshClient>>(static provider => provider.GetRequiredService<ITokenRefreshClient>);
        services.AddSingleton<Func<TimeSpan, CancellationToken, Task>>(static provider =>
        {
            TimeProvider timeProvider = provider.GetRequiredService<TimeProvider>();
            return (delay, cancellationToken) => Task.Delay(delay, timeProvider, cancellationToken);
        });
        services.AddSingleton<QuotaRefresh>();
    }

    /// <summary>
    /// The one CLI process adapter, registered under both of the ports it serves.
    /// Through the container rather than by hand, because the adapter needs a
    /// logger: what a failing child printed goes there and never into the
    /// failure string the page renders.
    /// </summary>
    private static void AddCli(IServiceCollection services, Result<ClaudeExecutable, string> located)
    {
        if (located.IsFailure)
        {
            UnavailableClaudeCli unavailable = new(located.Error);
            services.AddSingleton<IClaudeCliAuthStatus>(unavailable);
            services.AddSingleton<IClaudeCliLogout>(unavailable);
            return;
        }

        ClaudeExecutable executable = located.Value;
        services.AddSingleton(provider => new ClaudeCliProcessAuthStatus(
            executable,
            _cliTimeout,
            provider.GetRequiredService<ILogger<ClaudeCliProcessAuthStatus>>()));
        services.AddSingleton<IClaudeCliAuthStatus>(static provider => provider.GetRequiredService<ClaudeCliProcessAuthStatus>());
        services.AddSingleton<IClaudeCliLogout>(static provider => provider.GetRequiredService<ClaudeCliProcessAuthStatus>());
    }

    /// <summary>Stands in when no CLI could be resolved: every call reports why.</summary>
    private sealed class UnavailableClaudeCli(string reason) : IClaudeCliAuthStatus, IClaudeCliLogout
    {
        public Task<Result<ClaudeAuthStatus, string>> ReadAsync(string? configDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(Result<ClaudeAuthStatus, string>.Failure(reason));

        public Task<Result<Unit, string>> LogoutAsync(string configDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Unit, string>.Failure(reason));
    }
}
