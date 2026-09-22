using System.Net;
using System.Text;
using ClaudeCodeAccountRotation.App.Adapters.Peers;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class FollowerLoopbackTokenHandlerTests
{
    [Fact]
    public async Task ARefusedCallReadsTheTokenAgainOnceAndTheTokenIsNotLogged()
    {
        RecordingLogger<FollowerLoopbackTokenHandler> logger = new();
        ScriptedReader reader = new("token-a", "token-b");
        RecordingInner inner = new(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using HttpClient client = Client(reader, logger, inner);
        using HttpRequestMessage request = new(HttpMethod.Post, "http://127.0.0.1/api/import")
        {
            Content = new StringContent("{\"email\":\"a@example.com\"}", Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string logged = string.Join('\n', logger.Lines);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        reader.Reads.ShouldBe(2);
        inner.Authorizations.ShouldBe(["token-a", "token-b"]);
        inner.Bodies.ShouldBe(["{\"email\":\"a@example.com\"}", "{\"email\":\"a@example.com\"}"]);
        logged.ShouldContain("read the follower instance token again after it was refused");
        logged.ShouldNotContain("token-a");
        logged.ShouldNotContain("token-b");
    }

    [Fact]
    public async Task ACachedTokenIsNotReadAgainForASecondCall()
    {
        RecordingLogger<FollowerLoopbackTokenHandler> logger = new();
        ScriptedReader reader = new("token-a", "token-b");
        RecordingInner inner = new(HttpStatusCode.OK, HttpStatusCode.OK);
        using HttpClient client = Client(reader, logger, inner);

        using HttpResponseMessage first = await client.GetAsync(new Uri("http://127.0.0.1/api/dashboard"), TestContext.Current.CancellationToken);
        using HttpResponseMessage second = await client.GetAsync(new Uri("http://127.0.0.1/api/dashboard"), TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        reader.Reads.ShouldBe(1);
        inner.Authorizations.ShouldBe(["token-a", "token-a"]);
    }

    [Fact]
    public async Task AMissingLaunchFailsClosedWithoutCallingTheFollower()
    {
        WslFollowerInstanceTokenSource source = new(launch: null);
        Result<string, string> read = await source.ReadAsync(TestContext.Current.CancellationToken);
        RecordingLogger<FollowerLoopbackTokenHandler> logger = new();
        RecordingInner inner = new(HttpStatusCode.OK);
        using HttpClient client = Client(new FailingReader(), logger, inner);

        using HttpResponseMessage response = await client.GetAsync(new Uri("http://127.0.0.1/api/dashboard"), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        read.IsFailure.ShouldBeTrue();
        read.Error.ShouldBe(WslFollowerInstanceTokenSource.UnavailableReason);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.ShouldBe("{\"error\":\"" + WslFollowerInstanceTokenSource.UnavailableReason + "\"}");
        body.ShouldNotContain("instance.url");
        inner.Calls.ShouldBe(0);
        string.Join('\n', logger.Lines).ShouldNotContain("token-a");
    }

    [Fact]
    public void TheCatCommandIsAnArgumentListAndLinuxPathsStaySlashSeparated()
    {
        const string path = "/var/lib/follower/instance.url";

        IReadOnlyList<string> cat = WslFollowerInstanceTokenSource.CatArguments("Some-Distribution", "someone", path);
        IReadOnlyList<string> printenv = WslFollowerInstanceTokenSource.PrintEnvArguments("Some-Distribution", "someone", "HOME");

        cat.ShouldBe(["-d", "Some-Distribution", "-u", "someone", "--exec", "cat", "--", path]);
        printenv.ShouldBe(["-d", "Some-Distribution", "-u", "someone", "--exec", "printenv", "--", "HOME"]);
        string.Join(' ', cat).ShouldNotContain("\\");
        WslFollowerInstanceTokenSource.InstanceUrlPath("/var/lib/follower").ShouldBe("/var/lib/follower/instance.url");
        WslFollowerInstanceTokenSource.InstanceUrlPath("/var/lib/follower/").ShouldBe("/var/lib/follower/instance.url");
        WslFollowerInstanceTokenSource.DefaultAppDataDirectory("/xdg", "/opt/follower-home")
            .ShouldBe("/xdg/claude-code-account-rotation");
        WslFollowerInstanceTokenSource.DefaultAppDataDirectory("relative", "/opt/follower-home")
            .ShouldBe("/opt/follower-home/.local/share/claude-code-account-rotation");
        WslFollowerInstanceTokenSource.DefaultAppDataDirectory(null, "/opt/follower-home")
            .ShouldBe("/opt/follower-home/.local/share/claude-code-account-rotation");
    }

    [Fact]
    public void AppDataDirectoryIsReadFromTheFollowerConfiguration()
    {
        WslFollowerInstanceTokenSource.TryReadAppDataDirectory("{\"appDataDirectory\":\"/var/lib/follower\"}", out string? named).ShouldBeTrue();
        named.ShouldBe("/var/lib/follower");
        WslFollowerInstanceTokenSource.TryReadAppDataDirectory("{\"appDataDirectory\":\"/var/lib/app\\u0026data\"}", out string? decoded).ShouldBeTrue();
        decoded.ShouldBe("/var/lib/app&data");
        WslFollowerInstanceTokenSource.TryReadAppDataDirectory("{\"role\":\"follower\"}", out string? absent).ShouldBeTrue();
        absent.ShouldBeNull();
        WslFollowerInstanceTokenSource.TryReadAppDataDirectory("{\"appDataDirectory\":\"  \"}", out string? blank).ShouldBeTrue();
        blank.ShouldBeNull();
        WslFollowerInstanceTokenSource.TryReadAppDataDirectory("{", out string? malformed).ShouldBeFalse();
        malformed.ShouldBeNull();
        WslFollowerInstanceTokenSource.SecondLine("http://127.0.0.1:48212\ntoken-line\n").ShouldBe("token-line");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client disposes the handler, and the handler disposes the inner handler.")]
    private static HttpClient Client(IFollowerInstanceTokenReader reader, RecordingLogger<FollowerLoopbackTokenHandler> logger, HttpMessageHandler inner)
    {
        FollowerLoopbackTokenHandler handler = new(reader, logger) { InnerHandler = inner };
        return new HttpClient(handler);
    }

    private sealed class ScriptedReader(params string[] tokens) : IFollowerInstanceTokenReader
    {
        private readonly Queue<string> _tokens = new(tokens);

        public int Reads { get; private set; }

        public Task<Result<string, string>> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Result<string, string>.Success(_tokens.Dequeue()));
        }
    }

    private sealed class FailingReader : IFollowerInstanceTokenReader
    {
        public Task<Result<string, string>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<string, string>.Failure(WslFollowerInstanceTokenSource.UnavailableReason));
    }

    private sealed class RecordingInner(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);

        public int Calls { get; private set; }

        public List<string?> Authorizations { get; } = [];

        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorizations.Add(request.Headers.Authorization?.Parameter);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(_statuses.Dequeue());
        }
    }
}
