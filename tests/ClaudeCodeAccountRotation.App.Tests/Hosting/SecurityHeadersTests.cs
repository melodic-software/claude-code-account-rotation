namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// The page and its static assets are same-origin. The policy names that, and
/// every response carries it, including the script and the stylesheet the
/// document loads.
/// </summary>
public sealed class SecurityHeadersTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/app.css")]
    public async Task ThePageAndItsAssetsCarryTheContentSecurityPolicyAndNosniff(string path)
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        Header(response, "Content-Security-Policy").ShouldBe("default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            return values.Single();
        }

        return response.Content.Headers.TryGetValues(name, out IEnumerable<string>? content)
            ? content.Single()
            : throw new InvalidOperationException("missing header " + name);
    }
}
