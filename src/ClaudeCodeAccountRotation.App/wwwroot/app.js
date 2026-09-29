(function () {
  "use strict";

  // Loaded by Node for tests/js, which has no page: export the pure helpers and stop.
  if (typeof document === "undefined") {
    module.exports = {
      takeTokenFromHash: takeTokenFromHash,
      switchBlocked: switchBlocked,
      headroom: headroom,
      recommended: recommended,
      switchPrompts: switchPrompts,
      promptWording: promptWording,
      uniqueName: uniqueName,
      renameLabel: renameLabel,
      nearestLimit: nearestLimit,
      tier: tier
    };
    return;
  }

  // Ten seconds. Each poll reads this machine's own files and never starts a
  // usage read: only the page's load, its Refresh buttons, and the rate-limit
  // stop hook's route do.
  var POLL_MS = 10000;
  var BROWSERS = ["", "chrome", "edge", "brave"];
  // How close a login's fixed expiry has to be before its card warns.
  var LOGIN_WARN_SECONDS = 7 * 24 * 3600;
  // The select value that means "this profile is not in the list"; every other
  // value is an index into profileCatalog, because a directory name can hold
  // any character and a joined key would break on the separator.
  var OTHER = "other";
  var cards = document.getElementById("cards");
  var banner = document.getElementById("banner");
  var setup = document.getElementById("setup");
  var warnings = document.getElementById("warnings");
  var captured = document.getElementById("captured");
  var refreshAllButton = document.getElementById("refresh-all");
  var refreshStateLine = document.getElementById("refresh-state");
  var toast = document.getElementById("toast");
  var nowLine = document.getElementById("now");
  // The accounts the last dashboard named, and the other sides the last side
  // read named. A side's Now strip and every row's switch buttons are drawn
  // from the two together.
  var lastAccounts = [];
  var lastDashboard = null;
  var lastSides = [];
  // The sides, and what each held, that the rows' switch buttons were last drawn for.
  var renderedSides = "";
  // This side has no name on the wire; the other sides are named by config.
  var HERE = "This machine";
  var addForm = document.getElementById("add");
  var addEmail = document.getElementById("add-email");
  var toastTimer = null;
  var busy = false;
  // What the machine's browsers publish, read once at load. Every profile
  // picker on the page is built from this one list.
  var profileCatalog = [];
  var addFields = null;
  // The card node per e-mail, rebuilt by each render, so a login panel can be
  // hung on the right card without querying by an address that needs escaping.
  var cardNodes = {};
  // The addresses of the cards on the page, in the order they are on it, set by
  // the same rebuild that fills cardNodes. What an arriving payload is compared
  // against to tell a reorder from an update in place.
  var renderedOrder = [];
  // The per-process token from the document's meta, or from a #t= fragment that
  // --open put on the URL, or pasted by the operator. It stays in this closure:
  // the page reads the fragment once and removes it from the address bar, and
  // puts the token in no storage and no URL of its own.
  var instanceToken = readInstanceToken();
  var polling = null;

  function readInstanceToken() {
    var meta = document.querySelector('meta[name="ccar-instance-token"]');
    var value = meta ? (meta.getAttribute("content") || "") : "";
    return value.trim();
  }

  // The token from a #t= fragment, with the fragment taken off the address bar
  // and the history entry; "" when there is none or it does not decode.
  // Whether a card's Switch is disabled. A running refresh pass is not a
  // reason: the switch route ends the pass and then switches.
  function switchBlocked(account, dashboard, isBusy) {
    return !account.canSwitchHere || isBusy || !!dashboard.banner;
  }

  // Points left before the first of the 5-hour and 7-day windows runs out. Both
  // run to 100% (Q19); a row that is unknown or has reset since its read says
  // nothing, and null is "no figure at all".
  function headroom(account) {
    var used = account.usage.limits.filter(function (limit) {
      return (limit.kind === "session" || limit.kind === "weekly_all") && limit.known && !limit.windowReset && limit.percent !== null;
    }).map(function (limit) { return limit.percent; });
    return used.length ? Math.max(0, Math.round(100 - Math.max.apply(null, used))) : null;
  }

  // The account a side should take next: the first usable card in the server's
  // order, which is the soonest weekly reset, among those that side may take.
  function recommended(accounts, takes) {
    return accounts.filter(function (account) { return account.standing === "usable" && takes(account); })[0] || null;
  }

  // One "switch now" prompt per side whose account is at 100% of its 5-hour or
  // 7-day window: { side, from, to }, side null for this side and to null when
  // no account that side can take has headroom. The recommendation is the first
  // usable card in the server's order, which is the soonest weekly reset. Nothing
  // switches on its own; the prompt is a button the operator clicks.
  function switchPrompts(accounts, sides) {
    function atLimit(email) {
      var account = accounts.filter(function (candidate) { return candidate.email === email; })[0];
      return !!account && account.usage.limits.some(function (limit) {
        return (limit.kind === "session" || limit.kind === "weekly_all") && limit.percent >= 100 && !limit.windowReset;
      });
    }
    function firstUsable(takes) {
      var found = recommended(accounts, takes);
      return found ? found.email : null;
    }
    var prompts = [];
    var live = accounts.filter(function (account) { return account.isLive; })[0];
    if (live && atLimit(live.email)) {
      prompts.push({ side: null, from: live.email, to: firstUsable(function (account) { return account.canSwitchHere; }) });
    }
    sides.forEach(function (side) {
      if (side.online && side.liveAccount && atLimit(side.liveAccount)) {
        prompts.push({
          side: side.side,
          from: side.liveAccount,
          to: firstUsable(function (account) { return (account.offeredTo || []).indexOf(side.side) !== -1; })
        });
      }
    });
    return prompts;
  }

  function takeTokenFromHash(loc, hist) {
    if (loc.hash.indexOf("#t=") !== 0) { return ""; }
    var raw = loc.hash.slice(3);
    hist.replaceState(null, "", loc.pathname + loc.search);
    try {
      return decodeURIComponent(raw).trim();
    } catch (error) {
      return "";
    }
  }

  function showTokenForm() {
    var form = document.getElementById("instance-token");
    if (!form) {
      form = element("form", "roster-form instance-token");
      form.id = "instance-token";
      var input = element("input");
      input.type = "text";
      input.autocomplete = "off";
      input.spellcheck = false;
      input.placeholder = "paste the instance token";
      var label = element("label", "field");
      label.appendChild(element("span", null, "Instance token"));
      label.appendChild(input);
      form.appendChild(label);
      var button = element("button", null, "Continue");
      button.type = "submit";
      form.appendChild(button);
      form.addEventListener("submit", function (event) {
        event.preventDefault();
        var value = input.value.trim();
        input.value = "";
        if (!value) { return; }
        instanceToken = value;
        form.hidden = true;
        loadProfiles();
      });
      document.body.insertBefore(form, document.body.firstChild);
    }
    form.hidden = false;
  }

  function loseToken() {
    instanceToken = "";
    showTokenForm();
  }

  function withBearer(headers) {
    headers.Authorization = "Bearer " + instanceToken;
    return headers;
  }

  function showToast(text, kind) {
    toast.textContent = text;
    toast.className = "toast " + (kind || "");
    toast.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toast.hidden = true; }, 6000);
  }

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) { node.className = className; }
    if (text !== undefined) { node.textContent = text; }
    return node;
  }

  // An address that may wrap only after its "@" and its dots, never mid-word.
  function wrappableEmail(address) {
    var node = element("span");
    address.split(/(?<=[@.])/).forEach(function (part, index) {
      if (index) { node.appendChild(element("wbr")); }
      node.appendChild(document.createTextNode(part));
    });
    return node;
  }

  // One row per field: a visible label wrapping its own control, so no id is
  // needed and the Add form's rows cannot collide with a card's Edit rows.
  function labeled(container, text, field) {
    var row = element("label", "field");
    row.appendChild(element("span", null, text));
    row.appendChild(field);
    container.appendChild(row);
    return row;
  }

  // The display name is what the operator recognizes and the directory is what
  // the launcher needs, and the two disagree often enough to matter, so the
  // label carries both plus whichever address the profile is signed into.
  function profileLabel(found) {
    return found.name + (found.email ? " <" + found.email + ">" : "") + " (" + found.directory + ")";
  }

  function profileSelect() {
    var select = element("select");
    var none = element("option", null, "no browser profile mapped");
    none.value = "";
    select.appendChild(none);
    var groups = {};
    profileCatalog.forEach(function (found, index) {
      if (!groups[found.browser]) {
        groups[found.browser] = element("optgroup");
        groups[found.browser].label = found.browser;
        select.appendChild(groups[found.browser]);
      }
      var option = element("option", null, profileLabel(found));
      option.value = String(index);
      groups[found.browser].appendChild(option);
    });
    // The escape hatch: a profile the enumeration never saw is still typeable.
    var other = element("option", null, "another profile (type the directory)");
    other.value = OTHER;
    select.appendChild(other);
    return select;
  }

  // The card names the profile the way the browser does when the catalog knows
  // it, with the directory kept in parentheses because that is what the
  // launcher uses; a directory the enumeration never saw is shown as itself.
  function profileCardLabel(browser, directory) {
    var index = indexOfProfile(browser, directory);
    return index >= 0 ? profileCatalog[index].name + " (" + directory + ")" : directory;
  }

  function indexOfProfile(browser, directory) {
    for (var i = 0; i < profileCatalog.length; i++) {
      if (profileCatalog[i].browser === browser && profileCatalog[i].directory === directory) { return i; }
    }
    return -1;
  }

  // The display name, browser, and profile fields, built once and used both by
  // the Add form and by each card's Edit panel, so the two can never drift
  // apart. Notes are not in here: a warning belongs on an account that already
  // exists, and the Edit panel is the only place that surfaces them.
  function accountFields(container, entry) {
    var values = entry || {};
    var alias = element("input");
    alias.type = "text";
    alias.className = "alias";
    alias.placeholder = "short display name, e.g. weekly";
    alias.value = values.alias || "";
    labeled(container, "Display name", alias);

    var browser = element("select");
    BROWSERS.forEach(function (name) {
      var option = element("option", null, name === "" ? "no browser mapped" : name);
      option.value = name;
      browser.appendChild(option);
    });
    browser.value = values.browser || "";
    labeled(container, "Browser", browser);

    var profile = profileSelect();
    labeled(container, "Browser profile", profile);

    var typed = element("input");
    typed.type = "text";
    typed.placeholder = "e.g. Profile 3";
    var typedRow = labeled(container, "Profile directory", typed);

    // An entry already mapped to a profile the enumeration found selects it; one
    // mapped to anything else falls through to the typed field, which is the only
    // place that mapping can still be seen and edited.
    var mapped = values.browserProfileDirectory
      ? indexOfProfile(values.browser || "", values.browserProfileDirectory)
      : -1;
    if (mapped >= 0) {
      profile.value = String(mapped);
    } else if (values.browserProfileDirectory) {
      profile.value = OTHER;
      typed.value = values.browserProfileDirectory;
    }

    // Set once the operator has chosen from the profile select by hand,
    // including "no browser profile mapped": from then on no auto-match may
    // replace what they chose.
    var handPicked = false;

    function sync() { typedRow.hidden = profile.value !== OTHER; }

    profile.addEventListener("change", function () {
      handPicked = true;
      // Number("") is 0, so an empty selection must not be looked up: it would
      // read as the first profile and flip the browser select to its browser.
      var found = profile.value !== "" && profile.value !== OTHER ? profileCatalog[Number(profile.value)] : null;
      // Picking a profile names its browser too; showing that in the browser
      // select keeps the two from disagreeing on the way to the server.
      if (found) { browser.value = found.browser; }
      sync();
    });
    sync();

    return {
      read: function () {
        var found = profile.value !== "" && profile.value !== OTHER ? profileCatalog[Number(profile.value)] : null;
        return {
          alias: alias.value.trim() || null,
          browser: browser.value || null,
          // The typed directory counts only while the escape hatch is chosen;
          // an empty selection means no mapping, whatever the hidden field
          // still holds from an earlier off-catalog value.
          browserProfileDirectory: found ? found.directory : (profile.value === OTHER ? (typed.value.trim() || null) : null)
        };
      },
      // Nine of ten accounts are already signed into a profile on this machine,
      // so the mapping is derivable rather than typed. Overridable: it fills an
      // empty selection or replaces its own previous guess, and never touches a
      // value the operator picked by hand, "no browser profile mapped" included.
      autoMatch: function (address) {
        if (handPicked) { return; }
        var wanted = (address || "").trim().toLowerCase();
        var index = -1;
        for (var i = 0; wanted && i < profileCatalog.length; i++) {
          if (profileCatalog[i].email === wanted) { index = i; break; }
        }
        profile.value = index < 0 ? "" : String(index);
        // Only on a hit: a miss clears the guess without undoing a browser the
        // operator chose by hand.
        if (index >= 0) { browser.value = profileCatalog[index].browser; }
        sync();
      },
      // After the form's own reset: the controls are back to their defaults but
      // this closure is not, and a hand-pick made for one account must not
      // silence auto-match for the next.
      reset: function () {
        handPicked = false;
        sync();
      }
    };
  }

  function send(path, method, body) {
    if (!instanceToken) {
      showTokenForm();
      return Promise.resolve({ ok: false, body: { error: "unauthorized" } });
    }
    var options = {
      method: method,
      headers: withBearer({ "X-Claude-Code-Account-Rotation": "1", "Accept": "application/json" })
    };
    if (body) {
      options.headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    return fetch(path, options).then(function (response) {
      if (response.status === 401) { loseToken(); }
      return response.json().then(function (payload) { return { ok: response.ok, body: payload }; });
    });
  }

  function refused(body) {
    return "Refused: " + (body.message || body.error || body.refusal || "unknown reason");
  }

  function mutate(path, method, body, onDone) {
    busy = true;
    setButtonsDisabled(true);
    return send(path, method, body)
      .then(function (result) {
        if (onDone) { return onDone(result); }
        if (!result.ok) { showToast(refused(result.body), "error"); }
        return null;
      })
      .catch(function (error) { showToast("Request failed: " + error, "error"); })
      .then(function () { busy = false; setButtonsDisabled(false); return refresh(true); });
  }

  function accountPath(email, suffix) {
    return "/api/accounts/" + encodeURIComponent(email) + (suffix || "");
  }

  function switchTo(email) {
    return mutate(accountPath(email, "/switch"), "POST", null, function (result) {
      if (!result.ok) {
        showToast(refused(result.body), "error");
        return;
      }
      var text = "Switched to " + result.body.now;
      if (result.body.parkedAs) { text += "; parked " + result.body.parkedAs; }
      if (result.body.identityMismatchWarning) { text += ". The CLI reports " + result.body.cliEmail + "; check /status."; }
      showToast(text, result.body.identityMismatchWarning ? "warn" : "ok");
    });
  }

  // The "switch now" prompt's wording, all of it here: it is to follow the
  // operator's test at the next real limit (#148).
  function promptWording(prompt, accounts) {
    function named(email) {
      var account = accounts.filter(function (candidate) { return candidate.email === email; })[0];
      return account ? uniqueName(account, accounts) : email;
    }
    var line = (prompt.side ? prompt.side + " side: " : "") + named(prompt.from) + " is at its usage limit.";
    return {
      text: prompt.to ? line : line + " No other account has headroom.",
      button: prompt.to ? "Switch now to " + named(prompt.to) : null
    };
  }

  function sidePath(side, suffix) {
    return "/api/sides/" + encodeURIComponent(side) + suffix;
  }

  function switchSide(side, email) {
    // An empty picker is "choose an account". The button is disabled in that
    // state; this refuses the hand-off if a click lands anyway.
    if (!email) { return; }
    return mutate(sidePath(side, "/accounts/" + encodeURIComponent(email) + "/switch"), "POST", null, function (result) {
      if (!result.ok) {
        showToast(refused(result.body), "error");
        return;
      }
      var text = "The " + result.body.side + " side now holds " + result.body.now;
      if (result.body.parkedAs) { text += "; parked " + result.body.parkedAs; }
      if (result.body.loggedOut) { text += "; its CLI had logged out of " + result.body.loggedOut + ", which needs a login"; }
      showToast(text, "ok");
    });
  }

  // The park-back, as one control beside Switch. It is asked for once: the
  // account is named in the confirmation because the button cannot be, the side
  // holding a different account by the time it is clicked being exactly what
  // the server's own plan re-reads and refuses.
  function releaseSide(side, holding) {
    if (!window.confirm("Hand " + holding + " back to the store from the " + side + " side?\n\nThat side keeps no login afterwards; the account is parked here and either side can take it.")) {
      return;
    }
    return mutate(sidePath(side, "/release"), "POST", null, function (result) {
      if (!result.ok) {
        // The one refusal a second click can override, in the shape the
        // "Log in again" control already uses for the same decision.
        if (result.body.refusal === "ForeignFamily"
          && window.confirm(result.body.message + "\n\nHand it back into quarantine? The family that side holds is kept there and never used, and the one in the store stays as it is.")) {
          mutate(sidePath(side, "/release") + "?quarantineForeignFamily=true", "POST", null, function (retry) {
            showToast(retry.ok
              ? "The " + retry.body.side + " side holds nothing; a superseded family was quarantined at " + retry.body.quarantinedAt
              : refused(retry.body), retry.ok ? "warn" : "error");
          });
          return;
        }
        showToast(refused(result.body), "error");
        return;
      }
      showToast(result.body.loggedOut
        ? "The " + result.body.side + " side holds nothing; its CLI had logged out of " + result.body.loggedOut + ", which needs a login"
        : "The " + result.body.side + " side holds nothing; " + (result.body.parkedAs || holding) + " is parked here", "ok");
    });
  }

  // A login the CLI on that side gave up. The slot is still that side's, so a
  // release frees it first, and the login opens on the card the release redraws.
  function loginFromSide(side, email) {
    var released = false;
    return mutate(sidePath(side, "/release"), "POST", null, function (result) {
      released = result.ok;
      if (!released) { showToast(refused(result.body), "error"); }
    }).then(function () { return released ? startLogin(email) : null; });
  }

  function sideLabel(name) {
    if (!name) { return HERE; }
    return name.length <= 3 ? name.toUpperCase() : name.charAt(0).toUpperCase() + name.slice(1);
  }

  function accountByEmail(email) {
    return lastAccounts.filter(function (candidate) { return candidate.email === email; })[0] || null;
  }

  // A button its own state disables stays disabled through a request's
  // re-enable, which runs before the redraw a failed refresh never delivers.
  function blockable(button, blocked) {
    button.disabled = busy || blocked;
    if (blocked) { button.setAttribute("data-blocked", ""); }
    return button;
  }

  // A switch the server refuses outright, as opposed to one blocked for now.
  function refusable(button, refused) {
    if (refused) { button.setAttribute("data-refused", ""); }
    return button;
  }

  // The one-click hand-over a side's strip offers, naming the account and the
  // weekly figure it was picked for.
  function takeButton(label, target, at, primary, blocked, onClick) {
    var button = actionButton("", "lg " + (primary ? "primary" : "secondary"), onClick);
    // Named in the markup, so a strip whose target changed is never kept for
    // looking the same: renderNow compares markup, not click handlers.
    button.setAttribute("data-target", target.email);
    button.appendChild(element("span", null, label));
    var weekly = target.usage.limits.filter(function (limit) { return limit.kind === "weekly_all"; })[0];
    if (weekly && weekly.known) {
      var reset = resetLine(weekly, at);
      button.appendChild(element("small", null, "7-day at " + reading(weekly) + (reset ? ", " + reset : "")));
    }
    return blockable(button, blocked);
  }

  // One strip per side: what it holds, how much room that account has left, and
  // the switch to the account it should take next. This side first.
  function nowStrip(side, account, at) {
    var name = side ? side.side : null;
    var say = account ? sentence(account, at) : null;
    var strip = element("section", "side-card");
    strip.setAttribute("aria-label", sideLabel(name));
    var head = element("div", "sh");
    var who = element("div", "who");
    var where = element("div", "where");
    where.appendChild(element("span", "dot " + (say ? say.kind : "")));
    where.appendChild(element("span", null, sideLabel(name)));
    if (side) { where.appendChild(element("span", "detail", side.detail)); }
    who.appendChild(where);
    if (account) {
      // Every strip is headed the same way, the name over the address, so the
      // strips' meters line up whether or not another account shares the name.
      who.appendChild(element("h2", null, displayName(account)));
      var address = element("div", "em");
      address.appendChild(wrappableEmail(account.email));
      if (account.roster && account.roster.ciTokenGeneratedOn) { address.appendChild(element("span", "tag", "CI token")); }
      who.appendChild(address);
    } else {
      // An offline read leaves the address null too, and that side may still
      // hold an account, so only an answering side is said to hold nothing.
      who.appendChild(element("h2", "empty", side && !side.online ? "Not answering" : (side && side.liveAccount) || "Holding nothing"));
    }
    head.appendChild(who);
    if (account) {
      var room = headroom(account);
      var figure = element("div", "hr");
      figure.appendChild(element("span", "n " + (room === null ? "" : tier(100 - room)), room === null ? "–" : room + "%"));
      figure.appendChild(element("span", null, "headroom"));
      head.appendChild(figure);
    }
    strip.appendChild(head);

    if (account) {
      strip.appendChild(element("p", "say " + say.kind, say.text));
      if (account.cliLoggedOut) { strip.appendChild(element("p", "cli-logout", account.cliLoggedOut)); }
      var meters = element("div", "mt");
      account.usage.limits.forEach(function (limit) {
        if (limit.known || limit.kind === "session" || limit.kind === "weekly_all") { meters.appendChild(meter(limit, at)); }
      });
      strip.appendChild(meters);
    }

    var go = element("div", "go");
    var blocked = !!lastDashboard.banner;
    // The #148 "switch now" prompt, when this side's account is at a limit. It
    // names the same account the recommended button would, so it takes that
    // button's place rather than sitting beside it.
    var prompt = switchPrompts(lastAccounts, lastSides).filter(function (candidate) { return candidate.side === name; })[0];
    var wording = prompt ? promptWording(prompt, lastAccounts) : null;
    if (wording) { strip.appendChild(element("p", "prompt", wording.text)); }
    var urgent = !!wording || (!!say && say.kind === "crit");
    if (!side) {
      var here = recommended(lastAccounts, function (candidate) { return candidate.canSwitchHere; });
      if (here) {
        go.appendChild(takeButton(wording ? wording.button : "Switch to " + uniqueName(here, lastAccounts), here, at, urgent,
          switchBlocked(here, lastDashboard, false), function () { switchTo(here.email); }));
      }
    } else if (side.online) {
      if (account && account.roster && account.loggedOutOn === side.side) {
        go.appendChild(actionButton("Log in again on " + sideLabel(name), "primary lg", function () { loginFromSide(side.side, account.email); }));
      }
      var there = recommended(lastAccounts, function (candidate) { return (candidate.offeredTo || []).indexOf(side.side) !== -1; });
      if (there) {
        go.appendChild(takeButton(wording ? wording.button : "Switch " + sideLabel(name) + " to " + uniqueName(there, lastAccounts), there, at, urgent,
          blocked, function () { switchSide(side.side, there.email); }));
      }
      // Read at click time, and re-read by the server before anything moves.
      if (side.liveAccount) {
        go.appendChild(blockable(actionButton("Hand back", "quiet", function () { releaseSide(side.side, side.liveAccount); }), false));
      }
    } else if (side.canStart) {
      go.appendChild(blockable(actionButton("Start " + sideLabel(name) + " side", "secondary", function () { mutate(sidePath(side.side, "/start"), "POST", null, null); }), false));
    }
    if (go.children.length) { strip.appendChild(go); }
    return strip;
  }

  function renderNow() {
    if (!lastDashboard) { return; }
    var at = new Date(lastDashboard.capturedAt).getTime();
    var strips = [nowStrip(null, lastAccounts.filter(function (account) { return account.isLive; })[0] || null, at)];
    lastSides.forEach(function (side) {
      strips.push(nowStrip(side, side.liveAccount ? accountByEmail(side.liveAccount) : null, at));
    });
    // A strip is replaced only when it changed, so a poll that says the same
    // thing never takes a button from under the pointer or the focus.
    strips.forEach(function (strip, index) {
      var old = nowLine.children[index];
      if (!old) {
        nowLine.appendChild(strip);
      } else if (old.outerHTML !== strip.outerHTML) {
        nowLine.replaceChild(strip, old);
      }
    });
    while (nowLine.children.length > strips.length) { nowLine.removeChild(nowLine.lastChild); }
  }

  function setPaused(email, paused) {
    return mutate(accountPath(email), "PATCH", { paused: paused }, function (result) {
      showToast(result.ok ? (paused ? email + " is out of the rotation" : email + " is back in the rotation") : refused(result.body), result.ok ? "ok" : "error");
    });
  }

  function adopt(email) {
    return mutate(accountPath(email, "/adopt-live"), "POST", null, function (result) {
      showToast(result.ok ? email + " is on the roster" : refused(result.body), result.ok ? "ok" : "error");
    });
  }

  function remove(email) {
    if (!window.confirm("Remove " + email + "? Its login is revoked and its profile folder is deleted.")) {
      return Promise.resolve();
    }
    return mutate(accountPath(email), "DELETE", null, function (result) {
      if (result.ok) {
        showToast(email + " removed" + (result.body.warning ? ". " + result.body.warning : " and logged out"), result.body.warning ? "warn" : "ok");
        return null;
      }
      // The delete is refused rather than stranding a token the tool believes it
      // revoked. Deleting anyway is the operator's call, and it is said plainly.
      if (result.body.refusal === "LogoutFailed" && window.confirm(result.body.message + "\n\nDelete the folder anyway, without revoking?")) {
        return send(accountPath(email) + "?logout=false", "DELETE", null).then(function (forced) {
          showToast(forced.ok ? email + " removed. " + forced.body.warning : refused(forced.body), forced.ok ? "warn" : "error");
        });
      }
      showToast(refused(result.body), "error");
      return null;
    });
  }

  // Login is not routed through mutate: mutate ends in a forced render, which
  // rebuilds every card and would throw away the panel this just opened.
  function startLogin(email) {
    busy = true;
    setButtonsDisabled(true);
    return send(accountPath(email, "/login"), "POST", null)
      .then(function (result) {
        if (result.ok) {
          openLoginPanel(email, result.body);
          return null;
        }
        // The escape hatch, and the one click in this page that makes an
        // account a second token family. Offered only on this refusal, which
        // the server raises only while the other side is unreachable, and only
        // after the operator reads what it costs.
        if (result.body.refusal === "HeldByOtherSide"
          && window.confirm(result.body.message + "\n\nLog in again here anyway? That side keeps the login it has, so " + email + " will have two. The one it keeps is quarantined when that side is switched off the account.")) {
          return send(accountPath(email, "/login") + "?supersede=true", "POST", null).then(function (forced) {
            if (forced.ok) {
              openLoginPanel(email, forced.body);
            } else {
              showToast(refused(forced.body), "error");
            }
          });
        }
        showToast(refused(result.body), "error");
        return null;
      })
      .catch(function (error) { showToast("Request failed: " + error, "error"); })
      .then(function () { busy = false; setButtonsDisabled(false); });
  }

  function openLoginPanel(email, session) {
    var card = cardNodes[email];
    if (!card) { return; }

    var panel = element("details", "login");
    panel.open = true;
    panel.appendChild(element("summary", null, "Signing " + email + " in"));

    var link = element("a", null, "Open the sign-in page");
    link.href = session.signInUrl;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
    panel.appendChild(link);

    if (session.browserError) {
      panel.appendChild(element("p", "muted", "The mapped browser did not open: " + session.browserError + " Use the link above."));
    }

    var status = element("p", "muted", "Sign in, then paste the code that page shows. This login expires in ten minutes.");
    panel.appendChild(status);

    var form = element("form", "roster-form");
    var code = element("input");
    code.type = "text";
    code.autocomplete = "off";
    code.placeholder = "paste the code here";
    var submit = element("button", null, "Submit code");
    submit.type = "submit";
    form.appendChild(code);
    form.appendChild(submit);
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      submitCode(session.id, email, code, status, panel);
    });
    panel.appendChild(form);

    card.appendChild(panel);
    code.focus();
  }

  function submitCode(id, email, field, status, panel) {
    var code = field.value.trim();
    if (!code) { return Promise.resolve(); }
    busy = true;
    setButtonsDisabled(true);
    status.textContent = "Checking that code...";
    // The code travels in the body. It is never part of a path, and the reply
    // never carries it back.
    return send("/api/login-sessions/" + encodeURIComponent(id) + "/code", "POST", { code: code })
      .then(function (result) {
        field.value = "";
        if (!result.ok) {
          status.textContent = refused(result.body);
          return null;
        }
        if (result.body.state !== "Completed") {
          // A rejected code leaves the session open, so the field stays for another try.
          status.textContent = result.body.message || "Still waiting on the sign-in page.";
          return null;
        }
        panel.parentNode.removeChild(panel);
        showToast(result.body.message || (email + " is logged in"), "ok");
        return refresh(true);
      })
      .catch(function (error) { status.textContent = "Request failed: " + error; })
      .then(function () { busy = false; setButtonsDisabled(false); });
  }

  function editPanel(account, offerLogin) {
    var panel = element("details", "edit");
    panel.appendChild(element("summary", null, "Edit"));
    var form = element("form", "roster-form");
    var fields = accountFields(form, account.roster);
    // A warning is a note, not a name. The heading stays a short display name
    // because this field is where that sentence goes.
    var notes = element("textarea");
    notes.rows = 3;
    notes.value = (account.roster && account.roster.notes) || "";
    notes.placeholder = "warnings belong here, not in the name";
    labeled(form, "Notes", notes);
    // One account at a time: setting this clears it on whichever other card
    // carried it, the way adopting a live account elsewhere already leaves only
    // one seat live. The date is the operator's own record of when they ran
    // `claude setup-token`; nothing here reads GitHub or the token itself.
    var ciToken = element("input");
    ciToken.type = "date";
    ciToken.value = (account.roster && account.roster.ciTokenGeneratedOn) || "";
    labeled(form, "CI token generated on (this account backs CLAUDE_CODE_OAUTH_TOKEN)", ciToken);
    var save = element("button", null, "Save");
    save.type = "submit";
    form.appendChild(save);
    // A card that does not need a login still lets the operator force one; here
    // it is reachable without inviting a click from the card's face.
    if (offerLogin) {
      form.appendChild(actionButton("Log in again", "secondary", function () { startLogin(account.email); }));
    }
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      panel.open = false;
      var body = fields.read();
      // A present key can clear the field. Sending notes on every save, including
      // a blank one, is what lets the operator take a warning back off the card.
      body.notes = notes.value.trim() || null;
      body.ciTokenGeneratedOn = ciToken.value || null;
      mutate(accountPath(account.email), "PATCH", body, function (result) {
        showToast(result.ok ? account.email + " updated" : refused(result.body), result.ok ? "ok" : "error");
      });
    });
    panel.appendChild(form);
    return panel;
  }

  function actionButton(label, className, onClick) {
    var button = element("button", className, label);
    button.type = "button";
    button.addEventListener("click", onClick);
    return button;
  }

  // Every countdown on the page is measured from the instant the payload was
  // taken rather than from the browser's clock, so a card's numbers and the
  // time until its window resets are read off the same moment.
  function secondsUntil(instant, from) {
    return Math.max(0, Math.round((new Date(instant).getTime() - from) / 1000));
  }

  // The one bucket ladder every distance on the page is said in, bare of any
  // affix, so a countdown and an age cannot drift onto different thresholds.
  function span(seconds) {
    if (seconds < 60) { return seconds + " s"; }
    var minutes = Math.round(seconds / 60);
    if (minutes < 60) { return minutes + " min"; }
    var hours = Math.floor(minutes / 60);
    return hours < 48 ? hours + " h " + (minutes % 60) + " min" : Math.round(hours / 24) + " d";
  }

  function relative(instant, from) {
    return "in " + span(secondsUntil(instant, from));
  }

  // An age in the buckets a countdown uses, said the other way round, so the
  // operator reads "logged in 12 d ago" against "login expires in 16 d" without
  // translating between two shapes.
  function ago(instant, from) {
    return span(Math.max(0, Math.round((from - new Date(instant).getTime()) / 1000))) + " ago";
  }

  // One reading of the refresh token's life, shared by the chip, the Switch
  // guard, the standing line, and the expiry line, measured from the payload's
  // own instant like every other countdown on the page.
  function loginExpired(account, at) {
    return !!account.loginExpiresAt && new Date(account.loginExpiresAt).getTime() <= at;
  }

  function loginSoon(account, at) {
    return !!account.loginExpiresAt && secondsUntil(account.loginExpiresAt, at) <= LOGIN_WARN_SECONDS;
  }

  // How old the login is and how long it has left, in one line: either half is
  // dropped with its instant, and the line itself when neither is known.
  function loginLine(account, at) {
    var parts = [];
    if (account.loggedInAt) { parts.push("logged in " + ago(account.loggedInAt, at)); }
    if (account.loginExpiresAt) {
      parts.push(loginExpired(account, at) ? "login expired" : "login expires " + relative(account.loginExpiresAt, at));
    }
    return parts.length ? parts.join(" \u00b7 ") : null;
  }

  // The credential axis in one word: whether the operator can switch to this
  // account at all. The standing line under the usage rows is the quota axis,
  // so a card reading "ready" with "usable in 2 h" beneath it states two facts.
  function stateChip(account, at) {
    // Ahead of "live": a logged-out card is a blocking state, and the green
    // live chip would read as a healthy session.
    if (account.cliLoggedOut) { return "logged out"; }
    if (account.isLive) { return "live"; }
    if (account.roster && account.roster.paused) { return "paused"; }
    // A slot the other side holds is empty on purpose, so its emptiness is not
    // a missing login and must not be read as one: the store chip beside this
    // one says where that account's pair actually is, and the Login button is
    // already withheld for the same reason.
    if (account.heldAway) { return null; }
    if (!account.hasCredentials) { return "needs login"; }
    if (loginExpired(account, at)) { return "login expired"; }
    if (account.refresh.state === "stranded") { return "error"; }
    return "ready";
  }

  // A figure whose age and origin go unsaid is exactly what this card exists to
  // avoid, so the two travel together wherever a source is named. A time of day
  // alone reads as today: a figure cached before midnight, or a card nobody has
  // refreshed since last week, would look hours old instead of days, which is
  // the exact deceit this line exists to prevent. Same day, the time; any other
  // day, the date with it.
  function asOf(capturedAt, source, from) {
    var taken = new Date(capturedAt);
    var sameDay = taken.toDateString() === new Date(from).toDateString();
    return "as of " + (sameDay ? taken.toLocaleTimeString() : taken.toLocaleString()) + " via " + source;
  }

  // Unknown first: no source carried this bucket at all. Then a window that has
  // reset since its capture, whose percentage now measures nothing.
  function reading(limit) {
    if (!limit.known) { return "unknown"; }
    if (limit.windowReset) { return "window reset since last read"; }
    return limit.percent === null ? "unknown" : Math.round(limit.percent) + "%";
  }

  function barWidth(limit) {
    if (!limit.known || limit.windowReset || limit.percent === null) { return 0; }
    return Math.max(0, Math.min(100, limit.percent));
  }

  function creditsLine(credits) {
    var text = "usage credits: " + (credits.enabled
      ? "enabled"
      : "disabled" + (credits.disabledReason ? " (" + credits.disabledReason + ")" : ""));
    return credits.spendLimitReached ? text + "; spend limit reached" : text;
  }

  // What the card says about its last refresh. "idle" and "read" say nothing: a
  // card showing numbers with an "as of" line has already said it. While a pass
  // runs, a card that has never reported an outcome is waiting its turn; the
  // payload carries no per-outcome timestamp, so a card that already has one
  // keeps showing it rather than claiming to be in this pass.
  function refreshState(account, dashboard) {
    var state = account.refresh.state;
    if (dashboard.refresh.inProgress && state === "idle") { return "refreshing..."; }
    if (state === "idle" || state === "read") { return null; }
    return account.refresh.message || state;
  }

  // Colour carries meaning: healthy, near the limit, at it. The server's own
  // severity, when a source sent one, can only raise the tone.
  function meterTone(limit) {
    if (!limit.known || limit.windowReset || limit.percent === null) { return ""; }
    return tier(limit.percent);
  }

  // The statusline's four tiers for both rate-limit windows, so the page and
  // the terminal colour one figure alike: red from 90%, orange from 75%,
  // yellow from 50%, green below (~/.claude/statusline/lib/format.sh:31-33).
  function tier(percent) {
    return percent >= 90 ? "crit" : percent >= 75 ? "high" : percent >= 50 ? "warn" : "ok";
  }

  function bar(limit) {
    var track = element("div", "bar");
    var fill = element("i", meterTone(limit));
    fill.style.width = barWidth(limit) + "%";
    track.appendChild(fill);
    return track;
  }

  // A window that has already reset has no reset to count down to: "resets in
  // 0 s" beside "window reset since last read" is the same stale figure said twice.
  function resetLine(limit, at) {
    return limit.resetsAt && !limit.windowReset ? "resets " + relative(limit.resetsAt, at) : null;
  }

  // One window: its label and reading, the bar, when it resets, and, only for a
  // row taken from another source than the account's own, where it came from.
  function meter(limit, at) {
    var box = element("div", "meter");
    var line = element("div", "l");
    line.appendChild(element("span", "label", limit.label));
    line.appendChild(element("span", "pct " + meterTone(limit), reading(limit)));
    box.appendChild(line);
    box.appendChild(bar(limit));
    var reset = resetLine(limit, at);
    if (reset) { box.appendChild(element("small", null, reset)); }
    if (limit.source) { box.appendChild(element("small", null, asOf(limit.capturedAt, limit.source, at))); }
    return box;
  }

  // The account's state in one sentence, worst first, and the tone it is said
  // in. The credential facts come before the quota: a card whose login has
  // expired is not "usable now" whatever its figures say.
  function sentence(account, at) {
    if (account.cliLoggedOut) {
      return { kind: "crit", text: "Logged out" + (account.loggedOutOn ? " on " + sideLabel(account.loggedOutOn) : "") };
    }
    var chip = stateChip(account, at);
    if (chip === "login expired") { return { kind: "crit", text: "Login expired" }; }
    if (chip === "error") { return { kind: "crit", text: account.refresh.message || "Refresh failed" }; }
    if (chip === "needs login") { return { kind: "warn", text: "Needs login" }; }
    // Exhausted is the 7-day window spent, and nothing else; a spent 5-hour
    // window is a pause of hours with its own reset (Q34).
    if (account.standing === "exhausted") {
      return { kind: "crit", text: account.nextResetAt ? "Exhausted, usable " + relative(account.nextResetAt, at) : "Exhausted" };
    }
    if (account.standing === "limited") {
      return { kind: "crit", text: "5-hour limit reached" + (account.nextResetAt ? ", resets " + relative(account.nextResetAt, at) : "") };
    }
    if (chip === "paused" || account.standing === "paused") { return { kind: "paused", text: "Paused" }; }
    if (account.standing === "unread") { return { kind: "", text: "No usage read yet" }; }
    var near = nearestLimit(account.usage.limits);
    if (near) {
      var reset = resetLine(near, at);
      return { kind: meterTone(near), text: "Near the " + near.label + " limit" + (reset ? ", " + reset : "") };
    }
    return { kind: "ok", text: "Usable now" };
  }

  // The window the "near the limit" sentence names: the fuller of the two at
  // orange or red. A tie goes to the 7-day, whose reset is the later one.
  function nearestLimit(limits) {
    return limits.filter(function (limit) {
      var tone = meterTone(limit);
      return (limit.kind === "session" || limit.kind === "weekly_all") && (tone === "crit" || tone === "high");
    }).reduce(function (worst, limit) {
      return !worst || limit.percent > worst.percent || (limit.percent === worst.percent && limit.kind === "weekly_all") ? limit : worst;
    }, null);
  }

  // Everything else a row knows, in small lines under its sentence: where the
  // figures came from and when, the credits, the refresh outcome, the login's
  // age and expiry, and the browser it opens in.
  function statusLines(account, dashboard, at) {
    var lines = element("div", "lines");
    if (account.cliLoggedOut) { lines.appendChild(element("p", "cli-logout", account.cliLoggedOut)); }
    if (account.usage.source) { lines.appendChild(element("p", "asof", asOf(account.usage.capturedAt, account.usage.source, at))); }
    // The row's two columns are the 5-hour and 7-day windows; any other window
    // the endpoint reported, and a column taken from another source, say so here.
    account.usage.limits.forEach(function (limit) {
      var column = limit.kind === "session" || limit.kind === "weekly_all";
      if (!column && limit.known) {
        var reset = resetLine(limit, at);
        lines.appendChild(element("p", "asof", limit.label + " " + reading(limit) + (reset ? ", " + reset : "")));
      }
      if (limit.source) { lines.appendChild(element("p", "asof", limit.label + " " + asOf(limit.capturedAt, limit.source, at))); }
    });
    if (account.usage.credits) { lines.appendChild(element("p", "asof", creditsLine(account.usage.credits))); }
    if (account.usageNote) { lines.appendChild(element("p", "muted", account.usageNote)); }
    var state = refreshState(account, dashboard);
    if (state) { lines.appendChild(element("p", "refresh-state", state)); }
    var login = loginLine(account, at);
    if (login) {
      var line = element("p", "login-expiry" + (loginSoon(account, at) ? " warn" : ""), login);
      // new Date(null) is the 1970 epoch, which would date every card without an
      // expiry to a lie, so the absolute instant is offered only when there is one.
      if (account.loginExpiresAt) { line.title = new Date(account.loginExpiresAt).toLocaleString(); }
      lines.appendChild(line);
    }
    var roster = account.roster;
    if (roster && roster.browser) {
      var browser = roster.browser + (roster.browserProfileDirectory ? " / " + profileCardLabel(roster.browser, roster.browserProfileDirectory) : "");
      // One line, cut short with the whole of it on hover.
      lines.appendChild(element("p", "muted browser", browser)).title = browser;
    }
    return lines;
  }

  // One window in a row: the bar, the reading, and when it resets.
  function cell(limit, at) {
    var box = element("div", "u");
    if (!limit) { return box; }
    // The column header is hidden on narrow screens and from screen readers,
    // so each cell names its own window.
    box.appendChild(element("span", "cell-label", limit.label));
    box.appendChild(bar(limit));
    box.appendChild(element("span", "pct " + meterTone(limit), reading(limit)));
    var reset = resetLine(limit, at);
    if (reset) { box.appendChild(element("small", null, reset)); }
    return box;
  }

  // The pass as a whole, in one line above the cards.
  function passState(dashboard) {
    if (dashboard.refresh.inProgress) { return "refreshing all accounts..."; }
    if (dashboard.refresh.lockedUntil) {
      return "rate limited, retry in " + secondsUntil(dashboard.refresh.lockedUntil, new Date(dashboard.capturedAt).getTime()) + " s";
    }
    return dashboard.refresh.summary || "";
  }

  // Both refresh routes answer 202 and leave the pass to the background worker;
  // the ten-second poll is what shows it landing, card by card.
  function started(result) {
    showToast(result.ok ? "Refresh started" : refused(result.body), result.ok ? "ok" : "error");
  }

  function refreshAccount(email) {
    return mutate(accountPath(email, "/refresh"), "POST", null, started);
  }

  // The part of the address an operator can say out loud. It is a heading, not
  // an identity: the full address stays on the line under it.
  function localPart(email) {
    var at = email.indexOf("@");
    return at > 0 ? email.slice(0, at) : email;
  }

  // The name the card leads with. An alias is a display name; without one the
  // local part stands in so the heading is never empty.
  function displayName(account) {
    var alias = account.roster && account.roster.alias;
    return alias || localPart(account.email);
  }

  // The name for a place that shows no address beside it: a switch button, the
  // prompt, a strip's heading. Two accounts can share a local part across
  // providers, so a name another account also answers to gives way to the address.
  function uniqueName(account, accounts) {
    var name = displayName(account);
    var shared = accounts.some(function (other) { return other.email !== account.email && displayName(other).toLowerCase() === name.toLowerCase(); });
    return shared ? account.email : name;
  }

  function renameLabel(account, accounts) {
    return "Rename " + uniqueName(account, accounts);
  }

  // Alias only. A missing key leaves notes, the browser, and pause alone, which
  // is what makes typing a name safe while the Edit panel holds a warning.
  function renameAccount(account, alias) {
    return mutate(accountPath(account.email), "PATCH", { alias: alias }, function (result) {
      showToast(result.ok ? account.email + " renamed" : refused(result.body), result.ok ? "ok" : "error");
    });
  }

  function showDisplayName(heading, account) {
    heading.textContent = "";
    if (!account.roster) {
      heading.appendChild(document.createTextNode(displayName(account)));
      return;
    }
    var button = element("button", "rename");
    button.type = "button";
    button.title = "Rename this account";
    button.setAttribute("aria-label", renameLabel(account, lastAccounts));
    button.appendChild(element("span", "display-name", displayName(account)));
    button.appendChild(element("span", "rename-label", "Rename"));
    button.addEventListener("click", function () {
      // The Edit panel is already changing this account. Focus its display-name
      // field instead of opening a second editor that a save would throw away.
      var aliasField = heading.closest(".card") && heading.closest(".card").querySelector("details.edit[open] input.alias");
      if (aliasField) {
        aliasField.focus();
        if (aliasField.select) { aliasField.select(); }
        return;
      }
      beginRename(heading, account);
    });
    heading.appendChild(button);
  }

  function beginRename(heading, account) {
    var input = element("input", "rename");
    input.type = "text";
    input.value = (account.roster && account.roster.alias) || "";
    input.placeholder = localPart(account.email);
    input.setAttribute("aria-label", "Display name");
    input.autocomplete = "off";
    heading.textContent = "";
    heading.appendChild(input);
    input.focus();
    if (input.select) { input.select(); }
    var settled = false;
    // pointerdown is before blur, which is before click. Remember what was
    // pressed so the save can get out of that click's way.
    var pressed = null;
    function onPointerDown(event) { pressed = event.target; }
    document.addEventListener("pointerdown", onPointerDown, true);
    function stopWatching() {
      document.removeEventListener("pointerdown", onPointerDown, true);
    }
    function typedAlias() {
      return input.value.trim() || null;
    }
    function finish(save) {
      if (settled) { return; }
      settled = true;
      stopWatching();
      var alias = typedAlias();
      var current = (account.roster && account.roster.alias) || null;
      if (!save || alias === current) {
        showDisplayName(heading, account);
        return;
      }
      renameAccount(account, alias);
    }
    // The Edit panel is the thing that was opened. mutate() would refresh and
    // rebuild the card, which closes that panel and drops whatever was typed.
    function keepEditOpen(panel) {
      if (settled) { return; }
      settled = true;
      stopWatching();
      var alias = typedAlias();
      var current = (account.roster && account.roster.alias) || null;
      var field = panel.querySelector("input.alias");
      if (field) { field.value = alias || ""; }
      showDisplayName(heading, account);
      if (alias === current) { return; }
      if (account.roster) { account.roster.alias = alias; }
      send(accountPath(account.email), "PATCH", { alias: alias }).then(function (result) {
        showToast(result.ok ? account.email + " renamed" : refused(result.body), result.ok ? "ok" : "error");
        if (!result.ok && account.roster) {
          account.roster.alias = current;
          showDisplayName(heading, account);
        }
      });
    }
    input.addEventListener("keydown", function (event) {
      if (event.key === "Escape") {
        event.preventDefault();
        finish(false);
      } else if (event.key === "Enter") {
        event.preventDefault();
        finish(true);
      }
    });
    input.addEventListener("blur", function () {
      if (settled) { return; }
      var target = pressed;
      pressed = null;
      var edit = target && target.closest && target.closest("details.edit");
      if (edit) {
        keepEditOpen(edit);
        return;
      }
      // A button pressed to leave the field is still enabled here. mutate()
      // disables every button synchronously, so the save waits until that
      // click has been dispatched.
      var armed = target && target.closest && target.closest("button, summary, a, input, select, textarea");
      if (armed) {
        setTimeout(function () { finish(true); }, 0);
        return;
      }
      finish(true);
    });
  }

  function cardHeading(account) {
    var heading = element("h2");
    showDisplayName(heading, account);
    return heading;
  }

  // Whether the arriving payload would move a card, which is the only thing the
  // guard below defers for.
  function reordered(arriving) {
    return arriving.length !== renderedOrder.length
      || arriving.some(function (email, index) { return email !== renderedOrder[index]; });
  }

  // A row's overflow menu item: the menu closes before the action runs, so the
  // redraw that follows is not held back by an open menu.
  function menuItem(more, label, className, onClick) {
    return actionButton(label, className, function () {
      more.open = false;
      onClick();
    });
  }

  // The row's switch buttons, one per side: each hands that side this account in
  // one click, and a side already holding it says so instead. A button the
  // server's verdict refuses is hidden rather than shown dead; one the banner
  // blocks stays in view, disabled, so the row still says what it offers.
  function switchButtons(account, dashboard) {
    var seg = element("span", "seg");
    var name = uniqueName(account, lastAccounts);
    if (account.isLive && !account.cliLoggedOut) {
      seg.appendChild(element("span", "live", "Live on " + HERE.toLowerCase()));
    } else {
      // Whether this account can come live on this side is the server's verdict,
      // which is where the slot, the strand and the expiry are all known: a
      // paused account can still be switched to by hand, a stranded or expired
      // one cannot, and neither can one whose pair the other side is holding.
      // What is added here is only what the browser knows: a request in flight
      // and a banner.
      var here = actionButton(HERE, "secondary sm", function () { switchTo(account.email); });
      here.setAttribute("aria-label", "Switch " + HERE.toLowerCase() + " to " + name);
      seg.appendChild(refusable(blockable(here, switchBlocked(account, dashboard, false)), !account.canSwitchHere));
    }
    lastSides.forEach(function (side) {
      if (side.liveAccount === account.email) {
        seg.appendChild(element("span", "live", "Live on " + sideLabel(side.side)));
        return;
      }
      var there = actionButton(sideLabel(side.side), "secondary sm", function () { switchSide(side.side, account.email); });
      there.setAttribute("aria-label", "Switch " + sideLabel(side.side) + " to " + name);
      var refused = (account.offeredTo || []).indexOf(side.side) === -1;
      seg.appendChild(refusable(blockable(there, refused || !!dashboard.banner), refused));
    });
    return seg;
  }

  function row(account, dashboard, at, next) {
    var roster = account.roster;
    var paused = !!(roster && roster.paused);
    var say = sentence(account, at);
    var tone = say.kind === "ok" && loginSoon(account, at) ? "warn" : say.kind;
    var card = element("section", "card" + (account.isLive && !account.cliLoggedOut ? " live" : "") + (account.cliLoggedOut ? " logged-out" : "") + (paused ? " paused" : ""));
    var line = element("div", "row");

    var who = element("div", "nm");
    who.appendChild(element("span", "dot " + tone));
    var title = element("div", "title");
    title.appendChild(cardHeading(account));
    var badges = element("span", "badges");
    if (next) { badges.appendChild(element("span", "badge next", "Next up")); }
    // Two axes, and a card shows the second only when it adds something: the
    // store chip says where this account's one pair is, and the credential
    // chip says whether this side can switch to it. "live here" and "parked"
    // already carry "live" and "ready", so those two are not said twice. The
    // class is fixed rather than derived from the text, since a chip can
    // carry "(offline)".
    var chip = stateChip(account, at);
    if (account.chip) { badges.appendChild(element("span", "badge store", account.chip)); }
    if (chip && (!account.chip || (chip !== "live" && chip !== "ready"))) {
      badges.appendChild(element("span", "badge " + chip.replace(/ /g, "-"), chip));
    }
    if (!roster) { badges.appendChild(element("span", "badge off-roster", "not on roster")); }
    if (roster && roster.ciTokenGeneratedOn) {
      badges.appendChild(element("span", "badge ci-token", "CI token · " + roster.ciTokenGeneratedOn));
    }
    title.appendChild(badges);
    who.appendChild(title);
    // The heading is the display name, or the local part when the account has
    // no alias. The address is always its own line under that: the card-layout
    // record hid it only when the heading was the address itself, and a local
    // part is not the address, so this line is what keeps the account identifiable.
    who.appendChild(element("p", "address")).appendChild(wrappableEmail(account.email));
    line.appendChild(who);

    var status = element("div", "st");
    status.appendChild(element("p", "say " + say.kind, say.text));
    status.appendChild(statusLines(account, dashboard, at));
    line.appendChild(status);

    function limitOf(kind) { return account.usage.limits.filter(function (limit) { return limit.kind === kind; })[0]; }
    line.appendChild(cell(limitOf("session"), at));
    line.appendChild(cell(limitOf("weekly_all"), at));

    var act = element("div", "act");
    // The live account is signed in already; logging it into its parked folder
    // would leave one account holding two logins. A card with a login good for
    // longer than the warning window does not need the button on its face; an
    // expired login is inside that window, so the one test covers both.
    // A slot the other side holds is empty for a reason, and logging into it
    // would put a second token family on the machine. The route refuses it
    // anyway; the button goes so the operator is not sent at a 409.
    var needsLogin = !account.heldAway && (!account.hasCredentials || loginSoon(account, at));
    if (roster && !account.isLive && needsLogin) {
      act.appendChild(actionButton(account.hasCredentials ? "Log in again" : "Login", "primary sm", function () { startLogin(account.email); }));
    }
    if (roster && account.loggedOutOn) {
      act.appendChild(actionButton("Log in again", "primary sm", function () { loginFromSide(account.loggedOutOn, account.email); }));
    }
    if (!roster && account.isLive) {
      act.appendChild(actionButton("Adopt", "secondary sm", function () { adopt(account.email); }));
    }
    act.appendChild(switchButtons(account, dashboard));

    var more = element("details", "more");
    var summary = element("summary", "quiet sm", "···");
    summary.setAttribute("aria-label", "More actions for " + uniqueName(account, lastAccounts));
    more.appendChild(summary);
    var menu = element("div", "menu");
    var edit = roster ? editPanel(account, !account.isLive && !needsLogin) : null;
    if (edit) {
      menu.appendChild(menuItem(more, "Edit", "quiet sm", function () {
        edit.open = true;
        var first = edit.querySelector("input, select, textarea");
        if (first) { first.focus(); }
      }));
    }
    menu.appendChild(blockable(menuItem(more, "Refresh usage", "quiet sm", function () { refreshAccount(account.email); }), false));
    if (roster) {
      menu.appendChild(menuItem(more, paused ? "Resume rotation" : "Pause rotation", "quiet sm", function () { setPaused(account.email, !paused); }));
    }
    // Remove revokes the login and deletes the folder, so it sits apart, under
    // a rule, and never beside Switch.
    if (!account.isLive) {
      menu.appendChild(element("hr"));
      // Removing a slot the other side holds would skip the logout it cannot
      // reach and delete the record that says the pair exists at all. The
      // route refuses it; the button goes with it.
      menu.appendChild(blockable(menuItem(more, "Remove account", "danger sm", function () { remove(account.email); }), account.heldAway));
    }
    more.appendChild(menu);
    act.appendChild(more);
    line.appendChild(act);

    card.appendChild(line);
    if (edit) { card.appendChild(edit); }
    return card;
  }

  function render(dashboard, force) {
    // The header, the Now strips, the banner, the setup sentence, and the
    // warnings are not part of any row, so they are written before the guards
    // below and on every poll: an operator with an Edit panel, a half-typed
    // login code, or a display name open would otherwise watch the page's own
    // timestamp, the pass's progress, the Refresh all button, and a banner or
    // setup sentence that has since been cleared freeze at whatever they said
    // when the field opened. The setup sentence disables nothing; Switch still
    // follows canSwitchHere, a request in flight, and the reconciliation banner.
    lastDashboard = dashboard;
    lastAccounts = dashboard.accounts;
    captured.textContent = "Updated " + new Date(dashboard.capturedAt).toLocaleTimeString();
    refreshStateLine.textContent = passState(dashboard);
    refreshAllButton.disabled = busy;
    banner.hidden = !dashboard.banner;
    banner.replaceChildren();
    if (dashboard.banner) {
      banner.appendChild(element("b", null, "Switching is off"));
      banner.appendChild(element("span", null, dashboard.banner));
    }
    setup.hidden = !dashboard.setup;
    setup.textContent = dashboard.setup || "";
    warnings.innerHTML = "";
    warnings.hidden = dashboard.warnings.length === 0;
    dashboard.warnings.forEach(function (warning) { warnings.appendChild(element("li", null, warning)); });
    renderNow();
    // Rendering rebuilds every row, so the ten-second poll would otherwise wipe an
    // Edit panel, a half-typed login code, a display name being typed, or an open
    // row menu, out from under whoever is using it. A mutation's own render
    // passes force, since that one has to show the result.
    if (!force && cards.querySelector("details.edit[open], details.login[open], input.rename, details.more[open]")) { return; }
    // The hazard is a row moving, not a row being redrawn. Switch fires without a
    // confirm, so an order that changes while the pointer rests on the list, or
    // while a button inside it holds focus, sends the operator to whichever account
    // slid under the aim; the pointer resting there is the mouse's ordinary state,
    // so deferring on it alone would freeze the rows for as long as an operator
    // left the cursor on them. An order identical to the one on screen moves no
    // row, so it is rendered in place and a Refresh all pass keeps landing row by
    // row under the pointer.
    var arriving = dashboard.accounts.map(function (account) { return account.email; });
    if (!force && reordered(arriving) && (cards.matches(":hover") || cards.contains(document.activeElement))) { return; }

    var at = new Date(dashboard.capturedAt).getTime();
    var next = recommended(dashboard.accounts, function (account) {
      return account.canSwitchHere || (account.offeredTo || []).length > 0;
    });
    cards.innerHTML = "";
    cardNodes = {};
    renderedOrder = arriving;
    renderedSides = sideNames();
    dashboard.accounts.forEach(function (account) {
      var card = row(account, dashboard, at, account === next);
      cardNodes[account.email] = card;
      cards.appendChild(card);
    });
  }

  // What the rows' switch buttons read from the sides: which sides there are
  // and what each holds, so a side switched to another account redraws them.
  function sideNames() {
    return lastSides.map(function (side) { return side.side + "\u0001" + (side.liveAccount || ""); }).join("\u0000");
  }

  // The rows are drawn with the sides already known, and the side read (which
  // waits on the other side for up to its timeout) never holds them back. A
  // side that comes or goes redraws the rows once, under the same guards.
  function refresh(force) {
    if (!instanceToken) { return Promise.resolve(); }
    return fetch("/api/dashboard", { headers: withBearer({ "Accept": "application/json" }) })
      .then(function (response) {
        if (response.status === 401) { loseToken(); return null; }
        return response.json();
      })
      .then(function (dashboard) {
        if (!dashboard || !instanceToken) { return null; }
        render(dashboard, force);
        return fetch("/api/sides", { headers: withBearer({ "Accept": "application/json" }) });
      })
      .then(function (response) {
        if (!response || !instanceToken) { return null; }
        if (response.status === 401) { loseToken(); return null; }
        return response.ok ? response.json() : [];
      })
      .then(function (sides) {
        if (!sides || !instanceToken) { return; }
        lastSides = Array.isArray(sides) ? sides : [];
        renderNow();
        if (sideNames() !== renderedSides) { render(lastDashboard, force); }
      })
      .catch(function (error) { showToast("Dashboard unavailable: " + error, "error"); });
  }

  function setButtonsDisabled(disabled) {
    // Synchronously, at click time: a second click during the lock wait would
    // otherwise send a second request whose refusal toast overwrote the outcome of
    // the first. The re-enable runs before the redraw, and a failed refresh never
    // delivers that redraw, so a button its own state disables stays disabled.
    Array.prototype.forEach.call(document.querySelectorAll("button"), function (button) {
      button.disabled = disabled || button.hasAttribute("data-blocked");
    });
  }

  // A row menu closes when the pointer or the focus goes anywhere else.
  document.addEventListener("click", function (event) {
    Array.prototype.forEach.call(document.querySelectorAll("details.more[open], details.roster-add[open]"), function (open) {
      if (!open.contains(event.target)) { open.open = false; }
    });
  });
  document.addEventListener("keydown", function (event) {
    if (event.key !== "Escape") { return; }
    Array.prototype.forEach.call(document.querySelectorAll("details.more[open]"), function (open) {
      open.open = false;
      open.querySelector("summary").focus();
    });
  });


  addForm.addEventListener("submit", function (event) {
    event.preventDefault();
    if (!addFields) { return; }
    var body = addFields.read();
    body.email = addEmail.value.trim();
    mutate("/api/accounts", "POST", body, function (result) {
      if (result.ok) {
        addForm.reset();
        addFields.reset();
        showToast(body.email + " added; it needs a login before it can be switched to", "ok");
      } else {
        showToast(refused(result.body), "error");
      }
    });
  });

  refreshAllButton.addEventListener("click", function () {
    mutate("/api/refresh", "POST", null, started);
  });

  // The leader runs on Windows, Linux or macOS, so the command is the one every build answers to.
  var RESTART = "Start it again with claude-code-account-rotation --open, or on Windows from the Start-menu shortcut.";

  // Not through mutate(): its refresh afterwards would poll a process that is going away.
  document.getElementById("stop").addEventListener("click", function () {
    var names = lastSides.map(function (side) { return side.side; });
    var also = names.length ? " and its " + names.join(", ") + " side" : "";
    if (!window.confirm("Stop claude-code-account-rotation" + also + "?\n\nThe page cannot restart the tool. " + RESTART)) {
      return;
    }
    busy = true;
    setButtonsDisabled(true);
    send("/api/stop", "POST", null)
      .then(function (result) {
        if (!result.ok) { showToast(refused(result.body), "error"); return; }
        clearInterval(polling);
        showStopped(result.body.sides || []);
      })
      .catch(function (error) { showToast("Request failed: " + error, "error"); })
      .then(function () { busy = false; setButtonsDisabled(false); });
  });

  function showStopped(sides) {
    var page = element("main", "stopped");
    page.appendChild(element("h1", null, "claude-code-account-rotation is stopped"));
    page.appendChild(element("p", null, RESTART));
    sides.forEach(function (side) {
      page.appendChild(element("p", "muted", "The " + side.side + " side: " + side.detail + ". Start it from the page once the tool is running again."));
    });
    document.body.replaceChildren(page);
  }

  addEmail.addEventListener("input", function () {
    if (addFields) { addFields.autoMatch(addEmail.value); }
  });

  // Before the first render, so the Add form and every card's Edit panel are
  // built from the same list. An enumeration that fails is an empty list, not a
  // dead page: the typed field is still there. An empty token does not call
  // this, the dashboard, or the side list; the paste form is the whole page
  // until there is one.
  function loadProfiles() {
    if (!instanceToken) {
      showTokenForm();
      return;
    }
    fetch("/api/browser-profiles", { headers: withBearer({ "Accept": "application/json" }) })
      .then(function (response) {
        if (response.status === 401) { loseToken(); return null; }
        return response.ok ? response.json() : [];
      })
      .catch(function () { return []; })
      .then(function (found) {
        if (found === null || !instanceToken) { return; }
        profileCatalog = Array.isArray(found) ? found : [];
        if (!addFields) {
          addFields = accountFields(document.getElementById("add-fields"), null);
        }
        refresh();
        if (!polling) {
          polling = setInterval(refresh, POLL_MS);
          // Opening the page reads usage once, the way Refresh all does; the
          // server's per-account gap keeps a reload from reading again. A refusal
          // needs no toast: the pass line above the cards already says why.
          send("/api/refresh", "POST", null).then(function () { return refresh(true); }, function () { return null; });
        }
      });
  }

  // Always taken, so the fragment leaves the address bar even when the meta
  // already carries the token. The first /api call then sets the cookie.
  var tokenFromHash = takeTokenFromHash(window.location, window.history);
  if (!instanceToken) { instanceToken = tokenFromHash; }

  if (instanceToken) {
    loadProfiles();
  } else {
    showTokenForm();
  }
})();
