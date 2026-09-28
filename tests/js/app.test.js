"use strict";

const test = require("node:test");
const assert = require("node:assert");
const { takeTokenFromHash, switchBlocked, switchPrompts } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

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
