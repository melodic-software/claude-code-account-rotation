using System.Collections.Concurrent;
using System.Net;
using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// The HTTP factory's logging handler names every header at Trace and redacts
/// every value unless the registration names headers to keep, so a bearer token
/// cannot reach a log file. This pins that: an access token in a log outlives
/// the token itself. The User-Agent assertion is the load-bearing one, because
/// it is the assertion that fails the moment someone "adds redaction" by naming
/// headers, which narrows the default rather than widening it.
/// </summary>
public sealed class OutboundClientLoggingTests
{
    private const string AccessToken = "an-access-token-that-must-never-be-logged";
    private const string UserAgent = "claude-code-account-rotation/1.2.3 (+https://example.invalid)";

    [Fact]
    public async Task NoHeaderValueReachesALogLine()
    {
        using CapturingLoggerProvider logs = new();
        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });
        AppComposition.AddOutboundClients(services, UserAgent);
        // Nothing reaches the network: the factory's own handlers stay, only the
        // socket at the bottom of the chain is replaced. Named, because the
        // composition root's own primary handler runs after a defaults registration.
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, """{"limits":[]}"""));
        OutboundHandlerStub.Install(services, handler);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IUsageEndpointClient client = provider.GetRequiredService<IUsageEndpointClient>();

        (await client.ReadUsageAsync(AccessToken, TestContext.Current.CancellationToken)).Value.Dispose();

        // The handler must have logged headers at all, or the rest asserts nothing.
        logs.Lines.ShouldContain(line => line.Contains("Authorization", StringComparison.Ordinal));
        logs.Lines.ShouldAllBe(line => !line.Contains(AccessToken, StringComparison.Ordinal));
        // Every value, not just the obvious one: naming headers to redact would
        // narrow the framework default and let this one through.
        logs.Lines.ShouldAllBe(line => !line.Contains(UserAgent, StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentBag<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                lines.Add(formatter(state, exception));
            }
        }
    }
}
