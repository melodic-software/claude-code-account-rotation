"use strict";

const test = require("node:test");
const assert = require("node:assert");
const { takeTokenFromHash, switchBlocked, headroom, recommended, switchPrompts, promptWording, uniqueName, nearestLimit, tier } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

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

test("the recommended account is the first usable one in server order that the side may take", () => {
  const accounts = [
    { email: "a", standing: "exhausted", canSwitchHere: true },
    { email: "b", standing: "usable", canSwitchHere: false },
    { email: "c", standing: "usable", canSwitchHere: true },
    { email: "d", standing: "usable", canSwitchHere: true }
  ];
  assert.strictEqual(recommended(accounts, (account) => account.canSwitchHere).email, "c");
  assert.strictEqual(recommended(accounts, () => false), null);
});


function card(email, fields, session, weekly) {
  return Object.assign({
    email,
    isLive: false,
    standing: "usable",
    canSwitchHere: true,
    offeredTo: [],
    usage: { limits: [{ kind: "session", percent: session }, { kind: "weekly_all", percent: weekly }, { kind: "weekly_scoped", percent: 100 }] }
  }, fields);
}

test("no prompt while the live account is under 100% on both windows, whatever the scoped row says", () => {
  const accounts = [card("b@x", {}, 10, 10), card("a@x", { isLive: true, canSwitchHere: false }, 99, 99)];
  assert.deepStrictEqual(switchPrompts(accounts, []), []);
});

test("the live account at 100% of either window prompts a switch to the first usable account this side can take", () => {
  for (const [session, weekly] of [[100, 5], [5, 100], [null, 100]]) {
    const accounts = [
      card("c@x", { canSwitchHere: false }, 0, 0),
      card("b@x", {}, 0, 0),
      card("a@x", { isLive: true, canSwitchHere: false, standing: "exhausted" }, session, weekly)
    ];
    assert.deepStrictEqual(switchPrompts(accounts, []), [{ side: null, from: "a@x", to: "b@x" }]);
  }
});

test("a prompt with no usable account left names no target", () => {
  const accounts = [card("b@x", { standing: "exhausted" }, 100, 0), card("a@x", { isLive: true, canSwitchHere: false }, 100, 0)];
  assert.deepStrictEqual(switchPrompts(accounts, []), [{ side: null, from: "a@x", to: null }]);
});

test("another side at its limit prompts for an account offered to that side, and only while it answers", () => {
  const accounts = [
    card("b@x", {}, 0, 0),
    card("c@x", { offeredTo: ["wsl"] }, 0, 0),
    card("w@x", { canSwitchHere: false, standing: "exhausted" }, 100, 0)
  ];
  assert.deepStrictEqual(
    switchPrompts(accounts, [{ side: "wsl", online: true, liveAccount: "w@x" }]),
    [{ side: "wsl", from: "w@x", to: "c@x" }]);
  assert.deepStrictEqual(switchPrompts(accounts, [{ side: "wsl", online: false, liveAccount: "w@x" }]), []);
});

test("a limited card is never recommended or offered as a switch target, even ahead of a usable one", () => {
  const accounts = [
    card("l@x", { standing: "limited" }, 100, 0),
    card("u@x", {}, 0, 0),
    card("a@x", { isLive: true, canSwitchHere: false }, 100, 0)
  ];
  assert.strictEqual(recommended(accounts, (account) => account.canSwitchHere).email, "u@x");
  assert.deepStrictEqual(switchPrompts(accounts, []), [{ side: null, from: "a@x", to: "u@x" }]);
});

test("a 100% row whose window has reset raises no prompt", () => {
  const stale = card("a@x", { isLive: true, canSwitchHere: false }, 100, 5);
  stale.usage.limits[0].windowReset = true;
  assert.deepStrictEqual(switchPrompts([card("b@x", {}, 0, 0), stale], []), []);
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
  assert.deepStrictEqual(promptWording({ side: "wsl", from: "pat@example.com", to: "pat@example.org" }, accounts), {
    text: "wsl side: pat@example.com is at its usage limit.",
    button: "Switch now to pat@example.org"
  });
  assert.deepStrictEqual(promptWording({ side: null, from: "lee@example.com", to: null }, accounts), {
    text: "Lee is at its usage limit. No other account has headroom.",
    button: null
  });
});
