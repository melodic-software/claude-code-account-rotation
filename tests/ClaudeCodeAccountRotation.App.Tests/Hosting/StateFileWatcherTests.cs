using System.Net;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

public sealed class StateFileWatcherTests
{
    private static async Task<string?> StateFileEmailAsync(string path, CancellationToken cancellationToken)
    {
        // The watcher replaces this file atomically while the test reads it, so the read goes
        // through SharedFileReader like every other read of a file Claude Code also writes;
        // File.ReadAllTextAsync opens with FileShare.Read and loses that race.
        var node = JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(path, cancellationToken));
        return node?["oauthAccount"]?["emailAddress"]?.GetValue<string>();
    }

    [Fact]
    public async Task AStaleBlockWrittenBackAfterASwitchIsRepairedWithoutAnyRequest()
    {
        using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync("b@example.com", "refresh-b", TestContext.Current.CancellationToken);
        factory.Cli.Email = "b@example.com";
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage response = await client.PostAsync(new Uri("/api/accounts/b%40example.com/switch", UriKind.Relative), content: null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StateFileEmailAsync(factory.StateFilePath, TestContext.Current.CancellationToken)).ShouldBe("b@example.com");

        // A session writes its in-memory block back; no dashboard read follows.
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);

        string? email = null;
        for (int attempt = 0; attempt < 40 && email != "b@example.com"; attempt++)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            email = await StateFileEmailAsync(factory.StateFilePath, TestContext.Current.CancellationToken);
        }

        email.ShouldBe("b@example.com", "the watcher repairs the block within seconds of the write-back");
    }

    [Fact]
    public async Task AStateFileReadWhileItIsBeingRewrittenLeavesTheWatcherRunning()
    {
        using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync("b@example.com", "refresh-b", TestContext.Current.CancellationToken);
        factory.Cli.Email = "b@example.com";
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage response = await client.PostAsync(new Uri("/api/accounts/b%40example.com/switch", UriKind.Relative), content: null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Claude Code truncates the file before it rewrites it, so the debounced read
        // can land on zero bytes.
        await File.WriteAllBytesAsync(factory.StateFilePath, [], TestContext.Current.CancellationToken);
        for (int attempt = 0; attempt < 40 && !factory.Logs.Lines.Any(Warned); attempt++)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        factory.Logs.Lines.Any(Warned).ShouldBeTrue("the torn read is logged rather than taken as a reason to stop");

        // Still watching: the next write-back is repaired like any other.
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        string? email = null;
        for (int attempt = 0; attempt < 40 && email != "b@example.com"; attempt++)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            email = await StateFileEmailAsync(factory.StateFilePath, TestContext.Current.CancellationToken);
        }

        email.ShouldBe("b@example.com", "the watcher survived the torn read and repaired the next write-back");
    }

    private static bool Warned(string line) => line.Contains("stale identity repair failed", StringComparison.Ordinal);
}
