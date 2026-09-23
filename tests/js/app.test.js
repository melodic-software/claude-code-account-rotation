"use strict";

const test = require("node:test");
const assert = require("node:assert");
const { takeTokenFromHash } = require("../../src/ClaudeCodeAccountRotation.App/wwwroot/app.js");

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

test("no fragment, or another one, yields nothing and leaves the URL alone", () => {
  for (const hash of ["", "#", "#other=abc"]) {
    const hist = history();
    assert.strictEqual(takeTokenFromHash({ hash, pathname: "/", search: "" }, hist), "");
    assert.strictEqual(hist.calls.length, 0);
  }
});
