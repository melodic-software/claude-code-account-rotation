using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.Core;
using Microsoft.AspNetCore.Builder;

Result<StartupArguments, string> parsed = StartupArguments.Parse(args);
if (parsed.IsFailure)
{
    await Console.Error.WriteLineAsync(parsed.Error);
    return 2;
}

if (parsed.Value.ShowHelp)
{
    await Console.Out.WriteLineAsync(StartupArguments.Usage);
    return 0;
}

if (parsed.Value.ShowVersion)
{
    await Console.Out.WriteLineAsync(AppComposition.Version);
    return 0;
}

// The content root is pinned to the binary's own directory rather than left as
// the working directory the launcher happened to have. The host watches the
// content root for a reloadable appsettings.json, and this tool is started by
// other things: a logon shortcut whose working directory is the system folder,
// and a leader that spawns the follower through wsl.exe, which inherits the
// caller's. Started from a home directory on a 9P mount that watch took the
// whole startup with it, in uninterruptible I/O, before a line was logged.
// Nothing here reads appsettings.json; the configuration is the JSON file
// --config names, and the page's assets are embedded in the assembly.
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
Result<Unit, string> composed = await AppComposition.ComposeAsync(builder, parsed.Value, CancellationToken.None);
if (composed.IsFailure)
{
    await Console.Error.WriteLineAsync(composed.Error);
    return 1;
}

WebApplication app = builder.Build();
AppComposition.MapRoutes(app);
AppComposition.AnnounceDashboard(app);
await app.RunAsync();
return 0;
