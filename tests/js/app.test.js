"use strict";

const test = require("node:test");
const assert = require("node:assert");
const { takeTokenFromHash, switchBlocked, headroom, recommended, switchPrompts, promptWording, uniqueName, renameLabel, nearestLimit, tier, asOf, usageAge, sentence, ciRoleBadges, removedCiRoles, changedCiTokenDate, ciTag, ciSecretBody, remoteControlQuestion, switchedToast, historyLines, historyReadLines, assignableRoles, newRoleItem, ciPanelWords } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

function limit(kind, percent, fields) {
  return Object.assign({ kind, percent, known: true, windowReset: false }, fields);
}

test("headroom runs both windows to 100% and reads the fuller one", () => {
  assert.strictEqual(headroom({ usage: { limits: [limit("session", 91), limit("weekly_all", 71)] } }), 9);
  assert.strictEqual(headroom({ usage: { limits: [limit("session", 12), limit("weekly_all", 100)] } }), 0);
});

test("headroom ignores the scoped window and rows that say nothing, and is null with no figure", () => {
  const limits = [
    limit("session", 40, { windowReset: true }),
    limit("weekly_all", null, { known: false }),
    limit("weekly_scoped", 99)
  ];
  assert.strictEqual(headroom({ usage: { limits } }), null);
  limits[1] = limit("weekly_all", 30);
  assert.strictEqual(headroom({ usage: { limits } }), 70);
});

const NOW = Date.parse("2026-10-02T20:00:00Z");

function minutesAgo(minutes) {
  return new Date(NOW - minutes * 60000).toISOString();
}

// A card whose 5-hour and 7-day rows were read minutesOld ago, 5 by default:
// current, unless a test says otherwise.
function card(email, fields, session, weekly, minutesOld) {
  return Object.assign({
    email,
    isLive: false,
    standing: "usable",
    canSwitchHere: true,
    offeredTo: [],
    usage: {
      capturedAt: minutesAgo(minutesOld === undefined ? 5 : minutesOld),
      limits: [limit("session", session), limit("weekly_all", weekly), { kind: "weekly_scoped", percent: 100, known: true }]
    }
  }, fields);
}

test("the recommended account is the first usable one in server order that the side may take", () => {
  const accounts = [
    card("a", { standing: "exhausted" }, 0, 100),
    card("b", { canSwitchHere: false }, 0, 0),
    card("c", {}, 0, 0),
    card("d", {}, 0, 0)
  ];
  assert.strictEqual(recommended(accounts, (account) => account.canSwitchHere, NOW).email, "c");
  assert.strictEqual(recommended(accounts, () => false, NOW), null);
});

test("an account whose figures are past the stale threshold is never recommended, and a current one behind it is", () => {
  const accounts = [card("old@x", {}, 10, 20, 31), card("new@x", {}, 10, 20, 29)];
  assert.strictEqual(recommended(accounts, (account) => account.canSwitchHere, NOW).email, "new@x");
  assert.strictEqual(recommended([accounts[0]], (account) => account.canSwitchHere, NOW), null);
});

test("one row read by another source past the threshold leaves the whole card unverified", () => {
  const account = card("a@x", {}, 10, 20, 5);
  account.usage.limits[1].capturedAt = minutesAgo(45);
  assert.strictEqual(recommended([account], () => true, NOW), null);
});

test("a card with no dated 5-hour or 7-day figure is never recommended", () => {
  const account = card("a@x", {}, null, null);
  account.usage.limits[0].known = false;
  account.usage.limits[1].known = false;
  assert.strictEqual(recommended([account], () => true, NOW), null);
});

test("a current 5-hour figure with no 7-day figure is unverified: not recommended, not Usable now", () => {
  const account = card("half@x", { hasCredentials: true, roster: null, refresh: { state: "read" } }, 10, null, 1);
  account.usage.limits[1].known = false;
  assert.strictEqual(recommended([account], () => true, NOW), null);
  assert.deepStrictEqual(sentence(account, NOW), { kind: "warn", text: "Not verified: 7-day figure unknown" });
  const live = card("a@x", { isLive: true, canSwitchHere: false }, 100, 0);
  const prompts = switchPrompts([account, live], [], NOW);
  assert.deepStrictEqual(prompts, [{ side: null, from: "a@x", to: null, unverified: "half@x" }]);
  assert.match(promptWording(prompts[0], [account, live], NOW).text, /half is unverified, its 7-day figure unknown; refresh before switching\.$/);
});

test("a stop at the limit with only stale accounts left names no target and says which account is unverified and how old", () => {
  const accounts = [
    card("old@x", {}, 0, 10, 125),
    card("a@x", { isLive: true, canSwitchHere: false }, 100, 0)
  ];
  const prompts = switchPrompts(accounts, [], NOW);
  assert.deepStrictEqual(prompts, [{ side: null, from: "a@x", to: null, unverified: "old@x" }]);
  assert.deepStrictEqual(promptWording(prompts[0], accounts, NOW), {
    text: "a is at its usage limit. No account to switch to has usage read in the last 30 min. old is unverified, figures 2 h 5 min old; refresh before switching.",
    button: null
  });
});

test("the prompt for another side ranks a current account ahead of a stale one earlier in the server order", () => {
  const accounts = [
    card("old@x", { offeredTo: ["wsl"] }, 0, 0, 240),
    card("new@x", { offeredTo: ["wsl"] }, 0, 0, 1),
    card("w@x", { canSwitchHere: false, standing: "exhausted" }, 100, 0)
  ];
  assert.deepStrictEqual(
    switchPrompts(accounts, [{ side: "wsl", online: true, liveAccount: "w@x" }], NOW),
    [{ side: "wsl", from: "w@x", to: "new@x", unverified: null }]);
});

test("no prompt while the live account is under 100% on both windows and the server places it usable", () => {
  const accounts = [card("b@x", {}, 10, 10), card("a@x", { isLive: true, canSwitchHere: false }, 99, 99)];
  assert.deepStrictEqual(switchPrompts(accounts, [], NOW), []);
});

test("the live account at 100% of either window prompts a switch to the first usable account this side can take", () => {
  for (const [session, weekly] of [[100, 5], [5, 100], [null, 100]]) {
    const accounts = [
      card("c@x", { canSwitchHere: false }, 0, 0),
      card("b@x", {}, 0, 0),
      card("a@x", { isLive: true, canSwitchHere: false, standing: "exhausted" }, session, weekly)
    ];
    assert.deepStrictEqual(switchPrompts(accounts, [], NOW), [{ side: null, from: "a@x", to: "b@x", unverified: null }]);
  }
});

test("a prompt with no usable account left names no target", () => {
  const accounts = [card("b@x", { standing: "exhausted" }, 100, 0), card("a@x", { isLive: true, canSwitchHere: false }, 100, 0)];
  assert.deepStrictEqual(switchPrompts(accounts, [], NOW), [{ side: null, from: "a@x", to: null, unverified: null }]);
});

test("another side at its limit prompts for an account offered to that side, and only while it answers", () => {
  const accounts = [
    card("b@x", {}, 0, 0),
    card("c@x", { offeredTo: ["wsl"] }, 0, 0),
    card("w@x", { canSwitchHere: false, standing: "exhausted" }, 100, 0)
  ];
  assert.deepStrictEqual(
    switchPrompts(accounts, [{ side: "wsl", online: true, liveAccount: "w@x" }], NOW),
    [{ side: "wsl", from: "w@x", to: "c@x", unverified: null }]);
  assert.deepStrictEqual(switchPrompts(accounts, [{ side: "wsl", online: false, liveAccount: "w@x" }], NOW), []);
});

test("a limited card is never recommended or offered as a switch target, even ahead of a usable one", () => {
  const accounts = [
    card("l@x", { standing: "limited" }, 100, 0),
    card("u@x", {}, 0, 0),
    card("a@x", { isLive: true, canSwitchHere: false }, 100, 0)
  ];
  assert.strictEqual(recommended(accounts, (account) => account.canSwitchHere, NOW).email, "u@x");
  assert.deepStrictEqual(switchPrompts(accounts, [], NOW), [{ side: null, from: "a@x", to: "u@x", unverified: null }]);
});

test("a card at its Fable weekly limit is never recommended or offered by a prompt, on this side or another", () => {
  const accounts = [
    card("f@x", { standing: "model-limited", limitedModel: "Fable", offeredTo: ["wsl"] }, 0, 0),
    card("u@x", { offeredTo: ["wsl"] }, 0, 0),
    card("a@x", { isLive: true, canSwitchHere: false }, 100, 0),
    card("w@x", { canSwitchHere: false, standing: "exhausted" }, 100, 0)
  ];
  assert.strictEqual(recommended(accounts, (account) => account.canSwitchHere, NOW).email, "u@x");
  assert.deepStrictEqual(
    switchPrompts(accounts, [{ side: "wsl", online: true, liveAccount: "w@x" }], NOW),
    [{ side: null, from: "a@x", to: "u@x", unverified: null }, { side: "wsl", from: "w@x", to: "u@x", unverified: null }]);
  assert.strictEqual(recommended([accounts[0]], () => true, NOW), null);
});

test("a Fable row read longer ago than the threshold leaves the card unverified beside fresh 5-hour and 7-day rows", () => {
  const account = card("a@x", {}, 10, 10);
  account.usage.limits[2] = { kind: "weekly_scoped", percent: 99, known: true, capturedAt: minutesAgo(45) };
  assert.strictEqual(recommended([account], () => true, NOW), null);
  account.usage.limits[2] = { kind: "weekly_scoped", percent: 0, known: false };
  assert.strictEqual(recommended([account], () => true, NOW).email, "a@x");
});

test("a live account at its Fable weekly limit prompts a switch to an account with Fable headroom", () => {
  const accounts = [
    card("f@x", { standing: "model-limited", limitedModel: "Fable" }, 0, 0),
    card("u@x", {}, 0, 0),
    card("a@x", { isLive: true, canSwitchHere: false, standing: "model-limited", limitedModel: "Fable" }, 10, 10)
  ];
  assert.deepStrictEqual(switchPrompts(accounts, [], NOW), [{ side: null, from: "a@x", to: "u@x", unverified: null }]);
});

test("a card at its Fable weekly limit says so with its reset instead of Usable now", () => {
  const account = card("f@x", {
    standing: "model-limited",
    limitedModel: "Fable",
    nextResetAt: new Date(NOW + 3 * 3600 * 1000).toISOString(),
    hasCredentials: true,
    roster: null,
    refresh: { state: "read" }
  }, 0, 0);
  assert.deepStrictEqual(sentence(account, NOW), { kind: "crit", text: "Fable weekly limit reached, resets in 3 h 0 min" });
});

test("a 100% row whose window has reset raises no prompt", () => {
  const stale = card("a@x", { isLive: true, canSwitchHere: false }, 100, 5);
  stale.usage.limits[0].windowReset = true;
  assert.deepStrictEqual(switchPrompts([card("b@x", {}, 0, 0), stale], [], NOW), []);
});

test("windows that reset since an old read do not make the card current: it is not Usable now and not recommended", () => {
  const account = card("a@x", { hasCredentials: true, roster: null, refresh: { state: "read" } }, null, null, 6 * 24 * 60);
  account.usage.limits[0].windowReset = true;
  account.usage.limits[1].windowReset = true;
  assert.strictEqual(recommended([account], () => true, NOW), null);
  assert.deepStrictEqual(sentence(account, NOW), { kind: "warn", text: "Was usable; read 6 d ago, may be out of date" });
});

function history() {
  const calls = [];
  return { calls, replaceState: (...args) => calls.push(args) };
}

test("a #t= fragment yields the token and is removed from the URL", () => {
  const hist = history();
  const token = takeTokenFromHash({ hash: "#t=abc", pathname: "/", search: "?x=1" }, hist);
  assert.strictEqual(token, "abc");
  assert.deepStrictEqual(hist.calls, [[null, "", "/?x=1"]]);
});

test("a percent-encoded token is decoded", () => {
  const hist = history();
  assert.strictEqual(takeTokenFromHash({ hash: "#t=a%2Db_c", pathname: "/", search: "" }, hist), "a-b_c");
});

test("a fragment that does not decode is removed and yields nothing", () => {
  const hist = history();
  assert.strictEqual(takeTokenFromHash({ hash: "#t=%E0%A4%A", pathname: "/", search: "" }, hist), "");
  assert.strictEqual(hist.calls.length, 1);
});

test("a running refresh pass does not disable Switch", () => {
  const account = { canSwitchHere: true };
  assert.strictEqual(switchBlocked(account, { banner: null, refresh: { inProgress: true } }, false), false);
  assert.strictEqual(switchBlocked(account, { banner: "reconciling", refresh: { inProgress: false } }, false), true);
  assert.strictEqual(switchBlocked(account, { banner: null, refresh: { inProgress: false } }, true), true);
});

test("no fragment, or another one, yields nothing and leaves the URL alone", () => {
  for (const hash of ["", "#", "#other=abc"]) {
    const hist = history();
    assert.strictEqual(takeTokenFromHash({ hash, pathname: "/", search: "" }, hist), "");
    assert.strictEqual(hist.calls.length, 0);
  }
});

test("usage tiers follow the statusline: green, then yellow from 50, orange from 75, red from 90", () => {
  assert.deepStrictEqual([0, 49, 50, 74, 75, 89, 90, 100].map(tier), ["ok", "ok", "warn", "warn", "high", "high", "crit", "crit"]);
});

test("the near-limit sentence names the fuller window, and a tie names the 7-day", () => {
  const near = (session, weekly) => nearestLimit([limit("session", session), limit("weekly_all", weekly), limit("weekly_scoped", 99)]);
  assert.strictEqual(near(81, 92).kind, "weekly_all");
  assert.strictEqual(near(95, 80).kind, "session");
  assert.strictEqual(near(80, 80).kind, "weekly_all");
  assert.strictEqual(near(80, 10).kind, "session");
  assert.strictEqual(near(60, 10), null);
  const reset = nearestLimit([limit("session", 80), limit("weekly_all", 95, { windowReset: true })]);
  assert.strictEqual(reset.kind, "session");
  assert.strictEqual(nearestLimit([limit("session", null, { known: false }), limit("weekly_all", 85)]).kind, "weekly_all");
  assert.strictEqual(nearestLimit([limit("session", null), limit("weekly_all", null)]), null);
});

test("a name another account shares gives way to the address, in buttons and in the prompt", () => {
  const accounts = [
    card("pat@example.com", { isLive: true, canSwitchHere: false }, 100, 0),
    card("pat@example.org", {}, 0, 0),
    card("lee@example.com", { roster: { alias: "Lee" } }, 0, 0)
  ];
  assert.strictEqual(uniqueName(accounts[1], accounts), "pat@example.org");
  assert.strictEqual(uniqueName(accounts[2], accounts), "Lee");
  assert.strictEqual(uniqueName(accounts[1], accounts.slice(1)), "pat");
  const cased = [card("pat@example.net", { roster: { alias: "Pat" } }, 0, 0), accounts[1]];
  assert.strictEqual(uniqueName(cased[0], cased), "pat@example.net");
  assert.strictEqual(uniqueName(cased[1], cased), "pat@example.org");
  const four = ["com", "org", "net", "io"].map((tld) => card("pat@example." + tld, { roster: {} }, 0, 0));
  assert.deepStrictEqual(four.map((account) => renameLabel(account, four)), [
    "Rename pat@example.com", "Rename pat@example.org", "Rename pat@example.net", "Rename pat@example.io"
  ]);
  assert.strictEqual(renameLabel(accounts[2], accounts), "Rename Lee");
  assert.deepStrictEqual(promptWording({ side: "wsl", from: "pat@example.com", to: "pat@example.org" }, accounts), {
    text: "wsl side: pat@example.com is at its usage limit.",
    button: "Switch now to pat@example.org"
  });
  assert.deepStrictEqual(promptWording({ side: null, from: "lee@example.com", to: null }, accounts), {
    text: "Lee is at its usage limit. No other account has headroom.",
    button: null
  });
});

function readCard(minutesOld, session, weekly) {
  const capturedAt = new Date(NOW - minutesOld * 60000).toISOString();
  return {
    email: "a@x",
    isLive: false,
    hasCredentials: true,
    standing: "usable",
    roster: null,
    refresh: { state: "read" },
    usage: {
      source: "refresh",
      capturedAt,
      limits: [limit("session", session), limit("weekly_all", weekly), { kind: "weekly_scoped", known: false, percent: null }]
    }
  };
}

test("every as-of line says how old its figure is", () => {
  const line = asOf(new Date(NOW - 7 * 60000).toISOString(), "refresh", NOW);
  assert.match(line, /^as of .+, 7 min ago via refresh$/);
});

test("a figure older than half an hour says it may be out of date", () => {
  assert.match(asOf(new Date(NOW - 31 * 60000).toISOString(), "snapshot", NOW), /, 31 min ago via snapshot; may be out of date$/);
  assert.doesNotMatch(asOf(new Date(NOW - 29 * 60000).toISOString(), "snapshot", NOW), /out of date/);
});

test("a card never reads Usable now on a stale figure", () => {
  assert.deepStrictEqual(sentence(readCard(5, 10, 20), NOW), { kind: "ok", text: "Usable now" });
  assert.deepStrictEqual(sentence(readCard(45, 10, 20), NOW), { kind: "warn", text: "Was usable; read 45 min ago, may be out of date" });
  assert.match(sentence(readCard(45, 80, 20), NOW).text, /^Near the .+; read 45 min ago, may be out of date$/);
});

test("the oldest figure behind the standing dates the card, a row from another source by its own time", () => {
  const account = readCard(5, 10, 20);
  account.usage.limits[1].capturedAt = new Date(NOW - 50 * 60000).toISOString();
  assert.strictEqual(usageAge(account, NOW), 50 * 60);
  // A reset since the read still dates the row: nothing says the new window is unused.
  account.usage.limits[1].windowReset = true;
  assert.strictEqual(usageAge(account, NOW), 50 * 60);
  account.usage.limits[1].known = false;
  assert.strictEqual(usageAge(account, NOW), 5 * 60);
});

// Roles shaped as GET /api/dashboard returns them.
const AUTOMATIONS = {
  name: "AUTOMATIONS", prefix: "AUTOMATIONS", repository: null, organization: "octo",
  tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "AUTOMATIONS_CLAUDE_API_KEY",
  tokenSetOn: "2026-01-01", tokenSetAt: "2026-01-01T10:00:00+00:00", keySetOn: "2026-01-02", keySetAt: null
};
const LEGACY_LANE_ONE = {
  name: "legacy (review lanes)", prefix: "", repository: "octo/lane-one", organization: null,
  tokenSecret: "CLAUDE_CODE_OAUTH_TOKEN", keySecret: null,
  tokenSetOn: "2026-01-01", tokenSetAt: null, keySetOn: null, keySetAt: null
};

test("ciRoleBadges names the role and its halves and counts the token down to the one-year expiry", () => {
  const roster = { ciTokenGeneratedOn: "2026-01-01", ciRoles: [AUTOMATIONS] };
  assert.deepStrictEqual(ciRoleBadges(roster, Date.parse("2026-06-01T00:00:00Z")), [{
    kind: "ok",
    text: "automations · token+key",
    title: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN and AUTOMATIONS_CLAUDE_API_KEY in org octo. Key set 2026-01-02. Token generated 2026-01-01; expires about 2027-01-01."
  }]);
  assert.strictEqual(ciRoleBadges(roster, Date.parse("2026-12-12T00:00:00Z"))[0].text, "automations · token+key · 20 d left");
  assert.strictEqual(ciRoleBadges(roster, Date.parse("2026-12-12T00:00:00Z"))[0].kind, "warn");
  assert.strictEqual(ciRoleBadges(roster, Date.parse("2027-01-02T00:00:00Z"))[0].kind, "crit");
  const keyOnly = { ciRoles: [Object.assign({}, AUTOMATIONS, { tokenSetOn: null, tokenSetAt: null })] };
  assert.deepStrictEqual(ciRoleBadges(keyOnly, Date.parse("2027-06-01T00:00:00Z")), [{
    kind: "ok",
    text: "automations · key",
    title: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN and AUTOMATIONS_CLAUDE_API_KEY in org octo. Key set 2026-01-02."
  }]);
});

test("a card with two roles shows two badges", () => {
  const roster = { ciTokenGeneratedOn: "2026-01-01", ciRoles: [AUTOMATIONS, LEGACY_LANE_ONE] };
  assert.deepStrictEqual(
    ciRoleBadges(roster, Date.parse("2026-06-01T00:00:00Z")).map((badge) => badge.text),
    ["automations · token+key", "legacy (review lanes) (lane-one) · token"]);
});

test("removedCiRoles sends only the unticked roles, each by its token secret and where, with no dates", () => {
  assert.deepStrictEqual(removedCiRoles([AUTOMATIONS, LEGACY_LANE_ONE], [true, false]), [
    { tokenSecret: "CLAUDE_CODE_OAUTH_TOKEN", repository: "octo/lane-one", organization: null }
  ]);
  assert.deepStrictEqual(removedCiRoles([AUTOMATIONS], [false]), [
    { tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", repository: null, organization: "octo" }
  ]);
});

test("removedCiRoles sends nothing when no role was unticked", () => {
  assert.strictEqual(removedCiRoles([AUTOMATIONS, LEGACY_LANE_ONE], [true, true]), null);
  assert.strictEqual(removedCiRoles([], []), null);
});

test("an Edit sends the CI token date only when the operator changed it", () => {
  // A form drawn with no date, saved untouched after a token half was set: sending null would clear it.
  assert.strictEqual(changedCiTokenDate({ ciTokenGeneratedOn: null }, ""), undefined);
  // A form drawn with 2026-01-01, saved untouched after the account lost that token: sending it back would make a hand marker.
  assert.strictEqual(changedCiTokenDate({ ciTokenGeneratedOn: "2026-01-01" }, "2026-01-01"), undefined);
  assert.strictEqual(changedCiTokenDate({ ciTokenGeneratedOn: "2026-01-01" }, "2026-02-01"), "2026-02-01");
  assert.strictEqual(changedCiTokenDate({ ciTokenGeneratedOn: "2026-01-01" }, ""), null);
  assert.strictEqual(changedCiTokenDate({ ciTokenGeneratedOn: null }, "2026-02-01"), "2026-02-01");
});

test("ciSecretBody reads a repository by its slash, an organization without one, and blank as none", () => {
  assert.deepStrictEqual(ciSecretBody(" TOKEN ", " octo/lane-one "), { name: "TOKEN", repository: "octo/lane-one" });
  assert.deepStrictEqual(ciSecretBody("TOKEN", "octo"), { name: "TOKEN", organization: "octo" });
  assert.strictEqual(ciSecretBody("TOKEN", "  "), null);
  assert.strictEqual(ciSecretBody("", "octo"), null);
});

test("ciRoleBadges keeps a hand-set marker readable and is empty with no marker", () => {
  assert.deepStrictEqual(ciRoleBadges({ ciTokenGeneratedOn: "2026-09-01", ciRoles: [] }, Date.parse("2026-10-01T00:00:00Z")).map((badge) => badge.text), ["CI token · 2026-09-01"]);
  assert.deepStrictEqual(ciRoleBadges({ ciTokenGeneratedOn: null, ciRoles: [] }, Date.parse("2026-10-01T00:00:00Z")), []);
  assert.deepStrictEqual(ciRoleBadges(null, Date.parse("2026-10-01T00:00:00Z")), []);
  // A roster with no ciRoles key at all reads as one with none.
  assert.deepStrictEqual(ciRoleBadges({ ciTokenGeneratedOn: "2026-09-01" }, Date.parse("2026-10-01T00:00:00Z")).map((badge) => badge.text), ["CI token · 2026-09-01"]);
});

test("ciTag tags the side strip CI for a role or a hand-set marker, and nothing otherwise", () => {
  assert.strictEqual(ciTag({ ciTokenGeneratedOn: null, ciRoles: [Object.assign({}, AUTOMATIONS, { tokenSetOn: null })] }), "CI");
  assert.strictEqual(ciTag({ ciTokenGeneratedOn: "2026-09-01" }), "CI");
  assert.strictEqual(ciTag({ ciTokenGeneratedOn: null, ciRoles: [] }), null);
  assert.strictEqual(ciTag({ ciTokenGeneratedOn: null }), null);
  assert.strictEqual(ciTag(null), null);
});

test("Switch asks first only while the Remote Control task is running", () => {
  assert.strictEqual(
    remoteControlQuestion({ task: "claude-remote-control", running: true }, "Work"),
    "Switch to Work? Remote Control restarts under Work; live Remote Control sessions on this machine drop and reopen in Work's Claude app.");
  assert.strictEqual(remoteControlQuestion({ task: "claude-remote-control", running: false }, "Work"), null);
  assert.strictEqual(remoteControlQuestion({ task: null, running: false }, "Work"), null);
});

test("a Remote Control state that could not be read still asks, as a condition", () => {
  assert.strictEqual(
    remoteControlQuestion(null, "Work"),
    "Switch to Work? Remote Control's state could not be read. If it is running, it restarts under Work; live Remote Control sessions on this machine drop and reopen in Work's Claude app.");
  assert.strictEqual(
    remoteControlQuestion({ task: "claude-remote-control", running: null, error: "schtasks.exe did not finish within 20 s" }, "Work"),
    remoteControlQuestion(null, "Work"));
});

test("the switch toast says Remote Control restarted, and a restart that failed warns with its commands", () => {
  assert.deepStrictEqual(
    switchedToast({ now: "b@example.com", parkedAs: "a@example.com", remoteControlRestarted: true }),
    { text: "Switched to b@example.com; parked a@example.com. Remote Control restarted.", kind: "ok" });
  const warned = switchedToast({ now: "b@example.com", remoteControlWarning: "Remote Control was not restarted: denied; restart it by hand: Stop-ScheduledTask claude-remote-control; Start-ScheduledTask claude-remote-control" });
  assert.strictEqual(warned.kind, "warn");
  assert.match(warned.text, /Start-ScheduledTask claude-remote-control/);
  assert.deepStrictEqual(switchedToast({ now: "b@example.com" }), { text: "Switched to b@example.com", kind: "ok" });
});

const { apiKeyBody, apiKeyBadge } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

test("a pasted API key is sent trimmed in the body, and a blank paste sends nothing", () => {
  assert.deepStrictEqual(apiKeyBody("  sk-ant-" + "api03-abc\n"), { key: "sk-ant-" + "api03-abc" });
  assert.strictEqual(apiKeyBody(" \t\n"), null);
  assert.strictEqual(apiKeyBody(""), null);
  assert.strictEqual(apiKeyBody(undefined), null);
});

test("a keepStoredKey toast names the key left when there is one and never says null", () => {
  const { keptKeyToast } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");
  assert.deepStrictEqual(keptKeyToast("a@x removed", { ok: true, body: { warning: null } }), { text: "a@x removed", kind: "ok" });
  assert.deepStrictEqual(keptKeyToast("a@x removed", { ok: true, body: { warning: "The stored key k was left." } }), { text: "a@x removed. The stored key k was left.", kind: "warn" });
  assert.deepStrictEqual(keptKeyToast("a@x removed", { ok: false, body: { message: "locked" } }), { text: "Refused: locked", kind: "error" });
});

test("the API key badge shows only once the roster records a stored key, and names the day", () => {
  assert.strictEqual(apiKeyBadge(null), null);
  assert.strictEqual(apiKeyBadge({ consoleApiKeySetOn: null }), null);
  const badge = apiKeyBadge({ consoleApiKeySetOn: "2026-10-20" });
  assert.strictEqual(badge.text, "API key");
  assert.match(badge.title, /2026-10-20/);
});

test("the Check API key toast shows each status, a near expiry, a key it cannot tell, and a refusal", () => {
  const { apiKeyCheckToast } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");
  const found = (status, flag, expiresAt) => ({ ok: true, body: { outcome: "found", name: "ci", status: status, flag: flag, expiresAt: expiresAt || null } });
  assert.deepStrictEqual(apiKeyCheckToast(found("archived", "archived")), { text: "The Console API key \"ci\" is archived.", kind: "error" });
  assert.deepStrictEqual(apiKeyCheckToast(found("inactive", "inactive")), { text: "The Console API key \"ci\" is inactive.", kind: "error" });
  assert.deepStrictEqual(apiKeyCheckToast(found("expired", "expired", "2026-10-01T00:00:00Z")), { text: "The Console API key \"ci\" has expired (2026-10-01).", kind: "error" });
  assert.deepStrictEqual(apiKeyCheckToast(found("active", "expiresSoon", "2026-11-01T00:00:00Z")), { text: "The Console API key \"ci\" expires on 2026-11-01.", kind: "warn" });
  assert.deepStrictEqual(apiKeyCheckToast(found("active", null)), { text: "The Console API key \"ci\" is active.", kind: "ok" });
  assert.deepStrictEqual(
    apiKeyCheckToast({ ok: true, body: { outcome: "cannotTell", reason: "2 keys fit this key's hint" } }),
    { text: "Cannot tell which listed key is this account's Console API key: 2 keys fit this key's hint.", kind: "warn" });
  assert.deepStrictEqual(
    apiKeyCheckToast({ ok: false, body: { refusal: "AdminApiUnavailable", message: "Admin API unavailable for this account (403)." } }),
    { text: "Refused: Admin API unavailable for this account (403).", kind: "error" });
});

const { roleCheckLines } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

test("roleCheckLines names each role, its holder and its verdicts, and marks a change outside the tool", () => {
  const lines = roleCheckLines({
    checkedAt: "2026-10-20T12:00:00Z",
    roles: [
      { name: "AUTOMATIONS", prefix: "AUTOMATIONS", organization: "octo", tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "AUTOMATIONS_CLAUDE_API_KEY", hasToken: true, hasKey: true, holder: "a@example.com", token: "changed", key: "ok" },
      { name: "ISSUE_LANE", prefix: "ISSUE_LANE", organization: "octo", tokenSecret: "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "ISSUE_LANE_CLAUDE_API_KEY", hasToken: true, hasKey: false, holder: null, token: "unclaimed", key: null },
      { name: "legacy (review lanes)", prefix: "", organization: "octo", tokenSecret: "CLAUDE_CODE_OAUTH_TOKEN", keySecret: null, hasToken: true, hasKey: false, holder: "b@example.com", token: "ok", key: null }
    ],
    failures: [{ organization: "gone", error: "GitHub answered HTTP 404" }]
  });
  assert.deepStrictEqual(lines.map((line) => [line.kind, line.text]), [
    ["crit", "automations in org octo · token+key · a@example.com · token: changed outside the tool · key: ok"],
    ["warn", "issue_lane in org octo · token · no holder · token: unclaimed"],
    ["ok", "legacy (review lanes) in org octo · token · b@example.com · token: ok"],
    ["crit", "org gone could not be read: GitHub answered HTTP 404"]
  ]);
  assert.strictEqual(lines[0].title, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN and AUTOMATIONS_CLAUDE_API_KEY");
});

test("roleCheckLines marks a missing secret critical", () => {
  const [line] = roleCheckLines({ roles: [{ name: "REVIEWS", prefix: "REVIEWS", organization: "octo", tokenSecret: "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "REVIEWS_CLAUDE_API_KEY", hasToken: false, hasKey: false, holder: "a@example.com", token: "missing", key: null }], failures: [] });
  assert.strictEqual(line.kind, "crit");
  assert.strictEqual(line.text, "reviews in org octo · no secrets · a@example.com · token: missing");
});

test("history lines run newest first and name the role half and where, both accounts and the route", () => {
  const entries = [
    { at: "2026-10-01T11:00:00.0000000Z", role: { prefix: "AUTOMATIONS", organization: "octo" }, half: "token", account: "b@example.com", previous: "a@example.com", via: "push" },
    { at: "2026-10-02T09:30:00.0000000Z", role: { prefix: "", repository: "octo/lane-one" }, half: "token", account: null, previous: "b@example.com", via: "delete" }
  ];
  assert.deepStrictEqual(historyLines(entries), [
    "2026-10-02 09:30 \u00b7 legacy (review lanes) in octo/lane-one token: b@example.com \u2192 no account (delete)",
    "2026-10-01 11:00 \u00b7 AUTOMATIONS in org octo token: a@example.com \u2192 b@example.com (push)"
  ]);
  assert.deepStrictEqual(historyLines(null), []);
});

test("a failed history read says so with the status, and only a successful empty read says there were no changes", () => {
  assert.deepStrictEqual(historyReadLines({ ok: false, status: 500 }), ["Could not read the history (HTTP 500)"]);
  assert.deepStrictEqual(historyReadLines({ ok: false }), ["Could not read the history"]);
  assert.deepStrictEqual(historyReadLines({ ok: true, entries: [] }), ["No role changes in the last 30 days."]);
  assert.strictEqual(historyReadLines({ ok: true, entries: [{ at: "2026-10-01T11:00:00Z", role: { prefix: "A" }, half: "key", account: "b@example.com", previous: null, via: "assign" }] }).length, 1);
});

const { ciTokenExpiries, ciExpirySentence } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

function expiryRole(name, tokenSetOn, keySetOn, prefix) {
  return { name: name, prefix: prefix || false, tokenSetOn: tokenSetOn, keySetOn: keySetOn || null };
}

test("token halves set on two dates give two expiry entries, each with its roles", () => {
  const roster = { ciRoles: [
    expiryRole("A", "2026-01-01", "2026-01-01"),
    expiryRole("B", "2026-01-01", "2026-01-01"),
    expiryRole("C", "2026-01-10", "2026-01-10")
  ] };
  const got = ciTokenExpiries(roster, Date.parse("2026-12-20T00:00:00Z"));
  assert.deepStrictEqual(got.map((g) => [g.expiresOn, g.fallback]), [["2027-01-01", ["A", "B"]], ["2027-01-10", ["C"]]]);
});

test("within 30 days the sentence warns, roles with no key stop, and after the date the tone is critical", () => {
  const roster = { ciRoles: [expiryRole("A", "2026-01-01", "2026-01-01"), expiryRole("LEGACY", "2026-01-01", null)] };
  const soon = ciTokenExpiries(roster, Date.parse("2026-12-20T00:00:00Z"));
  assert.strictEqual(soon.length, 1);
  assert.strictEqual(soon[0].tone, "warn");
  assert.deepStrictEqual(soon[0].stop, ["LEGACY"]);
  assert.strictEqual(ciExpirySentence(soon[0]), "When the token expires on 2027-01-01, these roles fall back to their API key only: A; these roles stop: LEGACY");
  assert.strictEqual(ciTokenExpiries(roster, Date.parse("2027-01-02T00:00:00Z"))[0].tone, "crit");
});

test("key halves never produce an expiry entry", () => {
  const roster = { ciRoles: [expiryRole("A", null, "2026-01-01")] };
  assert.deepStrictEqual(ciTokenExpiries(roster, Date.parse("2027-06-01T00:00:00Z")), []);
});

test("outside the 30-day window no expiry entry is shown", () => {
  const roster = { ciRoles: [expiryRole("A", "2026-01-01", "2026-01-01")] };
  assert.deepStrictEqual(ciTokenExpiries(roster, Date.parse("2026-06-01T00:00:00Z")), []);
  assert.strictEqual(ciTokenExpiries(roster, Date.parse("2026-12-02T00:00:00Z")).length, 1);
  assert.deepStrictEqual(ciTokenExpiries(roster, Date.parse("2026-12-01T00:00:00Z")), []);
});

test("after the date the sentence reads in the past", () => {
  const roster = { ciRoles: [expiryRole("A", "2026-01-01", "2026-01-01"), expiryRole("LEGACY", "2026-01-01", null)] };
  const [late] = ciTokenExpiries(roster, Date.parse("2027-01-02T00:00:00Z"));
  assert.strictEqual(ciExpirySentence(late), "The token expired on 2027-01-01; these roles now run on their API key only: A; these roles stopped: LEGACY");
});

test("a split role whose key half another account holds falls back to its key rather than stopping", () => {
  const role = Object.assign({}, AUTOMATIONS, { keySetOn: null, keySetAt: null });
  const keyHolder = Object.assign({}, AUTOMATIONS, { tokenSetOn: null, tokenSetAt: null });
  const accounts = [
    { email: "a@example.com", roster: { ciRoles: [role] } },
    { email: "b@example.com", roster: { ciRoles: [keyHolder] } }
  ];
  const [split] = ciTokenExpiries(accounts[0].roster, Date.parse("2026-12-20T00:00:00Z"), accounts, "a@example.com");
  assert.deepStrictEqual([split.fallback, split.stop], [["automations"], []]);
  const [alone] = ciTokenExpiries(accounts[0].roster, Date.parse("2026-12-20T00:00:00Z"), [accounts[0]], "a@example.com");
  assert.deepStrictEqual([alone.fallback, alone.stop], [[], ["automations"]]);
});

test("expiry lines name a repository's role with its repository, as the badge does", () => {
  const one = Object.assign(expiryRole("tools", "2026-01-01", "2026-01-01", true), { repository: "octo/one" });
  const two = Object.assign(expiryRole("tools", "2026-01-01", "2026-01-01", true), { repository: "octo/two" });
  const [got] = ciTokenExpiries({ ciRoles: [one, two] }, Date.parse("2026-12-20T00:00:00Z"));
  assert.deepStrictEqual(got.fallback, ["tools (one)", "tools (two)"]);
});

test("roleCheckLines gives each unknown half the cause the server sent", () => {
  function unknownLine(reason) {
    return roleCheckLines({ roles: [{ name: "AUTOMATIONS", prefix: "AUTOMATIONS", organization: "octo", tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", keySecret: null, hasToken: true, hasKey: false, holder: "a@example.com", token: "unknown", tokenReason: reason, key: null }], failures: [] })[0];
  }
  assert.strictEqual(unknownLine("noInstant").text, "automations in org octo · token · a@example.com · token: unknown (only the day it was set is recorded, and GitHub shows that same day)");
  assert.strictEqual(unknownLine("olderThanWrite").text, "automations in org octo · token · a@example.com · token: unknown (GitHub lists it as older than the tool's write)");
  assert.strictEqual(unknownLine("noListedTime").text, "automations in org octo · token · a@example.com · token: unknown (GitHub listed no readable update time)");
  assert.strictEqual(unknownLine("noListedTime").kind, "warn");
  assert.strictEqual(unknownLine(null).text, "automations in org octo · token · a@example.com · token: unknown");
});

test("a role whose other half another account holds reads split and names both accounts", () => {
  const tokenOnly = Object.assign({}, AUTOMATIONS, { keySetOn: null });
  const keyOnly = Object.assign({}, AUTOMATIONS, { tokenSetOn: null, tokenSetAt: null });
  const accounts = [
    { email: "b@example.com", roster: { ciRoles: [tokenOnly] } },
    { email: "a@example.com", roster: { ciRoles: [keyOnly] } }
  ];
  const at = Date.parse("2026-06-01T00:00:00Z");
  const [holder] = ciRoleBadges(accounts[0].roster, at, accounts, "b@example.com");
  assert.strictEqual(holder.text, "automations · token · split");
  assert.strictEqual(holder.kind, "warn");
  assert.match(holder.title, /split: token from b@example\.com, key from a@example\.com; Push API key re-sends the key\./);
  const [previous] = ciRoleBadges(accounts[1].roster, at, accounts, "a@example.com");
  assert.strictEqual(previous.text, "automations · key · split");
  assert.match(previous.title, /split: token from b@example\.com, key from a@example\.com/);
  // With no other holder, a lone half is not split.
  assert.strictEqual(ciRoleBadges(accounts[0].roster, at, [accounts[0]], "b@example.com")[0].text, "automations · token");
});

test("the CI panel says what Start does before anything starts", () => {
  assert.match(ciPanelWords().intro, /^Pick the roles, then Start sign-in opens Anthropic's sign-in in this account's browser profile; paste the code it shows here\.$/);
  assert.strictEqual(ciPanelWords().start, "Start sign-in");
});

test("assignableRoles offers each roster role once, then the unclaimed roles Check roles found", () => {
  const accounts = [
    { email: "a@example.com", roster: { ciRoles: [AUTOMATIONS] } },
    { email: "b@example.com", roster: { ciRoles: [LEGACY_LANE_ONE] } }
  ];
  const check = { roles: [
    { name: "AUTOMATIONS", prefix: "AUTOMATIONS", organization: "octo", tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "AUTOMATIONS_CLAUDE_API_KEY", holder: "a@example.com" },
    { name: "ISSUE_LANE", prefix: "ISSUE_LANE", organization: "octo", tokenSecret: "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "ISSUE_LANE_CLAUDE_API_KEY", holder: null }
  ] };
  assert.deepStrictEqual(assignableRoles(accounts, check), [
    { item: { tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", organization: "octo" }, holder: "a@example.com", label: "automations in org octo · token+key · backed by a@example.com" },
    { item: { tokenSecret: "CLAUDE_CODE_OAUTH_TOKEN", repository: "octo/lane-one" }, holder: "b@example.com", label: "legacy (review lanes) (lane-one) in octo/lane-one · token only · backed by b@example.com" },
    { item: { tokenSecret: "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", organization: "octo" }, holder: null, label: "issue_lane in org octo · token+key · no holder" }
  ]);
  assert.deepStrictEqual(assignableRoles([], null), []);
});

test("newRoleItem says creates only for the secrets the last Check roles did not list, and is null with no prefix", () => {
  assert.deepStrictEqual(newRoleItem(" reviews ", "octo", null), {
    item: { prefix: "REVIEWS", organization: "octo" },
    confirm: "In org octo this creates REVIEWS_CLAUDE_CODE_OAUTH_TOKEN and REVIEWS_CLAUDE_API_KEY, visible to all repositories. Go ahead?"
  });
  const check = { roles: [{ organization: "octo", tokenSecret: "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "REVIEWS_CLAUDE_API_KEY", hasToken: true, hasKey: false }] };
  assert.strictEqual(newRoleItem("reviews", "octo", check).confirm,
    "In org octo this creates REVIEWS_CLAUDE_API_KEY, visible to all repositories, and sets REVIEWS_CLAUDE_CODE_OAUTH_TOKEN, keeping who may read it. Go ahead?");
  check.roles[0].hasKey = true;
  assert.strictEqual(newRoleItem("reviews", "octo", check).confirm,
    "In org octo this sets REVIEWS_CLAUDE_CODE_OAUTH_TOKEN and REVIEWS_CLAUDE_API_KEY, keeping who may read them. Go ahead?");
  assert.deepStrictEqual(newRoleItem("X", "octo/repo").item, { prefix: "X", repository: "octo/repo" });
  assert.strictEqual(newRoleItem("  ", "octo"), null);
});

const { reissueButton, ciPanelChecked, meterTicks, alertLines } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

// Three accounts as the dashboard sends them: two back a role, one does not.
const BACKER = { email: "a@example.com", roster: { backsCiRole: true, ciRoles: [AUTOMATIONS] } };
const OTHER_BACKER = { email: "b@example.com", roster: { backsCiRole: true, ciRoles: [LEGACY_LANE_ONE] } };
const NO_ROLE = { email: "c@example.com", roster: { backsCiRole: false, ciRoles: [], ciTokenGeneratedOn: "2026-10-01" } };
const UNCLAIMED = { roles: [
  { name: "ISSUE_LANE", prefix: "ISSUE_LANE", organization: "octo", tokenSecret: "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "ISSUE_LANE_CLAUDE_API_KEY", holder: null }
] };

test("reissue opens the CI token panel with the account's roles preselected", () => {
  let opened = null;
  const button = reissueButton(BACKER, (account) => { opened = account; });
  assert.strictEqual(button.label, "Re-issue token");
  button.click();
  assert.strictEqual(opened, BACKER);
  // The panel ticks the opened account's AUTOMATIONS role, not another account's role or an unclaimed one.
  const roles = assignableRoles([BACKER, OTHER_BACKER, NO_ROLE], UNCLAIMED);
  assert.deepStrictEqual(ciPanelChecked(roles, opened.email), [true, false, false]);
});

test("reissueButton is offered only on an account the server says backs a CI role", () => {
  assert.strictEqual(reissueButton(NO_ROLE, () => {}), null);
  assert.strictEqual(reissueButton({ email: "d@example.com", roster: null }, () => {}), null);
});

test("ciPanelChecked ticks exactly the roles the given account backs", () => {
  const roles = assignableRoles([BACKER, OTHER_BACKER, NO_ROLE], UNCLAIMED);
  assert.deepStrictEqual(ciPanelChecked(roles, OTHER_BACKER.email), [false, true, false]);
  assert.deepStrictEqual(ciPanelChecked(roles, NO_ROLE.email), [false, false, false]);
});

test("meterTicks marks the server's thresholds only on an account that backs a CI role", () => {
  // Not the shipped thresholds, so a value copied into the page would show here.
  assert.deepStrictEqual(meterTicks(BACKER, [60, 85]), [60, 85]);
  assert.deepStrictEqual(meterTicks(NO_ROLE, [60, 85]), []);
  assert.deepStrictEqual(meterTicks(BACKER, undefined), []);
});

test("alertLines writes the server's percent and threshold the way a card writes a figure", () => {
  const accounts = [{ email: "a@example.com", roster: { alias: "Work" } }];
  assert.deepStrictEqual(alertLines([
    { account: "a@example.com", level: "critical", percent: 92.4, threshold: 85 },
    { account: "z@example.com", level: "warning", percent: 89.6, threshold: 60 }
  ], accounts), [
    { kind: "crit", text: "Work: 7-day usage at 92%, past 85%" },
    { kind: "warn", text: "z@example.com: 7-day usage at 90%, past 60%" }
  ]);
  assert.deepStrictEqual(alertLines(undefined, accounts), []);
});

const { killSwitchRows, toggleKillSwitch } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

// Kill switches in the shape POST /api/roles/check returns them.
const LANES = { name: "CLAUDE_LANES_DISABLED", scope: "organization", owner: "octo", exists: true, value: "false", state: "running", writable: true, error: null };
function repoSwitch(owner, value, state) {
  return { name: "CLAUDE_LANES_REPO_DISABLED", scope: "repository", owner: owner, exists: value !== null, value: value, state: state, writable: false, error: null };
}

test("each lane repository shows its switch, read-only and with no toggle", () => {
  const rows = killSwitchRows({ killSwitches: [LANES, repoSwitch("octo/lanes", "false", "running"), repoSwitch("octo/other", null, "stopped")] });
  assert.strictEqual(rows.length, 3);
  assert.strictEqual(rows[1].text, "CLAUDE_LANES_REPO_DISABLED in octo/lanes · running (false) · read-only");
  assert.strictEqual(rows[2].text, "CLAUDE_LANES_REPO_DISABLED in octo/other · stopped (not set) · read-only");
  assert.strictEqual(rows[1].toggle, null);
  assert.strictEqual(rows[2].toggle, null);
});

test("a running organization switch offers Stop, which sends true; a stopped one offers Start, which sends false", () => {
  const [running] = killSwitchRows({ killSwitches: [LANES] });
  assert.strictEqual(running.text, "CLAUDE_LANES_DISABLED in org octo · running (false)");
  assert.strictEqual(running.toggle.label, "Stop");
  assert.deepStrictEqual(running.toggle.body, { org: "octo", name: "CLAUDE_LANES_DISABLED", value: "true" });
  const [stopped] = killSwitchRows({ killSwitches: [Object.assign({}, LANES, { name: "CLAUDE_REVIEW_DISABLED", value: "true", state: "stopped" })] });
  assert.strictEqual(stopped.toggle.label, "Start");
  assert.deepStrictEqual(stopped.toggle.body, { org: "octo", name: "CLAUDE_REVIEW_DISABLED", value: "false" });
});

test("a switch that is missing or could not be read has no toggle", () => {
  const rows = killSwitchRows({ killSwitches: [
    Object.assign({}, LANES, { exists: false, value: null, state: "stopped" }),
    Object.assign({}, LANES, { exists: false, value: null, state: null, error: "GitHub answered HTTP 403" })
  ] });
  assert.strictEqual(rows[0].toggle, null);
  assert.strictEqual(rows[1].toggle, null);
  assert.strictEqual(rows[1].kind, "crit");
});

test("a toggle sends nothing until the page's confirmation is accepted", async () => {
  const [row] = killSwitchRows({ killSwitches: [LANES] });
  const sent = [];
  const asked = [];
  function post(path, method, body) { sent.push([path, method, body]); return Promise.resolve({ ok: true, body: {} }); }

  const declined = await toggleKillSwitch(row.toggle, (question) => { asked.push(question); return false; }, post);
  assert.strictEqual(declined, null);
  assert.strictEqual(asked.length, 1);
  assert.deepStrictEqual(sent, []);

  await toggleKillSwitch(row.toggle, () => true, post);
  assert.deepStrictEqual(sent, [["/api/kill-switches", "POST", { org: "octo", name: "CLAUDE_LANES_DISABLED", value: "true" }]]);
});

const { limitStopSources, limitStopNotice, limitStopGate } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

function holding(email, roles) {
  return { email: email, roster: { ciRoles: roles } };
}

const AUTOMATIONS_TOKEN = { prefix: "AUTOMATIONS", organization: "octo", tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "AUTOMATIONS_CLAUDE_API_KEY", tokenSetOn: "2026-10-01", keySetOn: null };
const AUTOMATIONS_KEY = Object.assign({}, AUTOMATIONS_TOKEN, { tokenSetOn: null, keySetOn: "2026-10-01" });

test("a ticked role scans each other account that holds it, once, and never this account", () => {
  const accounts = [
    holding("a@example.com", [AUTOMATIONS_TOKEN]),
    holding("me@example.com", [AUTOMATIONS_KEY]),
    holding("c@example.com", [Object.assign({}, AUTOMATIONS_TOKEN, { organization: "elsewhere" })])
  ];
  const ticked = [{ tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", organization: "octo" }];
  assert.deepStrictEqual(limitStopSources(ticked, accounts, "me@example.com"), ["a@example.com"]);
  assert.deepStrictEqual(limitStopSources([{ tokenSecret: "OTHER_CLAUDE_CODE_OAUTH_TOKEN", organization: "octo" }], accounts, "me@example.com"), []);
});

test("a typed row naming a held role, in any case, scans its holder", () => {
  const accounts = [holding("a@example.com", [AUTOMATIONS_TOKEN])];
  const typed = newRoleItem("automations", "OCTO").item;
  assert.deepStrictEqual(limitStopSources([typed], accounts, "me@example.com"), ["a@example.com"]);
});

test("a split role scans the holder of each half", () => {
  const accounts = [holding("a@example.com", [AUTOMATIONS_TOKEN]), holding("b@example.com", [AUTOMATIONS_KEY])];
  const ticked = [{ tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", organization: "octo" }];
  assert.deepStrictEqual(limitStopSources(ticked, accounts, "me@example.com"), ["a@example.com", "b@example.com"]);
});

function answering(body) {
  const sent = [];
  return { sent: sent, post: (path, method, request) => { sent.push([path, method, request]); return Promise.resolve({ ok: true, body: body }); } };
}

function hitFor(account, label, credential) {
  return { account: account, repository: "octo/lanes", runId: 101, url: "https://github.com/octo/lanes/actions/runs/101", createdAt: "2026-10-18T09:00:00+00:00", role: "AUTOMATIONS", credential: credential, label: label };
}

test("two previous holders are scanned with one request naming both, and each account's hits are shown under it", async () => {
  const server = answering({
    outcome: "checked",
    hits: [hitFor("b@example.com", "credit stop", "AUTOMATIONS_CLAUDE_API_KEY"), hitFor("a@example.com", "subscription limit", "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN")],
    problems: []
  });
  const asked = [];
  const moved = await limitStopGate(["a@example.com", "b@example.com"], server.post, (lines) => { asked.push(lines.map((line) => line.text)); return Promise.resolve(true); });
  assert.strictEqual(moved, true);
  assert.deepStrictEqual(server.sent, [["/api/roles/limit-stops", "POST", { from: ["a@example.com", "b@example.com"] }]]);
  assert.deepStrictEqual(asked, [[
    "a@example.com: This account hit a limit in the last 7 days.",
    "subscription limit · AUTOMATIONS (AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN) · octo/lanes · 2026-10-18",
    "b@example.com: This account hit a limit in the last 7 days.",
    "credit stop · AUTOMATIONS (AUTOMATIONS_CLAUDE_API_KEY) · octo/lanes · 2026-10-18"
  ]]);
});

test("with no other holder no scan runs and the move goes ahead", async () => {
  const server = answering({ outcome: "checked", hits: [], problems: [] });
  assert.strictEqual(await limitStopGate([], server.post, () => { throw new Error("asked"); }), true);
  assert.deepStrictEqual(server.sent, []);
});

test("a clean scan moves at once without asking", async () => {
  const server = answering({ outcome: "checked", hits: [], problems: [] });
  assert.strictEqual(await limitStopGate(["a@example.com"], server.post, () => { throw new Error("asked"); }), true);
  assert.strictEqual(server.sent.length, 1);
});

test("with no lane repositories the warning says so and the move goes ahead only on Move anyway", async () => {
  const server = answering({ outcome: "notChecked", hits: [], problems: [] });
  const asked = [];
  assert.strictEqual(await limitStopGate(["a@example.com"], server.post, (lines) => { asked.push(lines[0].text); return Promise.resolve(true); }), true);
  assert.strictEqual(await limitStopGate(["a@example.com"], server.post, () => Promise.resolve(false)), false);
  assert.deepStrictEqual(asked, ["limit stops not checked: no lane repositories configured"]);
});

test("a failed request, or an answer the warning cannot be read from, still asks rather than blocking", async () => {
  const failing = (path, method, request) => Promise.reject(new Error("offline"));
  const asked = [];
  const ask = (lines) => { asked.push(lines[0].text); return Promise.resolve(true); };
  assert.strictEqual(await limitStopGate(["a@example.com"], failing, ask), true);
  assert.strictEqual(await limitStopGate(["a@example.com"], answering({ outcome: "checked", hits: { not: "a list" } }).post, ask), true);
  assert.strictEqual(await limitStopGate(["a@example.com"], answering(null).post, ask), true);
  assert.strictEqual(asked.length, 3);
  assert.strictEqual(asked[0], "limit stops not checked for a@example.com. Refused: Error: offline");
  assert.ok(asked[1].startsWith("limit stops not checked: TypeError"), asked[1]);
  assert.strictEqual(asked[2], "limit stops not checked for a@example.com. Refused: unknown reason");
});

test("a partial scan says so beside its hits, and no line mentions Anthropic support", () => {
  const lines = limitStopNotice(["a@example.com"], { ok: true, body: { outcome: "partial", hits: [hitFor("a@example.com", "credit stop", "AUTOMATIONS_CLAUDE_API_KEY")], problems: ["GitHub's API rate limit was reached"] } });
  assert.deepStrictEqual(lines.map((line) => line.text), [
    "a@example.com: This account hit a limit in the last 7 days.",
    "credit stop · AUTOMATIONS (AUTOMATIONS_CLAUDE_API_KEY) · octo/lanes · 2026-10-18",
    "limit stops checked only in part: GitHub's API rate limit was reached"
  ]);
  assert.strictEqual(lines[1].url, "https://github.com/octo/lanes/actions/runs/101");
  assert.ok(lines.every((line) => line.text.indexOf("Anthropic support") < 0));
});

test("a split role and a typed held role are scanned with one request naming every holder", async () => {
  const reviews = Object.assign({}, AUTOMATIONS_TOKEN, { prefix: "REVIEWS", tokenSecret: "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", keySecret: "REVIEWS_CLAUDE_API_KEY" });
  const accounts = [holding("a@example.com", [AUTOMATIONS_TOKEN]), holding("b@example.com", [AUTOMATIONS_KEY]), holding("c@example.com", [reviews])];
  const picked = [{ tokenSecret: "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", organization: "octo" }, newRoleItem("reviews", "octo").item];
  const server = answering({ outcome: "checked", hits: [], problems: [] });
  assert.strictEqual(await limitStopGate(limitStopSources(picked, accounts, "me@example.com"), server.post, () => { throw new Error("asked"); }), true);
  assert.deepStrictEqual(server.sent, [["/api/roles/limit-stops", "POST", { from: ["a@example.com", "b@example.com", "c@example.com"] }]]);
});
