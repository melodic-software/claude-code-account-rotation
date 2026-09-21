using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// A configured side's mailbox exists as soon as the leader is up. The
/// follower's own validator refuses to start without it, and the only other
/// creator is the first claim — which cannot happen until a follower is
/// running, so a fresh install had no way in.
/// </summary>
public sealed class PeerMailboxTests
{
    [Fact]
    public async Task AConfiguredSideGetsItsMailboxWhenTheLeaderStarts()
    {
        await using AppFactory factory = new(sharedStore: true, peerStorePath: "/mnt/c/store");
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        health.IsSuccessStatusCode.ShouldBeTrue();
        Directory.Exists(FileSystemCredentialPairStore.MailboxPath(factory.ProfilesRoot, SideName.Wsl)).ShouldBeTrue();
    }

    [Fact]
    public async Task NoMailboxIsMadeWhenNoSideIsConfigured()
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        health.IsSuccessStatusCode.ShouldBeTrue();
        Directory.Exists(Path.Combine(factory.ProfilesRoot, FileSystemCredentialPairStore.TransitDirectoryName)).ShouldBeFalse();
    }
}
