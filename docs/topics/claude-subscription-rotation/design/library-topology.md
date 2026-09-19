# Topology: claude-code-account-rotation

Light-form design, 2026-09-04. Repository `melodic-software/claude-code-account-rotation`, public, MIT.

## Solution layout

```text
claude-code-account-rotation/
  ClaudeCodeAccountRotation.slnx
  global.json                         # SDK 10.0.401, rollForward disable (org pin)
  Directory.Build.props               # imports eng/dotnet-analysis/Directory.Build.props; TargetFramework net10.0
  Directory.Packages.props            # central package versions
  eng/dotnet-analysis/                # synced from melodic-software/standards (dotnet-analysis component)
  src/
    ClaudeCodeAccountRotation.Core/             # domain, ports, pure operations; BCL only
      Identity/                       # AccountEmail, ProfileFolderName, OAuthAccountBlock, CredentialPair, ParkedProfile, LiveAccountState
      Switching/                      # SwitchPlanner, SwitchPlan, SwitchRefusal, SwitchOutcome
      Quota/                          # UsageLimit, UsageSnapshot, UsageResponseParser, StatuslineSnapshot, RefreshBudget, UsageReadFailure
      Routing/                        # RoutingPolicy, AccountStanding, AccountRanking, QueueCandidate, SwitchAdvisor, SwitchProposal
      Roster/                         # RosterEntry, Roster, BrowserKind
      Configuration/                  # ClaudeCodeAccountRotationConfiguration, RefreshBudgetSettings
      Ports/                          # the six interfaces (design thread T11)
      Result.cs
    ClaudeCodeAccountRotation.App/              # the executable
      Program.cs                      # composition root, Kestrel on 127.0.0.1, routes
      Endpoints/                      # one file per route group: Dashboard, Switch, Refresh, Roster, Login
      Dashboard/                      # DashboardAssembler, view records
      Switching/                      # LiveDirectorySwitch, SwitchJournal, CredentialMutationGate
      Quota/QuotaRefresh.cs
      InstanceLock.cs, ManagedLoginPolicy.cs
      Adapters/
        FileSystem/                   # FileSystemCredentialPairStore, ClaudeStateFile, ProfileFolderStore, RosterFile, UsageSnapshotCache, AdvisorState, RateLimitGuardTeeFileReader, RateLimitGuardStopEventsReader, AtomicJsonFile
        Http/                         # AnthropicUsageEndpointClient, ClaudeOAuthTokenRefreshClient
        Process/                      # ClaudeExecutableLocator, ClaudeCliProcessAuthStatus, ClaudeCliLoginSessionRunner
        Browser/                      # ChromiumFamilyBrowserLauncher
      Configuration/                  # ConfigurationDefaults, ConfigurationFile (validation: same volume, containment, live-dir cross-check)
      Security/SameOriginMutationFilter.cs  # plus Kestrel host filtering to loopback names
      wwwroot/                        # index.html, app.js, app.css (embedded resources, no build step)
      config.template.json            # shipped template; no literal paths
  tests/
    ClaudeCodeAccountRotation.Core.Tests/       # xunit v3 + MTP, Shouldly; fixtures/usage-response-*.json
    ClaudeCodeAccountRotation.App.Tests/        # adapter tests over temp dirs and fake handlers; endpoint tests via WebApplicationFactory
    acceptance/                       # scripted checklist for Brief criteria needing the real CLI, sessions, browser
  docs/
    topics/claude-subscription-rotation/   # this contract slice, moved in at Phase 0
  .github/workflows/                  # build-test on PR; publish single-file asset on tag
  README.md, LICENSE (MIT), CHANGELOG.md
```

## Dependency graph

```text
ClaudeCodeAccountRotation.App  ──►  ClaudeCodeAccountRotation.Core  ──►  (BCL only)
        │
        ├──► Microsoft.AspNetCore.App (framework reference: Kestrel, minimal API, static files)
        ├──► Microsoft.Extensions.Http (typed HttpClient via the factory; org overlay: no captive HttpClient)
        └──► System.Text.Json (in-box)

ClaudeCodeAccountRotation.Core.Tests ──► Core
ClaudeCodeAccountRotation.App.Tests  ──► App, Core, Microsoft.AspNetCore.Mvc.Testing
```

Core references no package. The App's only NuGet dependencies are the hosting and HTTP-factory
packages the org already pins in medley; test packages follow medley's pins (xunit.v3 on
Microsoft.Testing.Platform, Shouldly, NSubstitute). Dependency direction is the org's
`architecture-and-design.md` rule and is enforced by the project references alone; an ArchUnit test
is not worth adding for two projects.

## Adapter surfaces (infrastructure boundary)

| Port or class | Adapter | Machine surface |
|---|---|---|
| `ICredentialPairStore` | `FileSystemCredentialPairStore` | `<live>/.credentials.json`, `<profiles>/<folder>/.credentials.json`, `File.Move`, `AtomicJsonFile` |
| (concrete) | `ClaudeStateFile` | `~/.claude.json` or `<CLAUDE_CONFIG_DIR>/.claude.json` |
| (concrete) | `ProfileFolderStore` | `<profiles>/<folder>/profile.json`, folder lifecycle |
| (concrete) | `RosterFile`, `UsageSnapshotCache` | `<appdata>/roster.json`, `<appdata>/state/usage-cache.json` |
| (concrete) | `RateLimitGuardTeeFileReader` | `~/.claude/rate-limit-guard/rate-limits.json` |
| `IUsageEndpointClient` | `AnthropicUsageEndpointClient` | `GET https://api.anthropic.com/api/oauth/usage`, `anthropic-beta: oauth-2025-04-20`, own User-Agent |
| `ITokenRefreshClient` | `ClaudeOAuthTokenRefreshClient` | `POST https://platform.claude.com/v1/oauth/token`, own User-Agent |
| `IClaudeCliAuthStatus` | `ClaudeCliProcessAuthStatus` | `claude auth status --json` (optionally with `CLAUDE_CONFIG_DIR`) |
| `ILoginSessionRunner` | `ClaudeCliLoginSessionRunner` | `claude auth login --email <x>` under `CLAUDE_CONFIG_DIR=<folder>` (T8 mechanism a) or a visible terminal (b) |
| `IBrowserLauncher` | `ChromiumFamilyBrowserLauncher` | `<browser.exe> --profile-directory="<dir>" <url>` |

`AtomicJsonFile` is the one shared infrastructure helper: write to a temp file in the target's
directory, `Flush(true)`, then `File.Replace` (Windows) or `File.Move(overwrite: true)` elsewhere.
Every JSON write in the App goes through it.

## Configuration surface

`<appdata>/config.json` (created from `config.template.json` on first run) and `<appdata>/roster.json`,
where `<appdata>` is `%LOCALAPPDATA%\claude-code-account-rotation` on Windows and the XDG config directory
elsewhere. Command-line overrides: `--config <path>`, `--port <n>`, `--version`, `--help`. No
environment variable is read except `CLAUDE_CONFIG_DIR` (to find the live dir, matching the CLI) and
the platform's home and app-data variables.

## Cross-repo surface

`melodic-software/claude-code-plugins`, `plugins/rate-limit-guard/scripts/statusline-tee.sh` and
`reference/reader-contract.md`: the writer-side `account.email` field (design thread T14). This
tool reads it; it does not own it.
