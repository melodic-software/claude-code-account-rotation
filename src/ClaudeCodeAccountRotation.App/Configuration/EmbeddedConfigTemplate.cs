using System.Reflection;

namespace ClaudeCodeAccountRotation.App.Configuration;

/// <summary>The shipped <c>config.template.json</c>, read once from the executable's resources.</summary>
internal static class EmbeddedConfigTemplate
{
    private const string ResourceName = "ClaudeCodeAccountRotation.App.config.template.json";

    private static readonly Lazy<string> _json = new(Load);

    public static string Json => _json.Value;

    private static string Load()
    {
        Assembly assembly = typeof(EmbeddedConfigTemplate).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded configuration template " + ResourceName + " is missing from the executable.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
