---
outcome: early-exit-light
tier: B
date: 2026-09-14
---

# Design resolution: card-layout-and-login-age

Light design for #46 (card layout) and #49 (login age and refresh-token expiry), one chain and one
pull request. The types this work touches already exist: the parent record
(`docs/topics/claude-subscription-rotation/design/type-inventory.md`) specifies
`AccountStanding.LoginExpiresAt` and `CredentialPair.LoginExpiresAt`, and the #47 record
(`docs/topics/order-by-next-reset/design/design-resolution.md`) fixed `AvailabilityStanding`, the
key, `Arrange`, and the wire fields `standing` and `nextResetAt`. This work **fills a field the
parent already declared, adds two trailing wire fields, and reshapes the page**. No parent thread
is re-opened, and none of #47's three threads is re-opened.

Colour, typography and the visual language stay with #50. This record decides what each card
carries, in what order, and which facts cross the wire.

## What the code has that the issues do not know

Both issues were filed before #55, #56 and #47 landed. Re-read against `2b65a31`:

- The title is still one string: `alias (email)` in one `h2` (`app.js`, the render loop), with
  `word-break: break-all`. #46's first complaint stands.
- A badge row already exists (`div.badges`: `live`, `paused`, `needs login`, `not on roster`) with
  a reserved `min-height`, so "the state is nearly invisible" is now "the row shows some states and
  not the one that matters most": a card that is ready to switch to carries no badge at all.
- The quota block is filled (#56), and the standing line under it says `usable now` / `usable in …`
  / `no usage read yet` / `paused` (#47). Those stay as the quota facts; the chip this record adds
  is the credential fact, which is a different axis.
- `Remove` sits in `div.actions` beside `Switch` with class `danger`; the class colours it but does
  not separate it.
- `Log in again` is offered on every rostered, non-live card that holds credentials.
- The credential expiry is already read from disk: `FileSystemCredentialPairStore` parses
  `refreshTokenExpiresAt` into `CredentialPair.LoginExpiresAt`, the switch planner refuses
  `TargetLoginExpired` on it, and the refresh pass renews a paused login inside a seven-day window
  (`QuotaRefresh._loginRenewalWindow`). None of that reaches the payload: `DashboardAssembler`
  passes five arguments to `AccountStanding` and `LoginExpiresAt` defaults to null.
- The login moment is also already read: `ProfileFolderStore` parses `profileFetchedAt` into
  `OAuthAccountBlock.ProfileFetchedAt`, and `ParkedProfile.Account` carries it into the assembler.
- The render guard (#47) skips a non-forced render only when the arriving order differs from the
  rendered one and the pointer or focus is inside `#cards`. Card height does not enter it.
- No JS test framework exists; page behaviour is pinned by grep sanity checks and the App tests
  that assert the payload.

## Type sketch

Core changes nothing. Every change is in the App and the page.

```csharp
// Dashboard/DashboardViews.cs — two trailing fields, defaults so the two RosterEndpoints
// construction sites compile unchanged (the Standing/NextResetAt precedent).
internal sealed record AccountCardView(
    string Email,
    bool IsLive,
    bool HasCredentials,
    string? Folder,
    UsageView Usage,
    string? UsageNote,
    RefreshStateView Refresh,
    RosterEntryView? Roster = null,
    string Standing = "unread",
    DateTimeOffset? NextResetAt = null,
    DateTimeOffset? LoginExpiresAt = null,   // CredentialPair.LoginExpiresAt, the refresh token's life
    DateTimeOffset? LoggedInAt = null);      // OAuthAccountBlock.ProfileFetchedAt, when the login happened

// Dashboard/DashboardAssembler.cs — Card(...) gains the two instants; AccountStanding gets its
// sixth argument filled for the first time.
new AccountStanding(email, isLive, entry?.Paused ?? false, hasCredentials, merged?.Merged, loginExpiresAt)
```

The page's per-card state chip is a pure function of wire fields, in this precedence:

| chip | condition (first match wins) |
|---|---|
| `live` | `isLive` |
| `paused` | `roster.paused` |
| `needs login` | `!hasCredentials` |
| `login expired` | `loginExpiresAt <= dashboard.capturedAt` |
| `error` | `refresh.state === "stranded"` |
| `ready` | otherwise |

`not on roster` stays as a second, secondary badge: it is a roster fact, not a state.

## Threads

### T1 — the chip vocabulary against the shipped `standing` — RESOLVED

**Two axes, two surfaces.** `standing` (`usable` / `exhausted` / `unread` / `paused`) is the quota
axis and keeps its line under the usage rows exactly as #47 shipped it. The chip is the credential
axis: can the operator switch to this account at all. #46's "ready" is therefore not "usable"
renamed; a card can read `ready` with `usable in 2 h 14 min` under it (credentials fine, window
spent), and that is two true facts. The one overlap, `paused`, appears in both because #47 put it
in the standing and the chip row already shows it; the standing line is suppressed on a paused card
(the chip has said it) to avoid the same word twice. The chip replaces the conditional badge row
with an always-present one, so the card that was showing nothing (ready) now shows the fact the
operator most needs. `standing` and `nextResetAt` survive on the wire unchanged.

### T2 — an expired refresh token in the ordering and the switch gate — RESOLVED

**Flag, not a group.** `AccountAvailability.KeyFor` does not read `HasCredentials` today: a
needs-login account already sorts by its quota figures, because the key orders windows, not
credentials. An expired login is the same kind of fact as a missing one, so it stays out of the key
for the same reason, and #47's T2 (the total order) is not reopened. "Not presented as switchable"
is met on both sides that matter: the page disables `Switch` when `loginExpiresAt` has passed
(beside the existing `!hasCredentials` and `stranded` conditions), the chip says `login expired`,
the standing line is suppressed (a `usable now` under `login expired` would be a lie), and
`SwitchPlanner` already refuses `TargetLoginExpired` if the button is ever bypassed.

Consequence recorded for the parent's Phase 3.2: `Rank`'s filter must drop rows whose
`LoginExpiresAt <= now` as well as rows with `HasCredentials == false`, since `Arrange` still
orders both. That goes into the parent plan as a dated scope-change note under 3.2, the #47
precedent, and nowhere else.

### T3 — the wire form and where the assembler gets the instants — RESOLVED

**Two `DateTimeOffset?` trailing fields, `loginExpiresAt` and `loggedInAt`, filled from reads the
assembler already has or can make cheaply.** The live account's instant comes from the `livePair`
the assembler already reads; a parked account's from one `pairs.ReadParkedAsync(profile.FolderPath)`
per parked profile with credentials; a roster-only account has neither and sends null. The login
moment comes from `profile.Account?.ProfileFetchedAt` (parked) and `liveAccount?.ProfileFetchedAt`
(live), which are already in hand. No token content crosses: an instant is not a secret, and the
`DashboardView` doc comment's "no token field exists on any of these types" holds.

The read is per poll (ten small JSON files every ten seconds) and is the same call the refresh pass
and the switch already make; a torn or missing pair reads as null and the card says nothing rather
than failing the payload, matching how `HasCredentials` is file existence today. No formatting
happens server-side: `app.js` owns `relative()` measured from `dashboard.capturedAt` (the #47 T3
invariant), and the absolute date goes in the line's `title` attribute through the existing
`toLocaleString` idiom.

### T4 — sort or flag for the account nearest expiry — RESOLVED

**Flag.** A per-card `login-expiry` line and a warning class, no ordering change. The line reads
`login expires in 16 d` (through `relative()`, which already yields `in N d` past 48 hours), gains
the warning class inside seven days (the same window `QuotaRefresh` renews a paused login in, so the
page and the pass agree on what "soon" means), and reads `login expired` once past. When
`loggedInAt` is known the line leads with the age: `logged in 12 d ago · login expires in 16 d`.
"Findable without reading ten cards" is met by the warning class and the chip, which the eye picks
out of a grid faster than a position does; sorting on expiry would silently re-litigate #47's total
order and move a card that frees up next behind one that merely expires later.

### T5 — the card's order of facts and the action row — RESOLVED (page-only)

Per card, top to bottom, per #46's priority list:

1. `h2` the alias, or the address when there is no alias.
2. `p.address` the address, muted, only when an alias exists (no duplicate line otherwise).
3. the chip row (T1), always present.
4. the usage block, unchanged from #56 and #47 (rows, as-of, standing line, credits, note, refresh
   state), plus the `login-expiry` line (T4) at its foot.
5. `p.muted` the browser mapping, moved below the usage block as small print.
6. the action row, pinned to the card's bottom edge so rows of cards line up, with `Remove` in its
   own trailing group separated from the primary buttons.
7. the Edit panel, unchanged.

`Login` / `Log in again` is a primary action only when the card needs it: `!hasCredentials`,
`login expired`, or inside the seven-day warning window. On a healthy card it moves into the Edit
panel as a secondary button, so the operator can still force a fresh login without it inviting a
click on every card. Consistent card height: equal row heights in the grid and actions pinned to
the bottom; the title no longer wraps to three lines once the address is its own line. The render
guard needs no change: it keys on order, not height.

### T6 — test seams — RESOLVED

Existing seams only. `DashboardAssemblerTests` (App, temp directories, `factory.Clock`) pins the two
wire fields: a parked pair with `refreshTokenExpiresAt` yields `loginExpiresAt` on that card, a
profile with `profileFetchedAt` yields `loggedInAt`, a roster-only account yields null for both,
and the live card takes its instants from the live pair and state file. `RosterEndpointTests` proves
the two other construction sites still return null for both. The page has no test framework; its
behaviour is pinned by the plan's grep sanity checks (chip vocabulary, `innerHTML` count stays 2,
no `accounts.sort`) and a fresh-context verifier reading the diff.

## Not re-opened

No Core change, no new route, no persistence, no change to `Arrange`, `KeyFor`, the comparer, or
the refresh engine; no colour or typography (#50); no alias editing beyond what the Edit panel
already does (#48). The parent's 3.2 note is additive.
