namespace ClaudeCodeAccountRotation.Core.Identity;

/// <summary>The authorize URL a sign-in opens.</summary>
public static class SignInUrl
{
    /// <summary>
    /// <paramref name="url"/> with <c>login_hint</c> naming <paramref name="email"/>,
    /// so a signed-out browser shows that address in the email box. A hint the
    /// CLI already set is kept. Basis: the owner's check of a setup-token URL
    /// with the hint in a signed-out window, 2026-10-23 (the address was filled in).
    /// </summary>
    public static Uri WithLoginHint(Uri url, AccountEmail email)
    {
        ArgumentNullException.ThrowIfNull(url);
        string query = url.Query.TrimStart('?');
        if (query.Split('&').Any(static pair => pair.StartsWith("login_hint=", StringComparison.Ordinal)))
        {
            return url;
        }

        string hint = "login_hint=" + Uri.EscapeDataString(email.Value);
        return new UriBuilder(url) { Query = query.Length == 0 ? hint : query + "&" + hint }.Uri;
    }
}
