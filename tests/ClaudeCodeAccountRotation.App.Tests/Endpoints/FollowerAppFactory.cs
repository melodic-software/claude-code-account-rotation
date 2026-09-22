using System.Net.Http.Headers;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.App.Tests.Switching;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// The follower hosted in process over <see cref="FollowerRoots"/>: the same
/// composition the WSL side runs, so what this asserts about routes is what
/// the router really maps and not a second wiring written for a test.
/// </summary>
internal sealed class FollowerAppFactory : WebApplicationFactory<Program>
{
    public FollowerAppFactory()
    {
        ConfigPath = ImportCrashInjectionTests.WriteFollowerConfig(Roots);
    }

    public FollowerRoots Roots { get; } = new();

    public string ConfigPath { get; }

    public AppFactory.LogSink Logs { get; } = new();

    public HttpClient CreateMutatingClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add(SameOriginMutationFilter.HeaderName, "1");
        return client;
    }

    /// <summary>A client with the instance bearer removed, for a refusal.</summary>
    public HttpClient CreateAnonymousClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Services.GetRequiredService<InstanceLock>().Token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ClaudeCodeAccountRotation:ConfigPath", ConfigPath);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Roots.Clock)));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Roots.Dispose();
    }
}
