using System.Net.Http;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// Replaces the socket under both outbound clients with one scripted handler.
/// Named configuration runs after <c>ConfigureHttpClientDefaults</c>, and the
/// composition root now installs its own primary handler, so a default-only
/// replacement would lose. The handler that loses is disposed here; it has not
/// opened a socket. One scripted instance is shared by both clients, or a pass
/// could not say what order it sent its requests in.
/// </summary>
internal static class OutboundHandlerStub
{
    public static void Install(IServiceCollection services, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handler);
        Replace(services, nameof(AnthropicUsageEndpointClient), handler);
        Replace(services, nameof(ClaudeOAuthTokenRefreshClient), handler);
    }

    private static void Replace(IServiceCollection services, string name, HttpMessageHandler handler) =>
        services.Configure<HttpClientFactoryOptions>(name, options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
            {
                HttpMessageHandler previous = builder.PrimaryHandler;
                if (!ReferenceEquals(previous, handler))
                {
                    previous.Dispose();
                }

                builder.PrimaryHandler = handler;
            }));
}
