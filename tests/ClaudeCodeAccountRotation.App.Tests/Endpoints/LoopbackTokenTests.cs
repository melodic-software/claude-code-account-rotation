using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.App.Switching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

public sealed class LoopbackTokenTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAnonymousDashboardIsUnauthorizedAndMatchesAWrongToken()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync("seeded@example.com", Token);
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;
        using HttpClient missing = factory.CreateAnonymousClient();
        using HttpClient wrong = factory.CreateAnonymousClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        using HttpResponseMessage missingResponse = await missing.GetAsync(new Uri("/api/dashboard", UriKind.Relative), Token);
        using HttpResponseMessage wrongResponse = await wrong.GetAsync(new Uri("/api/dashboard", UriKind.Relative), Token);
        byte[] missingBody = await missingResponse.Content.ReadAsByteArrayAsync(Token);
        byte[] wrongBody = await wrongResponse.Content.ReadAsByteArrayAsync(Token);

        missingResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrongResponse.StatusCode.ShouldBe(missingResponse.StatusCode);
        Challenge(missingResponse).ShouldBe(Challenge(wrongResponse));
        Challenge(missingResponse).ShouldBe("Bearer");
        missingBody.ShouldBe(wrongBody);
        Encoding.UTF8.GetString(missingBody).ShouldBe("{\"error\":\"unauthorized\"}");
        Encoding.UTF8.GetString(missingBody).ShouldNotContain("seeded@example.com");
        Encoding.UTF8.GetString(missingBody).ShouldNotContain(token);
    }

    [Fact]
    public async Task AnAnonymousSwitchDoesNotMoveTheLivePairAndAWrongTokenDoesNotEither()
    {
        await using AppFactory factory = await LiveAsync();
        string before = await LiveFingerprintAsync(factory);
        using HttpClient anonymous = factory.CreateAnonymousClient();
        anonymous.DefaultRequestHeaders.Add(SameOriginMutationFilter.HeaderName, "1");
        using HttpClient wrong = factory.CreateAnonymousClient();
        wrong.DefaultRequestHeaders.Add(SameOriginMutationFilter.HeaderName, "1");
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        using HttpResponseMessage anonymousResponse = await anonymous.PostAsync(SwitchUri(), content: null, Token);
        using HttpResponseMessage wrongResponse = await wrong.PostAsync(SwitchUri(), content: null, Token);

        anonymousResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrongResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymousResponse.Content.ReadAsByteArrayAsync(Token)).ShouldBe(await wrongResponse.Content.ReadAsByteArrayAsync(Token));
        (await LiveFingerprintAsync(factory)).ShouldBe(before);
    }

    [Fact]
    public async Task AValidTokenWithoutTheMutationHeaderIsForbiddenAndDoesNotMoveTheLivePair()
    {
        await using AppFactory factory = await LiveAsync();
        string before = await LiveFingerprintAsync(factory);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri(), content: null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LiveFingerprintAsync(factory)).ShouldBe(before);
    }

    [Fact]
    public async Task AnAnonymousShutdownIsUnauthorizedAndHealthzStillAnswers()
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(SameOriginMutationFilter.HeaderName, "1");

        using HttpResponseMessage shutdown = await client.PostAsync(new Uri("/api/shutdown", UriKind.Relative), content: null, Token);
        using HttpResponseMessage health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Token);

        shutdown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ATokenInTheQueryStringIsNotACredential()
    {
        await using AppFactory factory = new();
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;
        using HttpClient client = factory.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/dashboard?token=" + Uri.EscapeDataString(token), UriKind.Relative),
            Token);
        string body = await response.Content.ReadAsStringAsync(Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        body.ShouldBe("{\"error\":\"unauthorized\"}");
        body.ShouldNotContain(token);
    }

    [Fact]
    public async Task TheDocumentWithoutACredentialContainsNeitherTheTokenNorASeededEmail()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync("seeded@example.com", Token);
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;
        using HttpClient client = factory.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative), Token);
        string html = await response.Content.ReadAsStringAsync(Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<meta name=\"ccar-instance-token\" content=\"\">");
        html.ShouldNotContain(token);
        html.ShouldNotContain("seeded@example.com");
    }

    [Fact]
    public async Task TheDocumentWithTheBearerEmbedsTheTokenOnceAndDoesNotStoreIt()
    {
        await using AppFactory factory = new();
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;
        using HttpClient client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative), Token);
        string html = await response.Content.ReadAsStringAsync(Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl.NoStore.ShouldBeTrue();
        CountOf(html, token).ShouldBe(1);
        html.ShouldContain("<meta name=\"ccar-instance-token\" content=\"" + token + "\">");
        string setCookie = string.Join('\n', response.Headers.GetValues("Set-Cookie"));
        string lowered = setCookie.ToLowerInvariant();
        lowered.ShouldContain("ccar-instance=");
        lowered.ShouldContain("httponly");
        lowered.ShouldContain("samesite=strict");
        lowered.ShouldContain("path=/");
        lowered.ShouldNotContain("domain=");
        lowered.ShouldNotContain("max-age");
        lowered.ShouldNotContain("expires=");
        setCookie.Split(';').Select(static part => part.Trim()).ShouldNotContain(static part => part.Equals("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AStaleCookieDoesNotBlockTheDocumentAndIsCleared()
    {
        await using AppFactory factory = new();
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;
        using HttpResponseMessage response = await factory.Server.CreateRequest("/")
            .AddHeader("Cookie", LoopbackTokenMiddleware.CookieName + "=stale-cookie-value")
            .GetAsync();
        string html = await response.Content.ReadAsStringAsync(Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<meta name=\"ccar-instance-token\" content=\"\">");
        html.ShouldNotContain(token);
        html.ShouldNotContain("stale-cookie-value");
        string setCookie = string.Join('\n', response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        setCookie.ShouldContain("ccar-instance=");
        setCookie.ShouldContain("expires=");
    }

    [Fact]
    public async Task TheBearerAndTheCookieMustBothMatch()
    {
        await using AppFactory factory = new();
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;
        using HttpResponseMessage response = await factory.Server.CreateRequest("/")
            .AddHeader("Authorization", "Bearer " + token)
            .AddHeader("Cookie", LoopbackTokenMiddleware.CookieName + "=stale-cookie-value")
            .GetAsync();
        string html = await response.Content.ReadAsStringAsync(Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldNotContain(token);
        html.ShouldContain("content=\"\"");
    }

    [Fact]
    public async Task AMatchingCookieEmbedsTheToken()
    {
        await using AppFactory factory = new();
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;
        using HttpResponseMessage response = await factory.Server.CreateRequest("/")
            .AddHeader("Cookie", LoopbackTokenMiddleware.CookieName + "=" + token)
            .GetAsync();
        string html = await response.Content.ReadAsStringAsync(Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        CountOf(html, token).ShouldBe(1);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task TheOpenAssetsAnswerWithoutATokenAndTheScriptDoesNotCarryIt()
    {
        await using AppFactory factory = new();
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;
        using HttpClient client = factory.CreateAnonymousClient();

        using HttpResponseMessage script = await client.GetAsync(new Uri("/app.js", UriKind.Relative), Token);
        using HttpResponseMessage style = await client.GetAsync(new Uri("/app.css", UriKind.Relative), Token);
        using HttpResponseMessage health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Token);
        string scriptBody = await script.Content.ReadAsStringAsync(Token);

        script.StatusCode.ShouldBe(HttpStatusCode.OK);
        style.StatusCode.ShouldBe(HttpStatusCode.OK);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        scriptBody.ShouldNotContain(token);
        scriptBody.ShouldContain("ccar-instance-token");
        scriptBody.ShouldContain("Authorization");
    }

    [Fact]
    public async Task ARefusedRequestDoesNotLogTheToken()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync("seeded@example.com", Token);
        string token = factory.Services.GetRequiredService<InstanceLock>().Token;

        using HttpResponseMessage response = await factory.Server.CreateRequest("/api/dashboard")
            .AddHeader("Authorization", "Bearer " + token)
            .AddHeader("Cookie", LoopbackTokenMiddleware.CookieName + "=stale-cookie-value")
            .GetAsync();
        string logs = string.Join('\n', factory.Logs.Lines);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        logs.ShouldNotContain(token);
        logs.ShouldNotContain("stale-cookie-value");
        logs.ShouldNotContain("seeded@example.com");
    }

    private static Uri SwitchUri() => new("/api/accounts/b@example.com/switch", UriKind.Relative);

    private static async Task<AppFactory> LiveAsync()
    {
        AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", Token);
        return factory;
    }

    private static async Task<string> LiveFingerprintAsync(AppFactory factory)
    {
        RefreshTokenFingerprint? fingerprint = await CredentialFiles.FingerprintAsync(factory.LiveDirectory, Token);
        fingerprint.HasValue.ShouldBeTrue();
        return fingerprint.Value.Sha256Hex;
    }

    private static string Challenge(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("WWW-Authenticate", out IEnumerable<string>? values).ShouldBeTrue();
        return string.Join(',', values!);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
