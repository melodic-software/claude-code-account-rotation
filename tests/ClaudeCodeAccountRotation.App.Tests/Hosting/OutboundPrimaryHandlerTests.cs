using System.Net.Http;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using ClaudeCodeAccountRotation.App.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// The primary handler is what stops a redirect from replaying a refresh-token
/// POST. A scripted 307 on the client proves the client sends one request; this
/// proves the handler the composition root actually installs refuses redirects
/// and keeps the factory's two-minute pooled connection lifetime.
/// </summary>
public sealed class OutboundPrimaryHandlerTests
{
    [Fact]
    public void EachCallReturnsANewHandlerThatRefusesRedirects()
    {
        using SocketsHttpHandler first = OutboundPrimaryHandler.Create();
        using SocketsHttpHandler second = OutboundPrimaryHandler.Create();

        first.AllowAutoRedirect.ShouldBeFalse();
        second.AllowAutoRedirect.ShouldBeFalse();
        first.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
        second.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
        ReferenceEquals(first, second).ShouldBeFalse();
    }

    [Fact]
    public void BothOutboundClientsInstallThatHandler()
    {
        CapturingFilter captured = new();
        ServiceCollection services = new();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(captured);
        AppComposition.AddOutboundClients(services, "claude-code-account-rotation/1.2.3 (+https://example.invalid)");

        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        using HttpClient usage = factory.CreateClient(nameof(AnthropicUsageEndpointClient));
        using HttpClient tokens = factory.CreateClient(nameof(ClaudeOAuthTokenRefreshClient));

        captured.Handlers.Count.ShouldBe(2);
        captured.Handlers.Select(handler => handler.GetType()).ShouldAllBe(type => type == typeof(SocketsHttpHandler));
        SocketsHttpHandler[] sockets = captured.Handlers.Cast<SocketsHttpHandler>().ToArray();
        sockets[0].AllowAutoRedirect.ShouldBeFalse();
        sockets[1].AllowAutoRedirect.ShouldBeFalse();
        sockets[0].PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
        sockets[1].PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
        ReferenceEquals(sockets[0], sockets[1]).ShouldBeFalse();
        usage.ShouldNotBeNull();
        tokens.ShouldNotBeNull();
    }

    private sealed class CapturingFilter : IHttpMessageHandlerBuilderFilter
    {
        public List<HttpMessageHandler> Handlers { get; } = [];

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);
            return builder =>
            {
                next(builder);
                Handlers.Add(builder.PrimaryHandler);
            };
        }
    }
}
