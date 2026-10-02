"use strict";

const test = require("node:test");
const assert = require("node:assert");
const { takeTokenFromHash, switchBlocked, headroom, recommended, switchPrompts, promptWording, uniqueName, renameLabel, nearestLimit, tier, asOf, usageAge, sentence } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

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

test("no prompt while the live account is under 100% on both windows, whatever the scoped row says", () => {
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
