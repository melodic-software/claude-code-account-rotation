using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

public sealed class CiTokenSecretTests
{
    // The names issue #259 keeps off any one repository: a role's token and key
    // secrets and the bare legacy token, compared without case as GitHub does.
    [Theory]
    [InlineData("REVIEWS_CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("INTAKE_CLAUDE_API_KEY")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("claude_code_oauth_token")]
    [InlineData("pr_verify_claude_api_key")]
    public void AClaudeRoleSecretInOneRepositoryIsRefused(string name)
    {
        CiTokenSecret.Parse(name, "octo/repo", null).Value.IsRepositoryRoleSecret.ShouldBeTrue();
        CiTokenSecret.Parse(name, null, "octo").Value.IsRepositoryRoleSecret.ShouldBeFalse();
    }

    [Theory]
    [InlineData("LANE_TOKEN")]
    [InlineData("XCLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN_OLD")]
    public void AnyOtherRepositorySecretIsNotARoleSecret(string name) =>
        CiTokenSecret.Parse(name, "octo/repo", null).Value.IsRepositoryRoleSecret.ShouldBeFalse();
}
