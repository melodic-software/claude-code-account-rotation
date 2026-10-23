using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Tests.Identity;

public sealed class SignInUrlTests
{
    private const string Authorize = "https://claude.com/cai/oauth/authorize?code=true&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e&response_type=code"
        + "&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&scope=user%3Ainference&code_challenge=abc&code_challenge_method=S256&state=xyz";

    private static readonly AccountEmail _email = AccountEmail.Parse("dev+ci@example.com").Value;

    [Fact]
    public void TheHintIsAddedLastWithTheAddressEscapedAndTheCliParametersUntouched()
    {
        Uri hinted = SignInUrl.WithLoginHint(new Uri(Authorize), _email);

        hinted.AbsoluteUri.ShouldBe(Authorize + "&login_hint=dev%2Bci%40example.com");
    }

    [Fact]
    public void AHintTheCliAlreadySetIsKept()
    {
        Uri url = new(Authorize + "&login_hint=other%40example.com");

        SignInUrl.WithLoginHint(url, _email).ShouldBe(url);
    }

    [Fact]
    public void AUrlWithNoQueryGetsTheHintAsItsQuery() =>
        SignInUrl.WithLoginHint(new Uri("https://claude.com/cai/oauth/authorize"), _email).AbsoluteUri
            .ShouldBe("https://claude.com/cai/oauth/authorize?login_hint=dev%2Bci%40example.com");
}
