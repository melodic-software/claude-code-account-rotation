(function () {
  "use strict";

  // How old a usage figure may be before the page stops treating it as
  // current: older than this, the line says "may be out of date" and a card
  // never reads "Usable now" on it. Basis: judgment. The five-hour window is
  // the one that moves fastest, and a heavy session can spend a large share of
  // it within half an hour, so a figure older than that may no longer be true;
  // a shorter limit would mark every parked card stale between Refresh clicks,
  // since usage is read only when asked (decision #164).
  var STALE_SECONDS = 30 * 60;

  // What every lane's Console key is named, so a key list says which is a lane's.
  var CONSOLE_KEY_NAME = "github-actions";

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
      tier: tier,
      asOf: asOf,
      usageAge: usageAge,
      sentence: sentence,
      ciRoleBadges: ciRoleBadges,
      ciTokenExpiries: ciTokenExpiries,
      ciExpirySentence: ciExpirySentence,
      removedCiRoles: removedCiRoles,
      changedCiTokenDate: changedCiTokenDate,
      ciTag: ciTag,
      ciSecretBody: ciSecretBody,
      repairOffers: repairOffers,
      repairSecrets: repairSecrets,
      remoteControlQuestion: remoteControlQuestion,
      switchedToast: switchedToast,
      historyLines: historyLines,
      historyReadLines: historyReadLines
    };
    module.exports.reissueButton = reissueButton;
    module.exports.ciPanelChecked = ciPanelChecked;
    module.exports.meterTicks = meterTicks;
    module.exports.alertLines = alertLines;
    module.exports.bestSpareActions = bestSpareActions;
    module.exports.apiKeyBody = apiKeyBody;
    module.exports.apiKeyBadge = apiKeyBadge;
    module.exports.keptKeyToast = keptKeyToast;
    module.exports.roleCheckLines = roleCheckLines;
    module.exports.assignableRoles = assignableRoles;
    module.exports.catalogNotice = catalogNotice;
    module.exports.newRoleItem = newRoleItem;
    module.exports.ciPanelWords = ciPanelWords;
    module.exports.ciNoRolesWords = ciNoRolesWords;
    module.exports.keyStoredWords = keyStoredWords;
    module.exports.browserLine = browserLine;
    module.exports.roleWhere = roleWhere;
    module.exports.apiKeyCheckToast = apiKeyCheckToast;
    module.exports.killSwitchRows = killSwitchRows;
    module.exports.toggleKillSwitch = toggleKillSwitch;
    module.exports.limitStopSources = limitStopSources;
    module.exports.keyRoleChoices = keyRoleChoices;
    module.exports.costSettingsBody = costSettingsBody;
    module.exports.limitStopNotice = limitStopNotice;
    module.exports.limitStopGate = limitStopGate;
    module.exports.ciTokenBadge = ciTokenBadge;
    module.exports.assignHalves = assignHalves;
    module.exports.ciTokenValueBody = ciTokenValueBody;
    module.exports.stateChip = stateChip;
    module.exports.loginLine = loginLine;
    module.exports.loginOffered = loginOffered;
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
  // The usage alerts the last full refresh computed, above the warnings. Built
  // here rather than in the page's markup, and only ever written by render().
  var alertStrip = element("ul", "alerts");
  alertStrip.hidden = true;
  warnings.parentNode.insertBefore(alertStrip, warnings);
  var captured = document.getElementById("captured");
  var refreshAllButton = document.getElementById("refresh-all");
  var refreshStateLine = document.getElementById("refresh-state");
  var toast = document.getElementById("toast");
  var nowLine = document.getElementById("now");
  // The accounts the last dashboard named, and the other sides the last side
  // read named. A side's Now strip and every row's switch buttons are drawn
  // from the two together.
  var lastAccounts = [];
  // What the last Check roles click found, so the CI panel can offer roles
  // with no holder yet. Null until Check roles runs; nothing else reads GitHub.
  var lastRoleCheck = null;
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

  // Whether a card's figures are recent enough to steer by: both the 5-hour and
  // the 7-day row are known and were read within STALE_SECONDS. A usable
  // standing read longer ago may be limited by now, and one with a window no
  // source carried may be spent on it, so nothing recommends either (#188).
  function current(account, at) {
    var age = usageAge(account, at);
    return age !== null && age <= STALE_SECONDS && unknownWindow(account) === null;
  }

  // The label of the first of the 5-hour and 7-day windows no source carried,
  // or null when both are known.
  function unknownWindow(account) {
    var missing = [["session", "5-hour"], ["weekly_all", "7-day"]].filter(function (window) {
      return !account.usage.limits.some(function (limit) { return limit.kind === window[0] && limit.known; });
    })[0];
    return missing ? missing[1] : null;
  }

  function usableFor(accounts, takes) {
    return accounts.filter(function (account) { return account.standing === "usable" && takes(account); });
  }

  // Which accounts a side may take: this side's Switch, or another side's offer.
  function takesHere(account) { return account.canSwitchHere; }

  function takesOn(side) {
    return function (account) { return (account.offeredTo || []).indexOf(side) !== -1; };
  }

  // The account a side should take next: the first usable card in the server's
  // order, which is the soonest weekly reset, among those that side may take
  // and whose figures are current. None when no such card exists.
  function recommended(accounts, takes, at) {
    return usableFor(accounts, takes).filter(function (account) { return current(account, at); })[0] || null;
  }

  // The card that would have been recommended had its figures been current:
  // named, with their age, when nothing current is left, and never as a target.
  function unverified(accounts, takes, at) {
    return usableFor(accounts, takes).filter(function (account) { return !current(account, at); })[0] || null;
  }

  // One "switch now" prompt per side whose account is at 100% of its 5-hour or
  // 7-day window, or that the server placed as model-limited (its Fable weekly
  // window spent): { side, from, to, unverified }, side null for this side. To is
  // the recommendation, null when no account that side can take has headroom on
  // current figures; unverified is then the first usable card whose figures are
  // too old to steer by, or null. Nothing switches on its own; the prompt is a
  // button the operator clicks.
  function switchPrompts(accounts, sides, at) {
    function atLimit(email) {
      var account = accounts.filter(function (candidate) { return candidate.email === email; })[0];
      return !!account && (account.standing === "model-limited" || account.usage.limits.some(function (limit) {
        return (limit.kind === "session" || limit.kind === "weekly_all") && limit.percent >= 100 && !limit.windowReset;
      }));
    }
    function prompt(side, from, takes) {
      var to = recommended(accounts, takes, at);
      var old = to ? null : unverified(accounts, takes, at);
      return { side: side, from: from, to: to ? to.email : null, unverified: old ? old.email : null };
    }
    var prompts = [];
    var live = accounts.filter(function (account) { return account.isLive; })[0];
    if (live && atLimit(live.email)) {
      prompts.push(prompt(null, live.email, takesHere));
    }
    sides.forEach(function (side) {
      if (side.online && side.liveAccount && atLimit(side.liveAccount)) {
        prompts.push(prompt(side.side, side.liveAccount, takesOn(side.side)));
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

  // One line per lane-history entry, newest first: when, which role half and
  // where, and which account took it from which. Names and dates only.
  function historyLines(entries) {
    return (entries || []).slice().reverse().map(function (entry) {
      var role = entry.role || {};
      var name = (role.tokenSecret || role.prefix || "legacy (review lanes)")
        + " in " + roleWhere(role);
      var moved = (entry.previous || "no account") + " \u2192 " + (entry.account || "no account");
      return String(entry.at).slice(0, 16).replace("T", " ") + " \u00b7 " + name + " " + entry.half + ": " + moved + " (" + entry.via + ")";
    });
  }

  // What the History list shows for one read: the entries, the empty text for
  // a successful empty read, or a failure line (with the status when known).
  function historyReadLines(read) {
    if (!read.ok) {
      return ["Could not read the history" + (read.status ? " (HTTP " + read.status + ")" : "")];
    }
    var lines = historyLines(read.entries);
    return lines.length ? lines : ["No role changes in the last 30 days."];
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

  // Switch asks first only while Remote Control runs here, because only then
  // does the switch cost anything beyond the account: the task is restarted
  // under the incoming account. Read at the click, not on every poll. A read
  // that fails still asks, in the conditional: the server reads the task again
  // and may find it running. Every button is off from the click, through the read and
  // the question, so a second click cannot start a second switch, and with it a
  // second restart, while the first is still being decided.
  function switchTo(email) {
    var account = lastAccounts.filter(function (candidate) { return candidate.email === email; })[0];
    var name = account ? uniqueName(account, lastAccounts) : email;
    busy = true;
    setButtonsDisabled(true);
    return send("/api/remote-control", "GET")
      .then(function (result) { return remoteControlQuestion(result.ok ? result.body : null, name); }, function () { return remoteControlQuestion(null, name); })
      .then(function (question) {
        if (question && !window.confirm(question)) {
          busy = false;
          setButtonsDisabled(false);
          return null;
        }
        return mutate(accountPath(email, "/switch"), "POST", null, function (result) {
          if (!result.ok) {
            showToast(refused(result.body), "error");
            return;
          }
          var toast = switchedToast(result.body);
          showToast(toast.text, toast.kind);
        });
      });
  }

  // Null, or a null running, when the read failed: the state is unknown, so the
  // question is asked as a condition rather than skipped.
  function remoteControlQuestion(remoteControl, name) {
    if (!remoteControl || remoteControl.running === null || remoteControl.running === undefined) {
      return "Switch to " + name + "? Remote Control's state could not be read. If it is running, it restarts under " + name
        + "; live Remote Control sessions on this machine drop and reopen in " + name + "'s Claude app.";
    }
    if (!remoteControl.running) { return null; }
    return "Switch to " + name + "? Remote Control restarts under " + name
      + "; live Remote Control sessions on this machine drop and reopen in " + name + "'s Claude app.";
  }

  function switchedToast(body) {
    var text = "Switched to " + body.now;
    if (body.parkedAs) { text += "; parked " + body.parkedAs; }
    if (body.identityMismatchWarning) { text += ". The CLI reports " + body.cliEmail + "; check /status."; }
    if (body.remoteControlRestarted) { text += ". Remote Control restarted."; }
    if (body.remoteControlWarning) { text += ". " + body.remoteControlWarning; }
    return { text: text, kind: body.identityMismatchWarning || body.remoteControlWarning ? "warn" : "ok" };
  }

  // The "switch now" prompt's wording, all of it here: it is to follow the
  // operator's test at the next real limit (#148).
  function promptWording(prompt, accounts, at) {
    function named(email) {
      var account = accounts.filter(function (candidate) { return candidate.email === email; })[0];
      return account ? uniqueName(account, accounts) : email;
    }
    var line = (prompt.side ? prompt.side + " side: " : "") + named(prompt.from) + " is at its usage limit.";
    var old = prompt.unverified ? accounts.filter(function (candidate) { return candidate.email === prompt.unverified; })[0] : null;
    return {
      text: prompt.to ? line : line + " " + (old ? unverifiedLine(old, accounts, at) : "No other account has headroom."),
      button: prompt.to ? "Switch now to " + named(prompt.to) : null
    };
  }

  // Said instead of a recommendation when the only accounts left were read too
  // long ago to steer by: no target, the best of them with its age, and Refresh.
  function unverifiedLine(account, accounts, at) {
    var age = usageAge(account, at);
    var gap = unknownWindow(account);
    return "No account to switch to has usage read in the last " + span(STALE_SECONDS) + ". "
      + uniqueName(account, accounts) + " is unverified, "
      + (gap ? "its " + gap + " figure unknown" : age === null ? "with no figures" : "figures " + span(age) + " old")
      + "; refresh before switching.";
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
      var tag = ciTag(account.roster);
      if (tag) { address.appendChild(element("span", "tag", tag)); }
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
        if (limit.known || limit.kind === "session" || limit.kind === "weekly_all") {
          meters.appendChild(meter(limit, at, limit.kind === "weekly_all" ? meterTicks(account, lastDashboard && lastDashboard.alertThresholds) : []));
        }
      });
      strip.appendChild(meters);
    }

    var go = element("div", "go");
    var blocked = !!lastDashboard.banner;
    // The #148 "switch now" prompt, when this side's account is at a limit. It
    // names the same account the recommended button would, so it takes that
    // button's place rather than sitting beside it.
    var prompt = switchPrompts(lastAccounts, lastSides, at).filter(function (candidate) { return candidate.side === name; })[0];
    var wording = prompt ? promptWording(prompt, lastAccounts, at) : null;
    if (wording) { strip.appendChild(element("p", "prompt", wording.text)); }
    var urgent = !!wording || (!!say && say.kind === "crit");
    // With no current target, the account that would have been named is said
    // to be unverified, with its age, and the strip offers Refresh in place of
    // a switch: a read the operator asks for, never one started here (#164).
    function refreshInstead(takes) {
      var old = unverified(lastAccounts, takes, at);
      if (!old) { return; }
      if (!wording) { strip.appendChild(element("p", "prompt", unverifiedLine(old, lastAccounts, at))); }
      go.appendChild(blockable(actionButton("Refresh usage", "lg " + (urgent ? "primary" : "secondary"), function () {
        mutate("/api/refresh", "POST", null, started);
      }), false));
    }
    if (!side) {
      var here = recommended(lastAccounts, takesHere, at);
      if (here) {
        go.appendChild(takeButton(wording ? wording.button : "Switch to " + uniqueName(here, lastAccounts), here, at, urgent,
          switchBlocked(here, lastDashboard, false), function () { switchTo(here.email); }));
      } else {
        refreshInstead(takesHere);
      }
    } else if (side.online) {
      if (account && account.roster && account.loggedOutOn === side.side) {
        go.appendChild(actionButton("Log in again on " + sideLabel(name), "primary lg", function () { loginFromSide(side.side, account.email); }));
      }
      var there = recommended(lastAccounts, takesOn(side.side), at);
      if (there) {
        go.appendChild(takeButton(wording ? wording.button : "Switch " + sideLabel(name) + " to " + uniqueName(there, lastAccounts), there, at, urgent,
          blocked, function () { switchSide(side.side, there.email); }));
      } else {
        refreshInstead(takesOn(side.side));
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
      if (result.body.refusal === "StoredKeyKept" && window.confirm(result.body.message + "\n\nRemove the account anyway and leave the key in the OS store?")) {
        return send(accountPath(email) + "?keepStoredKey=true", "DELETE", null).then(function (forced) {
          var toastFor = keptKeyToast(email + " removed", forced);
          showToast(toastFor.text, toastFor.kind);
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

  // Generate CI token: pick the roles this account is to back, then the same
  // sign-in as a login, in the account's mapped browser profile. The token goes
  // from the CLI to `gh secret set` on the server, once per role, with the
  // account's stored Console key going to each role's key secret; neither
  // reaches this page. The panel is a details.login, so an open one holds the
  // poll's re-render the same way.
  function openCiTokenPanel(account) {
    var card = cardNodes[account.email];
    if (!card) { return; }
    var existing = card.querySelector("details.ci-token");
    if (existing) { existing.parentNode.removeChild(existing); }

    var panel = element("details", "login ci-token");
    panel.open = true;
    panel.appendChild(element("summary", null, "CI token for " + account.email));
    var roster = account.roster;
    var words = ciPanelWords(roster, cardProfileLabel(roster));
    panel.appendChild(element("p", null, words.intro));
    panel.appendChild(element("p", "muted", words.detail));

    var catalog = lastDashboard && lastDashboard.laneCatalog;
    var cataloged = !!(catalog && (catalog.roles || []).length);
    var roles = assignableRoles(lastAccounts, lastRoleCheck, catalog);
    if (catalogNotice(catalog)) { panel.appendChild(catalogLoader(account, catalogNotice(catalog))); }
    else if (!roles.length) { panel.appendChild(inlineRoleCheck(account)); }
    var form = element("form", "roster-form");
    var checked = ciPanelChecked(roles, account.email);
    var boxes = roles.map(function (role, index) {
      var box = element("input");
      box.type = "checkbox";
      box.checked = checked[index];
      labeled(form, role.label, box);
      return box;
    });
    // A catalog read names every role, so the free-text row is only for
    // working without one.
    var prefix = element("input");
    prefix.type = "text";
    prefix.placeholder = "INTAKE";
    var where = element("input");
    where.type = "text";
    where.placeholder = "octo-org";
    if (!cataloged) {
      labeled(form, "New role (prefix, optional)", prefix);
      labeled(form, "New role's organization", where);
    }

    // A keyed role's Console key goes live with the token, so the owner
    // answers the cost questions first (issue #317 item 1).
    var cost = roles.some(function (role) { return role.keyed; }) || !cataloged ? costQuestions(form) : null;
    var start = element("button", null, words.start);
    start.type = "submit";
    form.appendChild(start);
    form.appendChild(actionButton("Cancel", "secondary", function () { panel.parentNode.removeChild(panel); }));
    var status = element("p", "muted");
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      var keyedPicked = roles.some(function (role, index) { return boxes[index].checked && role.keyed; });
      var picked = roles.filter(function (_, index) { return boxes[index].checked; }).map(function (role) { return role.item; });
      var created = newRoleItem(prefix.value, where.value, lastRoleCheck, lastDashboard && lastDashboard.tokenOnlyRolePrefixes);
      if (created && created.error) { status.textContent = created.error; return; }
      if (created) {
        // A role typed by hand may create secrets, visible to every
        // repository, so the page asks first and says which.
        if (!window.confirm(created.confirm)) { return; }
        picked.push(created.item);
      }
      if (!picked.length) { status.textContent = "Pick at least one role."; return; }
      var body = { roles: picked };
      if (cost && (keyedPicked || created)) {
        var answered = costSettingsBody(cost.autoReload.value, cost.extraUsage.value, account.email);
        if (answered.error && keyedPicked) { status.textContent = answered.error; return; }
        if (!answered.error) { body.costSettings = answered.costSettings; }
      }
      var go = function () { startCiToken(account.email, body, form, status, panel); };
      var froms = limitStopSources(picked, lastAccounts, account.email);
      if (!froms.length) { go(); return; }
      checkLimitStops(froms, panel, status, go);
    });
    panel.appendChild(form);
    panel.appendChild(status);
    card.appendChild(panel);
    start.focus();
  }

  // Load the configured lane catalog from inside the CI panel: one read on the
  // click, then the panel drawn again from the dashboard that follows.
  function catalogLoader(account, notice, reopen) {
    var box = element("form", "roster-form role-check");
    box.appendChild(element("p", "muted", notice));
    var load = element("button", "secondary", "Load lane catalog");
    load.type = "submit";
    box.appendChild(load);
    var status = element("p", "muted");
    box.appendChild(status);
    box.addEventListener("submit", function (event) {
      event.preventDefault();
      busy = true;
      setButtonsDisabled(true);
      status.textContent = "Reading the lane catalog...";
      send("/api/lane-catalog/refresh", "POST")
        .then(function (result) {
          if (!result.ok) { status.textContent = refused(result.body); return false; }
          if (lastDashboard) { lastDashboard.laneCatalog = result.body; }
          return true;
        })
        .catch(function (error) { status.textContent = "Request failed: " + error; return false; })
        .then(function (loaded) {
          busy = false;
          setButtonsDisabled(false);
          if (loaded) { (reopen || openCiTokenPanel)(account); }
        });
    });
    return box;
  }

  // Check roles from inside the CI panel, for when it has no role to offer:
  // the header's same one GitHub read, then the panel drawn again from what it
  // found. Its own little form, so Enter in it checks rather than starts.
  function inlineRoleCheck(account) {
    var box = element("form", "roster-form role-check");
    box.appendChild(element("p", "muted", ciNoRolesWords(lastRoleCheck)));
    var organization = element("input");
    organization.type = "text";
    organization.autocomplete = "off";
    organization.placeholder = "octo-org";
    labeled(box, "Organization (optional when a role names one)", organization);
    var check = element("button", "secondary", "Check roles");
    check.type = "submit";
    box.appendChild(check);
    var status = element("p", "muted");
    box.appendChild(status);
    box.addEventListener("submit", function (event) {
      event.preventDefault();
      busy = true;
      setButtonsDisabled(true);
      status.textContent = "Reading GitHub secrets...";
      checkRoles(organization.value, send)
        .then(function (result) {
          if (!result.ok) { status.textContent = refused(result.body); return false; }
          return true;
        })
        .catch(function (error) { status.textContent = "Request failed: " + error; return false; })
        .then(function (found) {
          busy = false;
          setButtonsDisabled(false);
          if (found) { openCiTokenPanel(account); }
        });
    });
    return box;
  }

  // Before roles move off other accounts: when one of them is near a limit
  // on the last dashboard, show that with Move anyway and Cancel; else start
  // at once. Nothing is read for it, and it never blocks the move.
  function checkLimitStops(froms, panel, status, go) {
    var existing = panel.querySelector(".limit-stops");
    if (existing) { existing.parentNode.removeChild(existing); }
    var ask = function (notice) {
      return new Promise(function (resolve) {
        var box = element("div", "limit-stops");
        notice.forEach(function (line) { box.appendChild(element("p", "say " + line.kind, line.text)); });
        var close = function (answer) { box.parentNode.removeChild(box); resolve(answer); };
        box.appendChild(actionButton("Move anyway", null, function () { close(true); }));
        box.appendChild(actionButton("Cancel", "secondary", function () { close(false); }));
        panel.insertBefore(box, status);
      });
    };
    return limitStopGate(froms, lastAccounts, ask).then(function (proceed) {
      if (proceed) { go(); }
    });
  }

  // The two cost questions every write of a Console key asks: auto-reload in
  // the Console and extra usage on the plan. Neither has an answer until the
  // owner picks one; this tool cannot read auto-reload, so it never assumes.
  function costQuestions(form) {
    function ask(text) {
      var select = element("select");
      [["", "Choose"], ["off", "Off"], ["on", "On"]].forEach(function (pair) {
        var option = element("option", null, pair[1]);
        option.value = pair[0];
        select.appendChild(option);
      });
      labeled(form, text, select);
      return select;
    }
    return {
      autoReload: ask("Auto-reload (Console, Billing)"),
      extraUsage: ask("Extra usage (claude.ai, Usage)")
    };
  }

  // Set API key: the account's Console API key, pasted once on this machine
  // and kept in its OS store (Credential Manager, or the Secret Service). The
  // field is cleared as soon as it is read, and the key never comes back to
  // this page. A details.login, so an open one holds the poll's re-render.
  // With admin set, the same panel takes the optional Admin key instead.
  // For the Console key the same panel is the one flow that puts it live:
  // paste it (or keep the stored one), tick the keyed roles it is to back,
  // answer the two cost questions, and it is stored and sent in one click.
  function openApiKeyPanel(account, admin) {
    var key = keyKind(admin);
    var card = cardNodes[account.email];
    if (!card) { return; }
    var existing = card.querySelector("details.api-key");
    if (existing) { existing.parentNode.removeChild(existing); }

    var roster = account.roster || {};
    var stored = !admin && !!roster.consoleApiKeySetOn;
    var panel = element("details", "login api-key");
    panel.open = true;
    panel.appendChild(element("summary", null, key.title + " for " + account.email));
    panel.appendChild(element("p", "muted",
      "Paste this account's " + key.title + "." + (admin ? " It is used only when you click Check API key." : "")
      + " It is kept only in this machine's OS store, never in a file of this tool's, and is not shown here again."));
    var catalog = lastDashboard && lastDashboard.laneCatalog;
    if (!admin) {
      panel.appendChild(element("p", "muted",
        "In the Console, name the key " + CONSOLE_KEY_NAME + " and turn auto-reload and extra usage off for this account first."
        + (roster.costSettingsConfirmedOn ? " You last confirmed both off on " + roster.costSettingsConfirmedOn + "." : "")));
      if (catalogNotice(catalog)) {
        panel.appendChild(catalogLoader(account, catalogNotice(catalog), function (again) { openApiKeyPanel(again); }));
      }
    }
    var form = element("form", "roster-form");
    var field = element("input");
    field.type = "password";
    field.autocomplete = "off";
    field.spellcheck = false;
    labeled(form, stored ? key.label + " (blank keeps the stored key)" : key.label, field);
    var choices = admin ? [] : keyRoleChoices(lastAccounts, catalog, account.email);
    var boxes = choices.map(function (choice) {
      var box = element("input");
      box.type = "checkbox";
      box.checked = choice.checked;
      labeled(form, choice.label, box);
      return box;
    });
    var cost = choices.length ? costQuestions(form) : null;
    var save = element("button", null, choices.length ? "Save and send" : "Save");
    save.type = "submit";
    form.appendChild(save);
    // The Console page that creates this kind of key, in the account's mapped
    // browser profile, so the key is made while signed in as this account.
    form.appendChild(actionButton("Open Console", "secondary", function () {
      busy = true;
      setButtonsDisabled(true);
      status.textContent = "Opening the Console...";
      send(accountPath(account.email, "/console-page"), "POST", { page: key.page.name })
        .then(function (result) {
          status.textContent = result.ok
            ? "Opened the Console's " + key.page.title + " page in this account's browser profile. Create a key there, then paste it here."
            : refused(result.body);
        })
        .catch(function (error) { status.textContent = "Request failed: " + error; })
        .then(function () { busy = false; setButtonsDisabled(false); });
    }));
    form.appendChild(actionButton("Cancel", "secondary", function () { panel.parentNode.removeChild(panel); }));
    var status = element("p", "muted");
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      var picked = choices.filter(function (_, index) { return boxes[index].checked; });
      var answered = picked.length ? costSettingsBody(cost.autoReload.value, cost.extraUsage.value, account.email) : null;
      if (answered && answered.error) { status.textContent = answered.error; return; }
      var body = apiKeyBody(field.value);
      if (!body && !stored) { status.textContent = "Paste the key first."; return; }
      if (!body && !picked.length) { status.textContent = "Paste a new key, or pick a role to send the stored one to."; return; }
      // The field is cleared only once the key is on its way: a Cancel at
      // the limit-stop check keeps it, since the Console shows a key once.
      var go = function () {
        field.value = "";
        busy = true;
        setButtonsDisabled(true);
        var said = [];
        var saved = body ? send(accountPath(account.email, key.path), "PUT", body) : Promise.resolve({ ok: true, body: null });
        saved
          .then(function (result) {
            if (!result.ok) { status.textContent = refused(result.body); return null; }
            if (result.body) { said.push(keyStoredWords(result.body, admin)); }
            if (!picked.length) { return true; }
            return send(accountPath(account.email, "/api-key/push"), "POST", {
              roles: picked.map(function (choice) { return choice.item; }),
              costSettings: answered.costSettings
            }).then(function (pushed) {
              if (!pushed.ok) { said.push(refused(pushed.body)); return false; }
              said.push(pushed.body.message);
              return !pushed.body.failed;
            });
          })
          .then(function (done) {
            if (done === null) { return null; }
            // The panel stays, now saying what was stored and sent, until
            // Close. The card is redrawn under it at once.
            form.parentNode.removeChild(form);
            status.textContent = said.join(" ");
            panel.appendChild(actionButton("Close", "secondary", function () {
              if (panel.parentNode) { panel.parentNode.removeChild(panel); }
            }));
            showToast(said.join(" "), done ? "ok" : "warn");
            return refresh(true).then(function () {
              var redrawn = cardNodes[account.email];
              if (redrawn && panel.parentNode !== redrawn) { redrawn.appendChild(panel); }
            });
          })
          .catch(function (error) { status.textContent = "Request failed: " + error; })
          .then(function () { busy = false; setButtonsDisabled(false); });
      };
      var froms = [];
      picked.forEach(function (choice) {
        if (choice.holder && choice.holder !== account.email && froms.indexOf(choice.holder) < 0) { froms.push(choice.holder); }
      });
      if (!froms.length) { go(); return; }
      checkLimitStops(froms, panel, status, go);
    });
    panel.appendChild(form);
    panel.appendChild(status);
    card.appendChild(panel);
    field.focus();
  }

  // The roles Set API key offers: with a catalog read, its keyed roles only,
  // in catalog order; without one, the keyed roles an account on the roster
  // backs in an organization (the dashboard sends no key secret for a
  // token-only role). Each says who sets its key secret now, and starts
  // ticked when this account does.
  function keyRoleChoices(accounts, catalog, email) {
    var cataloged = catalog && (catalog.roles || []).length ? catalog.roles : null;
    var seen = {};
    var choices = [];
    function holderOf(id) {
      var found = (accounts || []).filter(function (account) {
        return ((account.roster && account.roster.ciRoles) || []).some(function (held) {
          return held.keySetOn && held.keySecret && (held.keySecret + "@" + held.organization).toLowerCase() === id;
        });
      })[0];
      return found ? found.email : null;
    }
    function add(prefix, organization, keySecret) {
      if (!keySecret || !organization) { return; }
      var id = (keySecret + "@" + organization).toLowerCase();
      if (seen[id]) { return; }
      seen[id] = true;
      var holder = holderOf(id);
      choices.push({
        item: { prefix: prefix, organization: organization },
        holder: holder,
        checked: holder === email,
        label: prefix + " in org " + organization + " · " + keySecret + " · " + (holder ? "key from " + holder : "no key yet")
      });
    }
    if (cataloged) {
      cataloged.forEach(function (role) { if (role.keyed) { add(role.prefix, role.organization, role.keySecret); } });
    } else {
      (accounts || []).forEach(function (account) {
        ((account.roster && account.roster.ciRoles) || []).forEach(function (role) { add(role.prefix, role.organization, role.keySecret); });
      });
    }
    return choices;
  }

  // The owner's two answers as the server reads them, or the reason they
  // cannot be sent: one unanswered, or one on.
  function costSettingsBody(autoReload, extraUsage, email) {
    if (!autoReload || !extraUsage) {
      return { error: "Answer both: is auto-reload off, and is extra usage off, for " + email + "?" };
    }
    if (autoReload === "on" || extraUsage === "on") {
      return { error: "Turn auto-reload and extra usage off for " + email + " first: no lane key is sent while either is on." };
    }
    return { costSettings: { autoReload: autoReload, extraUsage: extraUsage } };
  }

  // Store CI token: a `claude setup-token` token made by hand, pasted once and
  // kept in the OS store beside the Console key. The field is cleared as soon
  // as it is read, and the token never comes back to this page.
  function openCiTokenValuePanel(account) {
    var card = cardNodes[account.email];
    if (!card) { return; }
    var existing = card.querySelector("details.ci-token-value");
    if (existing) { existing.parentNode.removeChild(existing); }
    var panel = element("details", "login ci-token-value");
    panel.open = true;
    panel.appendChild(element("summary", null, "CI token for " + account.email));
    panel.appendChild(element("p", "muted",
      "Paste a token `claude setup-token` printed for this account. It is kept only in this machine's OS store, never in a file of this tool's, and is not shown here again. Assign to role sends it to a role."));
    var form = element("form", "roster-form");
    var field = element("input");
    field.type = "password";
    field.autocomplete = "off";
    field.spellcheck = false;
    labeled(form, "CI token", field);
    var save = element("button", null, "Store");
    save.type = "submit";
    form.appendChild(save);
    form.appendChild(actionButton("Cancel", "secondary", function () { panel.parentNode.removeChild(panel); }));
    var status = element("p", "muted");
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      var body = ciTokenValueBody(field.value);
      field.value = "";
      if (!body) { status.textContent = "Paste the token first."; return; }
      mutate(accountPath(account.email, "/ci-token-value"), "PUT", body, function (result) {
        if (!result.ok) { status.textContent = refused(result.body); return null; }
        if (panel.parentNode) { panel.parentNode.removeChild(panel); }
        showToast("CI token stored for " + account.email + " in " + result.body.storedIn + ".", "ok");
        return null;
      });
    });
    panel.appendChild(form);
    panel.appendChild(status);
    card.appendChild(panel);
    field.focus();
  }

  function clearCiTokenValue(email) {
    if (!window.confirm("Delete " + email + "'s CI token from this machine's OS store? Secrets it was sent to keep it.")) {
      return Promise.resolve();
    }
    return mutate(accountPath(email, "/ci-token-value"), "DELETE", null, function (result) {
      showToast(result.ok ? "CI token deleted for " + email : refused(result.body), result.ok ? "ok" : "error");
      return null;
    });
  }

  // Assign to role: send this account's stored token, key, or both to one
  // role, with no sign-in. The roles are the CI panel's; the halves offered
  // are the ones this machine stores and the role takes.
  function openAssignPanel(account) {
    var card = cardNodes[account.email];
    if (!card) { return; }
    var existing = card.querySelector("details.assign-role");
    if (existing) { existing.parentNode.removeChild(existing); }
    var roster = account.roster || {};
    var panel = element("details", "login assign-role");
    panel.open = true;
    panel.appendChild(element("summary", null, "Assign " + account.email + " to a role"));
    var catalog = lastDashboard && lastDashboard.laneCatalog;
    if (catalogNotice(catalog)) { panel.appendChild(catalogLoader(account, catalogNotice(catalog), function (again) { openAssignPanel(again); })); }
    var roles = assignableRoles(lastAccounts, lastRoleCheck, catalog);
    if (!roles.length) { panel.appendChild(inlineRoleCheck(account)); }
    var form = element("form", "roster-form");
    var picker = element("select");
    roles.forEach(function (role, index) {
      var option = element("option", null, role.label);
      option.value = String(index);
      picker.appendChild(option);
    });
    labeled(form, "Role", picker);
    var halves = element("select");
    labeled(form, "Send", halves);
    var note = element("p", "muted");
    form.appendChild(note);
    var cost = costQuestions(form);
    var send = element("button", null, "Assign");
    send.type = "submit";
    form.appendChild(send);
    form.appendChild(actionButton("Cancel", "secondary", function () { panel.parentNode.removeChild(panel); }));
    var status = element("p", "muted");
    function redraw() {
      var role = roles[Number(picker.value)];
      var offered = assignHalves(roster, role);
      halves.textContent = "";
      offered.choices.forEach(function (choice) {
        var option = element("option", null, choice.label);
        option.value = choice.value;
        halves.appendChild(option);
      });
      note.textContent = offered.why ? "Not offered: " + offered.why + "." : "";
      send.disabled = !offered.choices.length;
    }
    picker.addEventListener("change", redraw);
    redraw();
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      var role = roles[Number(picker.value)];
      if (!role || !halves.value) { status.textContent = "Pick a role and what to send."; return; }
      var body = { role: role.item, halves: halves.value };
      if (halves.value !== "token") {
        var answered = costSettingsBody(cost.autoReload.value, cost.extraUsage.value, account.email);
        if (answered.error) { status.textContent = answered.error; return; }
        body.costSettings = answered.costSettings;
      }
      var go = function () {
        mutate(accountPath(account.email, "/assign"), "POST", body, function (result) {
          status.textContent = result.ok ? result.body.message : refused(result.body);
          if (result.ok) { showToast(result.body.message, result.body.failed ? "warn" : "ok"); }
          return null;
        });
      };
      var froms = limitStopSources([role.item], lastAccounts, account.email);
      if (!froms.length) { go(); return; }
      checkLimitStops(froms, panel, status, go);
    });
    panel.appendChild(form);
    panel.appendChild(status);
    card.appendChild(panel);
    picker.focus();
  }

  // The alert strip's Assign to best spare: the click is the approval, so it
  // posts Assign to role at once. No limit-stop question: the account the role
  // moves off is the alerted one, and the alert line is that notice.
  function assignToBestSpare(action) {
    return mutate(accountPath(action.spare, "/assign"), "POST", action.body, function (result) {
      showToast(result.ok ? result.body.message : refused(result.body), result.ok ? (result.body.failed ? "warn" : "ok") : "error");
      return null;
    });
  }

  function clearApiKey(email, admin) {
    var key = keyKind(admin);
    if (!window.confirm("Delete " + email + "'s " + key.title + " from this machine's OS store?")) {
      return Promise.resolve();
    }
    return mutate(accountPath(email, key.path), "DELETE", null, function (result) {
      // The store could not delete it. Leaving it there is the operator's
      // call, and the response then names the key and how to remove it by hand.
      if (!result.ok && result.body.refusal === "StoredKeyKept" && window.confirm(result.body.message + "\n\nForget it here and leave it in the OS store?")) {
        return send(accountPath(email, key.path) + "?keepStoredKey=true", "DELETE", null).then(function (forced) {
          var toastFor = keptKeyToast(key.label + " forgotten here for " + email, forced);
          showToast(toastFor.text, toastFor.kind);
        });
      }
      showToast(result.ok ? key.label + " deleted for " + email : refused(result.body), result.ok ? "ok" : "error");
      return null;
    });
  }

  // Open claude.ai and Open Console in the ··· menu: one of the fixed pages,
  // in the account's mapped browser profile, so nobody has to recall which
  // profile is signed in as it.
  function openAccountPage(email, page, title) {
    return mutate(accountPath(email, "/console-page"), "POST", { page: page }, function (result) {
      showToast(result.ok ? "Opened " + title + " in " + email + "'s browser profile." : refused(result.body), result.ok ? "ok" : "error");
      return null;
    });
  }

  function keyKind(admin) {
    return admin
      ? { path: "/admin-key", title: "Admin key", label: "Admin key", page: { name: "admin-keys", title: "Admin keys" } }
      : { path: "/api-key", title: "Console API key", label: "API key", page: { name: "keys", title: "API keys" } };
  }

  // Check API key: one listing of the organization's keys through the Admin
  // API, on this click only, to say whether the stored Console key still works.
  function checkApiKey(email) {
    return mutate(accountPath(email, "/api-key/check"), "POST", null, function (result) {
      var toastFor = apiKeyCheckToast(result);
      showToast(toastFor.text, toastFor.kind);
      return null;
    });
  }

  function startCiToken(email, body, form, status, panel) {
    busy = true;
    setButtonsDisabled(true);
    status.textContent = "Starting claude setup-token...";
    return send(accountPath(email, "/ci-token"), "POST", body)
      .then(function (result) {
        if (!result.ok) {
          status.textContent = refused(result.body);
          return;
        }
        var session = result.body;
        form.parentNode.removeChild(form);
        var link = element("a", null, "Open the sign-in page");
        link.href = session.signInUrl;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
        panel.insertBefore(link, status);
        if (session.browserError) {
          panel.insertBefore(element("p", "muted", "The mapped browser did not open: " + session.browserError + " Use the link above."), status);
        }
        status.textContent = "Sign in as " + email + ", then paste the code that page shows. This expires in ten minutes.";
        var codeForm = element("form", "roster-form");
        var code = element("input");
        code.type = "text";
        code.autocomplete = "off";
        code.placeholder = "paste the code here";
        var submit = element("button", null, "Submit code");
        submit.type = "submit";
        codeForm.appendChild(code);
        codeForm.appendChild(submit);
        codeForm.addEventListener("submit", function (event) {
          event.preventDefault();
          submitCiTokenCode(session.id, email, code, status, panel, codeForm);
        });
        panel.appendChild(codeForm);
        code.focus();
      })
      .catch(function (error) { status.textContent = "Request failed: " + error; })
      .then(function () { busy = false; setButtonsDisabled(false); });
  }

  function submitCiTokenCode(id, email, field, status, panel, codeForm) {
    var code = field.value.trim();
    if (!code) { return Promise.resolve(); }
    busy = true;
    setButtonsDisabled(true);
    status.textContent = "Checking that code and setting the secret...";
    return send("/api/ci-token-sessions/" + encodeURIComponent(id) + "/code", "POST", { code: code })
      .then(function (result) {
        field.value = "";
        if (!result.ok) {
          status.textContent = refused(result.body);
          return null;
        }
        // One code per session: whatever the answer, this session takes no other.
        codeForm.parentNode.removeChild(codeForm);
        return settleCiToken(id, email, result.body, status, panel);
      })
      .catch(function (error) { status.textContent = "Request failed: " + error; })
      .then(function () { busy = false; setButtonsDisabled(false); });
  }

  // The code call answers once the CLI and gh have, or after its own wait runs
  // out; a session still pending then is read again every two seconds until it
  // settles or its ten minutes end.
  function settleCiToken(id, email, session, status, panel) {
    if (session.state === "Pending") {
      status.textContent = "Still working: waiting on claude setup-token and gh...";
      return new Promise(function (resolve) { setTimeout(resolve, 2000); })
        .then(function () { return send("/api/ci-token-sessions/" + encodeURIComponent(id), "GET", null); })
        .then(function (result) {
          if (!result.ok) {
            status.textContent = refused(result.body);
            return null;
          }
          return settleCiToken(id, email, result.body, status, panel);
        });
    }
    if (session.state !== "Completed") {
      // Some halves may have been set; the cards show what the roster records.
      status.textContent = session.message || "That did not work. Start again.";
      return refresh(true);
    }
    if (panel.parentNode) { panel.parentNode.removeChild(panel); }
    showToast(session.message || ("CI token set from " + email), "ok");
    return refresh(true);
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
    // Generate CI token sets this itself. By hand it is the operator's own
    // record of when they ran `claude setup-token`; clearing it also clears
    // every token half the account backs. Nothing here reads GitHub or the token itself.
    var ciToken = element("input");
    ciToken.type = "date";
    ciToken.value = (account.roster && account.roster.ciTokenGeneratedOn) || "";
    labeled(form, "CI token generated on", ciToken);
    // The roles this account backs; unticking one takes it off on save.
    var ciRoles = (account.roster && account.roster.ciRoles) || [];
    var ciKeep = ciRoles.map(function (role) {
      var box = element("input");
      box.type = "checkbox";
      box.checked = true;
      labeled(form, "Backs " + role.name + " in " + roleWhere(role), box);
      return box;
    });
    // Roles to add by their token secrets, all set on the date above. It
    // records tokens set by hand, or a card whose roster write failed, so it
    // sits folded away; inside the form, so Save still reads it. The roles the
    // last Check roles found unclaimed are offered, read when it opens, and
    // none is ticked: the tool cannot tell whose token a secret holds.
    var repair = element("details", "repair");
    repair.appendChild(element("summary", null, "Repair a role record"));
    repair.appendChild(element("p", "muted",
      "Use this only when a CI token secret was set but this card does not show its role: tick each role this account's token is in, or name a secret below, and Save records them with the date above."));
    var offered = element("div");
    var offerBoxes = [];
    repair.appendChild(offered);
    repair.addEventListener("toggle", function () {
      if (!repair.open) { return; }
      // Redrawn on each open; a tick made before it was folded stays.
      var kept = offerBoxes.filter(function (offer) { return offer.box.checked; }).map(function (offer) { return offer.secret.name + "@" + offer.secret.organization; });
      offered.replaceChildren();
      var offers = repairOffers(lastRoleCheck, lastAccounts);
      if (!offers.length) {
        offered.appendChild(element("p", "muted", lastRoleCheck ? "The last Check roles found no unclaimed token." : "Run Check roles to list the unclaimed roles here."));
      }
      offerBoxes = offers.map(function (offer) {
        var box = element("input");
        box.type = "checkbox";
        box.checked = kept.indexOf(offer.secret.name + "@" + offer.secret.organization) >= 0;
        labeled(offered, offer.label, box);
        return { box: box, secret: offer.secret };
      });
    });
    var ciSecretName = element("input");
    ciSecretName.type = "text";
    ciSecretName.placeholder = "CLAUDE_CODE_OAUTH_TOKEN";
    labeled(repair, "CI secret name", ciSecretName);
    var ciSecretWhere = element("input");
    ciSecretWhere.type = "text";
    ciSecretWhere.placeholder = "owner/repository, or an organization";
    labeled(repair, "CI secret in", ciSecretWhere);
    form.appendChild(repair);
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
      var date = changedCiTokenDate(account.roster, ciToken.value);
      if (date !== undefined) { body.ciTokenGeneratedOn = date; }
      var removed = removedCiRoles(ciRoles, ciKeep.map(function (box) { return box.checked; }));
      if (removed) { body.removeCiRoles = removed; }
      var ticked = offerBoxes.filter(function (offer) { return offer.box.checked; }).map(function (offer) { return offer.secret; });
      var secrets = repairSecrets(ticked, ciSecretBody(ciSecretName.value, ciSecretWhere.value));
      if (secrets) { body.ciTokenSecrets = secrets; }
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
  // A login the token endpoint refused is expired whatever its recorded
  // expiry says.
  function loginExpired(account, at) {
    return !!account.loginDead || (!!account.loginExpiresAt && new Date(account.loginExpiresAt).getTime() <= at);
  }

  function loginSoon(account, at) {
    return loginExpired(account, at) || (!!account.loginExpiresAt && secondsUntil(account.loginExpiresAt, at) <= LOGIN_WARN_SECONDS);
  }

  // How old the login is and how long it has left, in one line: either half is
  // dropped with its instant, and the line itself when neither is known.
  function loginLine(account, at) {
    var parts = [];
    if (account.loggedInAt) { parts.push("logged in " + ago(account.loggedInAt, at)); }
    if (account.loginExpiresAt || account.loginDead) {
      parts.push(loginExpired(account, at) ? "login expired" : "login expires " + relative(account.loginExpiresAt, at));
    }
    return parts.length ? parts.join(" \u00b7 ") : null;
  }

  // Whether the card offers a login here. Not for a slot the other side holds,
  // and not while an online side is live on this account: a login here would
  // be a second live family, and a dead pair parked here is set aside by the
  // next hand-off instead.
  function loginOffered(account, at) {
    return !account.heldAway && !account.liveOn && (!account.hasCredentials || !!account.loginPaused || loginSoon(account, at));
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
    // Switch is still offered: it retries the paused login before it moves anything.
    if (account.loginPaused) { return "login paused"; }
    if (account.refresh.state === "stranded") { return "error"; }
    return "ready";
  }

  // A figure whose age and origin go unsaid is exactly what this card exists to
  // avoid, so the two travel together wherever a source is named. A time of day
  // alone reads as today: a figure cached before midnight, or a card nobody has
  // refreshed since last week, would look hours old instead of days, which is
  // the exact deceit this line exists to prevent. Same day, the time; any other
  // day, the date with it. The age follows, so nobody has to subtract, and a
  // figure past STALE_SECONDS says it may be out of date.
  function asOf(capturedAt, source, from) {
    var taken = new Date(capturedAt);
    var sameDay = taken.toDateString() === new Date(from).toDateString();
    return "as of " + (sameDay ? taken.toLocaleTimeString() : taken.toLocaleString()) + ", " + ago(capturedAt, from)
      + " via " + source + (stale(capturedAt, from) ? "; may be out of date" : "");
  }

  function stale(capturedAt, from) {
    return (from - new Date(capturedAt).getTime()) / 1000 > STALE_SECONDS;
  }

  // The class an "as of" line carries: muted when current, marked when stale.
  function asOfClass(capturedAt, from) {
    return stale(capturedAt, from) ? "asof stale" : "asof";
  }

  // How old, in seconds, the oldest figure behind the card's standing is: the
  // 5-hour, 7-day and model-scoped weekly rows a source carried, each dated by
  // its own source or the card's. The scoped row counts because a spent one
  // makes the account model-limited, and the statusline never refreshes it, so
  // fresh 5-hour and 7-day figures can sit beside an old Fable one (#191). A
  // row whose window has reset since counts too: the reset says the
  // old figure no longer applies, not that nothing has used the account since,
  // so it is only as current as the read (#188). Null when no such row exists.
  function usageAge(account, from) {
    var oldest = null;
    account.usage.limits.forEach(function (limit) {
      if ((limit.kind !== "session" && limit.kind !== "weekly_all" && limit.kind !== "weekly_scoped") || !limit.known) { return; }
      var taken = limit.capturedAt || account.usage.capturedAt;
      if (!taken) { return; }
      var age = Math.max(0, Math.round((from - new Date(taken).getTime()) / 1000));
      oldest = oldest === null || age > oldest ? age : oldest;
    });
    return oldest;
  }

  // Unknown first: no source carried this bucket at all. Then a window that has
  // reset since its capture, whose percentage now measures nothing.
  function reading(limit) {
    if (!limit.known) { return "unknown"; }
    if (limit.windowReset) { return "window reset since last read"; }
    return limit.percent === null ? "unknown" : percentText(limit.percent);
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

  // The bar, with a tick at each percentage in ticks (none when absent).
  function bar(limit, ticks) {
    var track = element("div", "bar");
    var fill = element("i", meterTone(limit));
    fill.style.width = barWidth(limit) + "%";
    track.appendChild(fill);
    (ticks || []).forEach(function (percent) {
      var tick = element("b", "tick");
      tick.style.left = percent + "%";
      tick.title = "alert at " + percentText(percent);
      track.appendChild(tick);
    });
    return track;
  }

  // A window that has already reset has no reset to count down to: "resets in
  // 0 s" beside "window reset since last read" is the same stale figure said twice.
  function resetLine(limit, at) {
    return limit.resetsAt && !limit.windowReset ? "resets " + relative(limit.resetsAt, at) : null;
  }

  // One window: its label and reading, the bar, when it resets, and, only for a
  // row taken from another source than the account's own, where it came from.
  function meter(limit, at, ticks) {
    var box = element("div", "meter");
    var line = element("div", "l");
    line.appendChild(element("span", "label", limit.label));
    line.appendChild(element("span", "pct " + meterTone(limit), reading(limit)));
    box.appendChild(line);
    box.appendChild(bar(limit, ticks));
    var reset = resetLine(limit, at);
    if (reset) { box.appendChild(element("small", null, reset)); }
    if (limit.source) { box.appendChild(element("small", stale(limit.capturedAt, at) ? "stale" : null, asOf(limit.capturedAt, limit.source, at))); }
    return box;
  }

  // The account's state in one sentence, worst first, and the tone it is said
  // in. The credential facts come before the quota: a card whose login has
  // expired is not "usable now" whatever its figures say.
  // The CI badges: one per role the account backs, naming the role and the
  // halves set from this account. A token half counts down to its one-year
  // expiry: setup-token asks for 365 days, so the expiry is reckoned from the
  // day it was set; within 30 days the badge warns, and past it the badge says
  // so. A date with no role is a marker set by hand and keeps its one badge.
  // Empty when the account backs nothing.
  function ciRoleBadges(roster, at, accounts, email) {
    if (!roster) { return []; }
    var roles = roster.ciRoles || [];
    if (!roles.length) {
      var marker = roster.ciTokenGeneratedOn ? tokenExpiry("CI token", roster.ciTokenGeneratedOn, "", at) : null;
      if (marker && marker.kind === "ok") { marker.text += " · " + roster.ciTokenGeneratedOn; }
      return marker ? [marker] : [];
    }
    return roles.map(function (role) {
      // The badge sits in a narrow column, so it names the role (and the
      // repository, for a repository's role) and the hover says the rest.
      var where = roleWhere(role);
      var halves = [role.tokenSetOn ? "token" : null, role.keySetOn ? "key" : null].filter(Boolean).join("+");
      // Split: another account holds the role's other half, because the half
      // set from here was set alone.
      var partner = splitPartner(role, accounts, email);
      var split = partner !== null;
      var text = ciRoleLabel(role) + " · " + halves + (split ? " · split" : "");
      var title = role.tokenSecret + (role.keySecret ? " and " + role.keySecret : "") + " in " + where + ". "
        + (role.keySetOn ? "Key set " + role.keySetOn + ". " : "")
        + (split ? "split: token from " + (role.tokenSetOn ? email : partner) + ", key from " + (role.keySetOn ? email : partner)
          + (role.keySetOn ? "; Generate CI token re-sends the token. " : "; Push API key re-sends the key. ") : "");
      var badge = (role.tokenSetOn && tokenExpiry(text, role.tokenSetOn, title, at)) || { kind: "ok", text: text, title: title.trim() };
      if (split && badge.kind === "ok") { badge.kind = "warn"; }
      return badge;
    });
  }

  // The other account that holds the half of a key-carrying role this one
  // lacks, or null: a role whose two secrets hold two accounts' values.
  function splitPartner(role, accounts, email) {
    if (!role.keySecret || !role.tokenSetOn === !role.keySetOn) { return null; }
    var owner = (role.repository || role.organization || "").toLowerCase();
    var found = (accounts || []).filter(function (account) {
      return account.email !== email && ((account.roster && account.roster.ciRoles) || []).some(function (other) {
        return other.tokenSecret.toLowerCase() === role.tokenSecret.toLowerCase()
          && (other.repository || other.organization || "").toLowerCase() === owner
          && (role.tokenSetOn ? other.keySetOn && !other.tokenSetOn : other.tokenSetOn && !other.keySetOn);
      });
    })[0];
    return found ? found.email : null;
  }

  // The roles whose token half was set on one date lose it together, a year
  // on. Lanes spend the API key first, so a role whose key half any account
  // holds (this one, or the other side of a split) keeps running on its key,
  // and one without stops. One entry per expiry date within the warning
  // window or past it, soonest first; the tone is tokenExpiry's.
  function ciTokenExpiries(roster, at, accounts, email) {
    var groups = {};
    ((roster && roster.ciRoles) || []).forEach(function (role) {
      if (!role.tokenSetOn) { return; }
      var life = tokenLife(role.tokenSetOn, at);
      if (!life || !life.tone) { return; }
      var key = life.expires.toISOString().slice(0, 10);
      var group = groups[key] || (groups[key] = { expiresOn: key, tone: life.tone, fallback: [], stop: [] });
      (role.keySetOn || splitPartner(role, accounts, email) ? group.fallback : group.stop).push(ciRoleLabel(role));
    });
    return Object.keys(groups).sort().map(function (key) { return groups[key]; });
  }

  function ciExpirySentence(expiry) {
    var past = expiry.tone === "crit";
    var parts = [];
    if (expiry.fallback.length) { parts.push((past ? "these roles now run on their API key only: " : "these roles fall back to their API key only: ") + expiry.fallback.join(", ")); }
    if (expiry.stop.length) { parts.push((past ? "these roles stopped: " : "these roles stop: ") + expiry.stop.join(", ")); }
    return (past ? "The token expired on " + expiry.expiresOn + "; " : "When the token expires on " + expiry.expiresOn + ", ") + parts.join("; ");
  }

  // A role's name as a badge and the expiry line say it: lowercase for a
  // prefix, and the repository's name after a repository's role.
  function ciRoleLabel(role) {
    return (role.prefix ? role.name.toLowerCase() : role.name)
      + (role.repository ? " (" + role.repository.split("/")[1] + ")" : "");
  }

  // The side strip's tag: "CI" on an account that backs a role or carries a
  // hand-set marker, else null.
  function ciTag(roster) {
    return roster && (roster.ciTokenGeneratedOn || (roster.ciRoles || []).length) ? "CI" : null;
  }

  // A token's one year: the day it expires, the whole days left from `at`
  // (negative once expired) and the tone: "crit" once expired, "warn" in the
  // last 30 days, else null. Null itself for a date that does not parse.
  function tokenLife(setOn, at) {
    var made = Date.parse(setOn + "T00:00:00Z");
    if (isNaN(made)) { return null; }
    var expires = new Date(made + 365 * 86400000);
    var days = Math.floor((expires.getTime() - at) / 86400000);
    return { expires: expires, days: days, tone: days < 0 ? "crit" : days <= 30 ? "warn" : null };
  }

  function tokenExpiry(text, setOn, title, at) {
    var life = tokenLife(setOn, at);
    if (!life) { return null; }
    var expires = life.expires;
    var days = life.days;
    title += "Token generated " + setOn + "; expires about " + expires.toISOString().slice(0, 10) + ".";
    if (life.tone === "crit") { return { kind: "crit", text: text + " · expired", title: title }; }
    if (life.tone === "warn") { return { kind: "warn", text: text + " · " + days + " d left", title: title }; }
    return { kind: "ok", text: text, title: title };
  }

  // The roles an Edit takes off, as the PATCH names them: each by its token
  // secret's name, which the server reads back into the same role. Null when
  // every box is still ticked. Only the unticked roles are sent, never the
  // ticked ones, so a form opened before another account took a role cannot
  // hand it back.
  function removedCiRoles(roles, keep) {
    var removed = roles.filter(function (_, index) { return !keep[index]; }).map(function (role) {
      return { tokenSecret: role.tokenSecret, repository: role.repository, organization: role.organization };
    });
    return removed.length ? removed : null;
  }

  // The "CI token generated on" an Edit sends: undefined when the field still
  // holds the date the form was drawn with, so a stale form neither clears
  // token halves set since nor turns a date the account has since lost into a
  // hand marker; else the new date, or null when it was cleared.
  function changedCiTokenDate(roster, value) {
    return value === ((roster && roster.ciTokenGeneratedOn) || "") ? undefined : value || null;
  }

  // The CI secret an Edit names: null when either field is blank, else its name
  // and, by whether it holds a slash, a repository or an organization. The
  // server holds both to GitHub's rules.
  function ciSecretBody(name, where) {
    var trimmedName = (name || "").trim();
    var trimmedWhere = (where || "").trim();
    if (!trimmedName || !trimmedWhere) { return null; }
    return trimmedWhere.indexOf("/") >= 0
      ? { name: trimmedName, repository: trimmedWhere }
      : { name: trimmedName, organization: trimmedWhere };
  }

  // The roles Edit's repair offers: each whose token secret the last Check
  // roles found with no account backing it and no account on the current
  // dashboard backs since, named as the PATCH names a secret. Only the token
  // half: the repair records no key.
  function repairOffers(check, accounts) {
    var held = {};
    (accounts || []).forEach(function (account) {
      ((account.roster && account.roster.ciRoles) || []).forEach(function (role) {
        held[(role.tokenSecret + "@" + (role.repository || role.organization)).toLowerCase()] = true;
      });
    });
    return ((check && check.roles) || []).filter(function (role) {
      return role.token === "unclaimed" && !held[(role.tokenSecret + "@" + role.organization).toLowerCase()];
    }).map(function (role) {
      return {
        label: "Record " + (role.prefix ? role.name.toLowerCase() : role.name) + " in org " + role.organization + " (" + role.tokenSecret + ")",
        secret: { name: role.tokenSecret, organization: role.organization }
      };
    });
  }

  // The secrets one repair save sends: the ticked offers, then the one typed
  // when it names another, or null when there are none.
  function repairSecrets(ticked, typed) {
    var secrets = ticked.slice();
    var key = function (secret) { return (secret.name + "@" + (secret.repository || secret.organization)).toLowerCase(); };
    if (typed && !secrets.some(function (secret) { return key(secret) === key(typed); })) { secrets.push(typed); }
    return secrets.length ? secrets : null;
  }

  // The CI panel's words: what happens after Start, before anything does. A
  // function, since the Node export runs before any var below is assigned.
  // The Start button names the browser profile it opens, from the account's
  // roster mapping, in the words the card uses; profile is that profile's card
  // label, or null when the mapping names no profile directory.
  function ciPanelWords(roster, profile) {
    var browser = roster && roster.browser;
    return {
      intro: "Pick the roles, then Start sign-in opens Anthropic's sign-in in this account's browser profile; paste the code it shows here.",
      detail: "The tool runs `claude setup-token` once and sets each role's token secret from it, and each role's key secret from this account's stored Console key. "
        + "Neither is shown here or written to disk. A role with a key half needs the Console key stored first (Set API key).",
      start: browser
        ? "Start sign-in (opens " + browserLine(browser, profile) + ")"
        : "Start sign-in (no browser mapped: you will get a link)"
    };
  }

  // The browser a roster entry opens, as the card and the Start button both
  // say it: the browser, then the profile's card label when the mapping names
  // a profile directory. With none the launcher passes no profile, so the
  // line names the browser alone.
  function browserLine(browser, profile) {
    return browser + (profile ? " / " + profile : "");
  }

  function cardProfileLabel(roster) {
    return roster && roster.browser && roster.browserProfileDirectory
      ? profileCardLabel(roster.browser, roster.browserProfileDirectory) : null;
  }

  // Where a role's secrets are: its repository, or its organization.
  function roleWhere(role) {
    return role.repository || "org " + role.organization;
  }

  // What the CI panel says when it has no role to offer: before any Check
  // roles, that one reads the organization's role secrets; after one, which
  // organizations could not be read, else that it found none. A new role can
  // be named either way.
  function ciNoRolesWords(check) {
    if (!check) {
      return "No roles known yet: Check roles reads your organization's role secrets from GitHub, or name a new role below.";
    }
    var failed = (check.failures || []).map(function (failure) { return "org " + failure.organization + " (" + failure.error + ")"; });
    return failed.length
      ? "Check roles could not read " + failed.join(", ") + ": check again, or name a new role below."
      : "Check roles found no role secrets: name a new role below.";
  }

  // The sentence after a key is stored, from the save's answer: the store the
  // server kept it in, then what sends it to GitHub. An Admin key never goes
  // to GitHub.
  function keyStoredWords(view, admin) {
    var stored = "Stored in " + view.storedIn + " on this machine.";
    if (admin) { return stored + " It is used only when you click Check API key."; }
    return stored + " " + ((view.pushesTo || []).length
      ? "Push API key sends it to " + view.pushesTo.join(", ") + "."
      : "No GitHub secret yet: use Generate CI token and pick a role with a key half.");
  }

  // Check roles: one read of the role secrets of every organization a role on
  // the roster names, and of the one typed, through `gh api`. A check that
  // answered is kept for the CI panel's role list, failures included.
  function checkRoles(organization, post) {
    return post("/api/roles/check", "POST", { organization: organization.trim() }).then(function (result) {
      if (result.ok) { lastRoleCheck = result.body; }
      return result;
    });
  }

  // The roles the CI panel offers. With a lane catalog read: its roles in its
  // order, then any role an account backs that it does not name. Without one:
  // every role an account on the roster backs, then every role the last Check
  // roles found that none does. Once each; an item names its token secret,
  // which the server reads back into the role.
  function assignableRoles(accounts, check, catalog) {
    var cataloged = catalog && (catalog.roles || []).length ? catalog.roles : null;
    var seen = {};
    var roles = [];
    function add(role, holder) {
      var where = role.repository || role.organization;
      var key = (role.tokenSecret + "@" + where).toLowerCase();
      if (seen[key]) { return; }
      seen[key] = true;
      var item = { tokenSecret: role.tokenSecret };
      item[role.repository ? "repository" : "organization"] = where;
      roles.push({
        item: item,
        holder: holder,
        keyed: !!role.keySecret,
        label: ciRoleLabel(role) + " in " + roleWhere(role)
          + (role.keySecret ? " · token+key" : " · token only") + " · " + (holder ? "backed by " + holder : "no holder")
      });
    }
    function holderOf(role) {
      var key = (role.tokenSecret + "@" + role.organization).toLowerCase();
      var backer = (accounts || []).filter(function (account) {
        return ((account.roster && account.roster.ciRoles) || []).some(function (held) {
          return (held.tokenSecret + "@" + (held.repository || held.organization)).toLowerCase() === key;
        });
      })[0];
      return backer ? backer.email : null;
    }
    (cataloged || []).forEach(function (role) {
      // An extra token secret is named by the secret itself, not a prefix.
      var extra = role.category === "extra";
      add({ name: extra ? role.tokenSecret : role.prefix, prefix: extra ? "" : role.prefix, repository: null, organization: role.organization, tokenSecret: role.tokenSecret, keySecret: role.keySecret }, holderOf(role));
    });
    (accounts || []).forEach(function (account) {
      ((account.roster && account.roster.ciRoles) || []).forEach(function (role) { add(role, account.email); });
    });
    if (!cataloged) {
      ((check && check.roles) || []).forEach(function (role) { add(role, role.holder || null); });
    }
    return roles;
  }

  // What the CI panel says about a configured lane catalog it cannot use yet:
  // not read, or not readable, when the server refuses every write it keys.
  // Null when none is configured or it has been read.
  function catalogNotice(catalog) {
    if (!catalog || !catalog.configured || (catalog.roles || []).length) { return null; }
    return catalog.error
      ? "The lane catalog could not be read (" + catalog.error + "), so Generate CI token and Push API key are refused until it is."
      : "The lane catalog has not been read yet; load it to see the roles and which of them take a key.";
  }

  // Which of the panel's roles start ticked: the ones this account backs, so
  // Generate CI token and Re-issue token both re-issue its own roles by default.
  function ciPanelChecked(roles, email) {
    return roles.map(function (role) { return role.holder === email; });
  }

  // The Re-issue token button on the card of an account that backs a CI role
  // (the server's roster.backsCiRole), or null: its label, and its click,
  // which hands the account to open (the CI token panel).
  function reissueButton(account, open) {
    if (!backsCiRole(account)) { return null; }
    return { label: "Re-issue token", click: function () { open(account); } };
  }

  function backsCiRole(account) {
    return !!(account && account.roster && account.roster.backsCiRole);
  }

  // Where a 7-day meter marks the server's alert thresholds: only on an
  // account that backs a CI role, the only kind an alert is raised for.
  function meterTicks(account, thresholds) {
    return backsCiRole(account) ? (thresholds || []) : [];
  }

  // A percentage as every figure on the page is written.
  function percentText(percent) {
    return Math.round(percent) + "%";
  }

  // The alert strip's lines, worst first as the server sent them. An alert
  // only says how close an account is; it acts on nothing by itself (see
  // bestSpareActions for the one button it carries).
  function alertLines(alerts, accounts) {
    return (alerts || []).map(function (alert) {
      var account = (accounts || []).filter(function (candidate) { return candidate.email === alert.account; })[0];
      var name = account ? uniqueName(account, accounts) : alert.account;
      return {
        kind: alert.level === "critical" ? "crit" : "warn",
        text: name + ": " + alert.window + " usage at " + percentText(alert.percent) + ", past " + percentText(alert.threshold)
      };
    });
  }

  // Assign to best spare, per alert line: one action for each role whose
  // token half the alerted account holds now, sending that token half to the
  // best spare the server named, through Assign to role. Only the token: the
  // alerted windows are the subscription's, which the CI token spends, and a
  // key half would need the cost questions. Only on the account's first (worst)
  // line, and none when no spare qualifies or the spare stores no CI token,
  // since Assign to role would refuse it. The roles come from the current
  // roster, so a role already moved drops its button on the next render.
  function bestSpareActions(alerts, accounts) {
    var seen = {};
    return (alerts || []).map(function (alert) {
      if (seen[alert.account]) { return []; }
      seen[alert.account] = true;
      var find = function (email) { return (accounts || []).filter(function (candidate) { return candidate.email === email; })[0]; };
      var alerted = find(alert.account);
      var spare = alert.bestSpare ? find(alert.bestSpare) : null;
      if (!alerted || !spare || !(spare.roster && spare.roster.ciTokenStoredOn)) { return []; }
      return ((alerted.roster && alerted.roster.ciRoles) || []).filter(function (role) { return role.tokenSetOn; }).map(function (role) {
        var item = { tokenSecret: role.tokenSecret };
        item[role.repository ? "repository" : "organization"] = role.repository || role.organization;
        return {
          spare: spare.email,
          body: { role: item, halves: "token" },
          label: "Assign " + ciRoleLabel(role) + " to best spare " + uniqueName(spare, accounts)
        };
      });
    });
  }

  // A role named in the panel's free-text row, or null when the prefix is
  // blank: the request item and the confirmation naming its two secrets. It
  // says "creates" for a secret the last Check roles did not list and "sets"
  // for one it did, and no key secret for a prefix in tokenOnly, the
  // dashboard's list of roles whose lanes read no key. A role in one
  // repository is refused, as the server refuses it, with the reason in error.
  function newRoleItem(prefix, where, check, tokenOnly) {
    var name = (prefix || "").trim().toUpperCase();
    var place = (where || "").trim();
    if (!name) { return null; }
    if (place.indexOf("/") >= 0) {
      return { error: "A role's secrets are set on the organization, never on one repository: name an organization." };
    }
    var item = { prefix: name, organization: place };
    var secrets = (tokenOnly || []).indexOf(name) >= 0
      ? [name + "_CLAUDE_CODE_OAUTH_TOKEN"]
      : [name + "_CLAUDE_CODE_OAUTH_TOKEN", name + "_CLAUDE_API_KEY"];
    var listed = function (secret) {
      return ((check && check.roles) || []).some(function (role) {
        return (role.organization || "").toLowerCase() === place.toLowerCase()
          && ((role.hasToken && role.tokenSecret === secret) || (role.hasKey && role.keySecret === secret));
      });
    };
    var created = secrets.filter(function (secret) { return !listed(secret); });
    var kept = secrets.filter(listed);
    var parts = [];
    if (created.length) { parts.push("creates " + created.join(" and ") + ", visible to all repositories"); }
    if (kept.length) { parts.push("sets " + kept.join(" and ") + ", keeping who may read " + (kept.length > 1 ? "them" : "it")); }
    return { item: item, confirm: "In org " + place + " this " + parts.join(", and ") + ". Go ahead?" };
  }

  // The accounts a start moves roles off: every other account on the last
  // dashboard whose roster holds either half of a picked role, ticked or
  // typed, once each. A role matches by its token secret and organization or
  // repository, without case, as assignableRoles keys it. Empty when no other
  // account holds one, and then nothing is checked.
  function limitStopSources(picked, accounts, email) {
    function key(role) {
      var secret = role.tokenSecret || (role.prefix + "_CLAUDE_CODE_OAUTH_TOKEN");
      return (secret + "@" + (role.repository || role.organization || "")).toLowerCase();
    }
    var wanted = (picked || []).map(key);
    var froms = [];
    (accounts || []).forEach(function (other) {
      if (other.email === email || froms.indexOf(other.email) >= 0) { return; }
      var holds = ((other.roster && other.roster.ciRoles) || []).some(function (role) {
        return (role.tokenSetOn || role.keySetOn) && wanted.indexOf(key(role)) >= 0;
      });
      if (holds) { froms.push(other.email); }
    });
    return froms;
  }

  // Whether the move goes ahead: at once when no account moved off is near
  // a limit, else on the operator's answer to the warning (confirm resolves
  // true for Move anyway).
  function limitStopGate(froms, accounts, confirm) {
    var notice = limitStopNotice(froms, accounts);
    return Promise.resolve(notice ? confirm(notice) : true);
  }

  // The warning's lines, or null when no account moved off is near a limit:
  // a known 5-hour or 7-day figure at 90% or more, whose window has not
  // reset, names the account, the figure and its reset (the meters' crit
  // tier; a source's severity is optional, so the figure decides). A reset
  // window counts as read, with no figure; an account with neither window
  // read says so. The figures are the claude
  // binary's own reads, so no GitHub call is made. A Console key's credit
  // stop has no such figure.
  function limitStopNotice(froms, accounts) {
    var lines = [];
    froms.forEach(function (from) {
      var account = (accounts || []).filter(function (other) { return other.email === from; })[0];
      var known = ((account && account.usage && account.usage.limits) || []).filter(function (limit) {
        return (limit.kind === "session" || limit.kind === "weekly_all") && limit.known && (limit.windowReset || typeof limit.percent === "number");
      });
      if (!known.length) {
        lines.push({ kind: "warn", text: from + ": no usage read yet, so its limits were not checked." });
        return;
      }
      known.forEach(function (limit) {
        if (limit.windowReset || limit.percent < 90) { return; }
        lines.push({
          kind: "crit",
          text: from + ": at " + limit.percent + "% of its " + limit.label + " limit"
            + (limit.resetsAt ? ", resets " + new Date(limit.resetsAt).toLocaleString() : "") + "."
        });
      });
    });
    if (lines.length) { lines.push({ kind: "warn", text: "Credit stops on Console keys are not checked." }); }
    return lines.length ? lines : null;
  }

  // The PUT body for a pasted Console API key, trimmed, or null when nothing
  // but whitespace was pasted.
  function apiKeyBody(pasted) {
    var key = (pasted || "").trim();
    return key ? { key: key } : null;
  }

  // The toast after a keepStoredKey delete: what was done, then the warning
  // naming the key left behind when there is one, never a bare "null".
  function keptKeyToast(done, result) {
    if (!result.ok) { return { text: refused(result.body), kind: "error" }; }
    var warning = result.body && result.body.warning;
    return warning ? { text: done + ". " + warning, kind: "warn" } : { text: done, kind: "ok" };
  }

  // The card's key badge: the day the key was stored, from the roster, so
  // the page never reads the store to draw it. Null when none is stored.
  // The card's badge for a CI token kept in the OS store: the day only, never the token.
  function ciTokenBadge(roster) {
    var setOn = roster && roster.ciTokenStoredOn;
    return setOn ? { text: "CI token", title: "CI token stored in this machine's OS store on " + setOn + "; Assign to role can send it." } : null;
  }

  // Which halves Assign to role can send for one role: each half this
  // machine stores, the key only for a role that takes one. Each choice
  // carries its value for the route, and `why` says what is missing.
  function assignHalves(roster, role) {
    var token = !!(roster && roster.ciTokenStoredOn);
    var key = !!(roster && roster.consoleApiKeySetOn) && !!(role && role.keyed);
    var choices = [];
    if (token && key) { choices.push({ value: "both", label: "Token and key" }); }
    if (token) { choices.push({ value: "token", label: "Token only" }); }
    if (key) { choices.push({ value: "key", label: "Key only" }); }
    var missing = [];
    if (!token) { missing.push("no CI token is stored (Generate CI token or Store CI token)"); }
    if (role && role.keyed && !(roster && roster.consoleApiKeySetOn)) { missing.push("no API key is stored (Set API key)"); }
    if (role && !role.keyed && roster && roster.consoleApiKeySetOn) { missing.push("the key, since this role's lanes read a token only (lane catalog)"); }
    return { choices: choices, why: missing.length ? missing.join("; ") : null };
  }

  // The body Store CI token sends, or null for a blank field. Trimmed, since a
  // paste often brings a line break; the server checks the token's shape.
  function ciTokenValueBody(value) {
    var token = String(value || "").trim();
    return token ? { token: token } : null;
  }

  function apiKeyBadge(roster) {
    var setOn = roster && roster.consoleApiKeySetOn;
    return setOn ? { text: "API key", title: "Console API key stored in this machine's OS store on " + setOn + "." } : null;
  }

  // The toast after Check API key: the key's status, or why it cannot be
  // told which listed key is this one. Archived, inactive and expired are
  // errors, since CI spending this key fails; a near expiry is a warning.
  function apiKeyCheckToast(result) {
    if (!result.ok) { return { text: refused(result.body), kind: "error" }; }
    var view = result.body;
    if (view.outcome !== "found") {
      return { text: "Cannot tell which listed key is this account's Console API key: " + view.reason + ".", kind: "warn" };
    }
    var named = "The Console API key" + (view.name ? " \"" + view.name + "\"" : "");
    var expires = view.expiresAt ? String(view.expiresAt).slice(0, 10) : null;
    switch (view.flag) {
      case "archived":
      case "inactive":
        return { text: named + " is " + view.flag + ".", kind: "error" };
      case "expired":
        return { text: named + " has expired" + (expires ? " (" + expires + ")" : "") + ".", kind: "error" };
      case "expiresSoon":
        return { text: named + " expires on " + expires + ".", kind: "warn" };
      default:
        return { text: named + " is " + view.status + (expires ? ", expiring " + expires : "") + ".", kind: "ok" };
    }
  }

  // What Check roles found, one line per role and one per organization that
  // could not be read. A line is crit when a half the roster records changed
  // outside the tool or is gone, warn when a role has no holder or a half's
  // change cannot be told apart, and ok otherwise.
  function roleCheckLines(view) {
    var words = {
      ok: "ok",
      changed: "changed outside the tool",
      unknown: "unknown",
      missing: "missing",
      unclaimed: "unclaimed"
    };
    // Why a half is unknown, one cause each, as the README gives them.
    var reasons = {
      noInstant: "unknown (only the day it was set is recorded, and GitHub shows that same day)",
      olderThanWrite: "unknown (GitHub lists it as older than the tool's write)",
      noListedTime: "unknown (GitHub listed no readable update time)"
    };
    var lines = (view.roles || []).map(function (role) {
      var halves = [role.hasToken ? "token" : null, role.hasKey ? "key" : null].filter(Boolean).join("+") || "no secrets";
      var verdicts = [["token", role.token, role.tokenReason], ["key", role.key, role.keyReason]].filter(function (pair) { return pair[1]; });
      var text = (role.prefix ? role.name.toLowerCase() : role.name) + " in org " + role.organization + " · " + halves
        + " · " + (role.holder || "no holder")
        + verdicts.map(function (pair) { return " · " + pair[0] + ": " + ((pair[1] === "unknown" && reasons[pair[2]]) || words[pair[1]] || pair[1]); }).join("");
      var values = verdicts.map(function (pair) { return pair[1]; });
      var kind = values.some(function (value) { return value === "changed" || value === "missing"; }) ? "crit"
        : values.some(function (value) { return value === "unclaimed" || value === "unknown"; }) ? "warn" : "ok";
      return { kind: kind, text: text, title: role.tokenSecret + (role.keySecret ? " and " + role.keySecret : "") };
    });
    (view.failures || []).forEach(function (failure) {
      lines.push({ kind: "crit", text: "org " + failure.organization + " could not be read: " + failure.error, title: "" });
    });
    return lines;
  }

  // The kill switches Check roles read, one row each. The server decides
  // running or stopped. An organization switch that exists and was read
  // carries the toggle that flips it; a repository switch never does, and
  // neither does one that is missing or could not be read.
  function killSwitchRows(view) {
    return (view.killSwitches || []).map(function (item) {
      var where = item.scope === "repository" ? " in " + item.owner : " in org " + item.owner;
      var said = item.error ? "could not be read: " + item.error
        : item.state + " (" + (item.exists ? item.value : "not set") + ")";
      var toggle = null;
      if (item.writable && item.scope === "organization" && item.exists && item.state) {
        var stopping = item.state === "running";
        toggle = {
          label: stopping ? "Stop" : "Start",
          question: (stopping ? "Stop" : "Start") + " every lane " + item.name + " governs in org " + item.owner + "?\n\n"
            + "This sets the organization variable " + item.name + " to " + (stopping ? "true" : "false") + " on GitHub, sending back the visibility it has now.",
          body: { org: item.owner, name: item.name, value: stopping ? "true" : "false" }
        };
      }
      return {
        kind: item.error ? "crit" : item.state === "running" ? "ok" : "warn",
        text: item.name + where + " · " + said + (item.writable ? "" : " · read-only"),
        toggle: toggle
      };
    });
  }

  // Flips one switch only once the operator confirms; a cancel sends nothing.
  function toggleKillSwitch(toggle, ask, post) {
    if (!toggle || !ask(toggle.question)) { return Promise.resolve(null); }
    return post("/api/kill-switches", "POST", toggle.body);
  }

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
    // A spent model-scoped weekly window, the other windows open: never
    // recommended, still switchable by hand for work on another model.
    if (account.standing === "model-limited") {
      return { kind: "crit", text: (account.limitedModel || "Model") + " weekly limit reached" + (account.nextResetAt ? ", resets " + relative(account.nextResetAt, at) : "") };
    }
    if (chip === "paused" || account.standing === "paused") { return { kind: "paused", text: "Paused" }; }
    if (account.standing === "unread") { return { kind: "", text: "No usage read yet" }; }
    // A figure older than STALE_SECONDS may no longer be true, so the card
    // says how old it is instead of claiming the account is usable now.
    var age = usageAge(account, at);
    var outOfDate = age !== null && age > STALE_SECONDS ? "; read " + span(age) + " ago, may be out of date" : "";
    var near = nearestLimit(account.usage.limits);
    if (near) {
      var reset = resetLine(near, at);
      return { kind: meterTone(near), text: "Near the " + near.label + " limit" + (reset ? ", " + reset : "") + outOfDate };
    }
    if (outOfDate) { return { kind: "warn", text: "Was usable" + outOfDate }; }
    // A window no source carried may be the spent one.
    var gap = unknownWindow(account);
    if (gap) { return { kind: "warn", text: "Not verified: " + gap + " figure unknown" }; }
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
    if (account.usage.source) { lines.appendChild(element("p", asOfClass(account.usage.capturedAt, at), asOf(account.usage.capturedAt, account.usage.source, at))); }
    // The row's two columns are the 5-hour and 7-day windows; any other window
    // the endpoint reported, and a column taken from another source, say so here.
    account.usage.limits.forEach(function (limit) {
      var column = limit.kind === "session" || limit.kind === "weekly_all";
      if (!column && limit.known) {
        var reset = resetLine(limit, at);
        lines.appendChild(element("p", "asof", limit.label + " " + reading(limit) + (reset ? ", " + reset : "")));
      }
      if (limit.source) { lines.appendChild(element("p", asOfClass(limit.capturedAt, at), limit.label + " " + asOf(limit.capturedAt, limit.source, at))); }
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
      var browser = browserLine(roster.browser, cardProfileLabel(roster));
      // One line, cut short with the whole of it on hover.
      lines.appendChild(element("p", "muted browser", browser)).title = browser;
    }
    return lines;
  }

  // One window in a row: the bar, the reading, and when it resets.
  function cell(limit, at, ticks) {
    var box = element("div", "u");
    if (!limit) { return box; }
    // The column header is hidden on narrow screens and from screen readers,
    // so each cell names its own window.
    box.appendChild(element("span", "cell-label", limit.label));
    box.appendChild(bar(limit, ticks));
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
    ciRoleBadges(roster, at, lastAccounts, account.email).forEach(function (ci) {
      var ciBadge = element("span", "badge ci-token " + ci.kind, ci.text);
      ciBadge.title = ci.title;
      badges.appendChild(ciBadge);
    });
    var tokenBadge = ciTokenBadge(roster);
    if (tokenBadge) {
      var tokenChip = element("span", "badge api-key", tokenBadge.text);
      tokenChip.title = tokenBadge.title;
      badges.appendChild(tokenChip);
    }
    var keyBadge = apiKeyBadge(roster);
    if (keyBadge) {
      var keyChip = element("span", "badge api-key", keyBadge.text);
      keyChip.title = keyBadge.title;
      badges.appendChild(keyChip);
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
    ciTokenExpiries(roster, at, lastAccounts, account.email).forEach(function (expiry) {
      status.appendChild(element("p", "say " + expiry.tone, ciExpirySentence(expiry)));
    });
    line.appendChild(status);

    function limitOf(kind) { return account.usage.limits.filter(function (limit) { return limit.kind === kind; })[0]; }
    line.appendChild(cell(limitOf("session"), at));
    line.appendChild(cell(limitOf("weekly_all"), at, meterTicks(account, dashboard.alertThresholds)));

    var act = element("div", "act");
    // Beside the meters, so an account near its 7-day limit can have its
    // roles' token re-issued without opening the menu. It opens the same
    // panel as Generate CI token, with this account's roles ticked.
    var reissue = reissueButton(account, openCiTokenPanel);
    if (reissue) { act.appendChild(actionButton(reissue.label, "secondary sm", reissue.click)); }
    // The live account is signed in already; logging it into its parked folder
    // would leave one account holding two logins. A card with a login good for
    // longer than the warning window does not need the button on its face; an
    // expired login is inside that window, so the one test covers both.
    // A slot the other side holds is empty for a reason, and logging into it
    // would put a second token family on the machine. The route refuses it
    // anyway; the button goes so the operator is not sent at a 409.
    var needsLogin = loginOffered(account, at);
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
      menu.appendChild(menuItem(more, "Open claude.ai", "quiet sm", function () { openAccountPage(account.email, "claude-ai", "claude.ai"); }));
      menu.appendChild(menuItem(more, "Open Console", "quiet sm", function () { openAccountPage(account.email, "keys", "the Console's API keys page"); }));
      menu.appendChild(menuItem(more, "Generate CI token", "quiet sm", function () { openCiTokenPanel(account); }));
      menu.appendChild(menuItem(more, roster.ciTokenStoredOn ? "Replace CI token" : "Store CI token", "quiet sm", function () { openCiTokenValuePanel(account); }));
      if (roster.ciTokenStoredOn) {
        menu.appendChild(menuItem(more, "Delete CI token", "quiet sm", function () { clearCiTokenValue(account.email); }));
      }
      if (roster.ciTokenStoredOn || roster.consoleApiKeySetOn) {
        menu.appendChild(menuItem(more, "Assign to role", "quiet sm", function () { openAssignPanel(account); }));
      }
    }
    if (roster) {
      menu.appendChild(menuItem(more, roster.consoleApiKeySetOn ? "Replace API key" : "Set API key", "quiet sm", function () { openApiKeyPanel(account); }));
      if (roster.consoleApiKeySetOn && (roster.ciRoles || []).some(function (role) { return role.keySecret; })) {
        menu.appendChild(menuItem(more, "Push API key", "quiet sm", function () { openApiKeyPanel(account); }));
      }
      if (roster.consoleApiKeySetOn) {
        menu.appendChild(menuItem(more, "Delete API key", "quiet sm", function () { clearApiKey(account.email); }));
      }
      menu.appendChild(menuItem(more, roster.adminKeySetOn ? "Replace Admin key" : "Set Admin key", "quiet sm", function () { openApiKeyPanel(account, true); }));
      if (roster.adminKeySetOn) {
        menu.appendChild(menuItem(more, "Delete Admin key", "quiet sm", function () { clearApiKey(account.email, true); }));
      }
      if (roster.consoleApiKeySetOn && roster.adminKeySetOn) {
        menu.appendChild(menuItem(more, "Check API key", "quiet sm", function () { checkApiKey(account.email); }));
      }
    }
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
    var alerts = alertLines(dashboard.alerts, dashboard.accounts);
    alertStrip.replaceChildren();
    alertStrip.hidden = alerts.length === 0;
    var spareActions = bestSpareActions(dashboard.alerts, dashboard.accounts);
    alerts.forEach(function (alert, index) {
      var line = element("li", alert.kind, alert.text);
      spareActions[index].forEach(function (action) {
        line.appendChild(document.createTextNode(" "));
        // Rebuilt by every poll, so it takes the running mutation's lock as it is drawn.
        line.appendChild(blockable(actionButton(action.label, "sm", function () { assignToBestSpare(action); }), false));
      });
      alertStrip.appendChild(line);
    });
    if (alerts.length && dashboard.alertsComputedAt) {
      alertStrip.appendChild(element("li", "asof", "as of the last refresh, " + new Date(dashboard.alertsComputedAt).toLocaleTimeString()));
    }
    renderNow();
    // Rendering rebuilds every row, so the ten-second poll would otherwise wipe an
    // Edit panel, a half-typed login code, a display name being typed, or an open
    // row menu, out from under whoever is using it. A mutation's own render
    // passes force, since that one has to show the result.
    if (!force && cards.querySelector("details.edit[open], details.login[open], input.rename, details.more[open]")) { return; }
    // The hazard is a row moving, not a row being redrawn. Switch fires without a
    // confirm unless Remote Control is running, so an order that changes while the pointer rests on the list, or
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
    }, at);
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

  // Check roles: lists the GitHub secrets of every organization a role on the
  // roster names, and of the one typed here, through `gh api`. Only this
  // button reads GitHub; no load, poll or refresh does.
  (function () {
    var panel = element("details", "roster-add");
    var summary = element("summary", "btn quiet sm", "Check roles");
    panel.appendChild(summary);
    var form = element("form", "roster-form popover");
    var organization = element("input");
    organization.type = "text";
    organization.autocomplete = "off";
    organization.placeholder = "octo-org";
    labeled(form, "Organization (optional when a role names one)", organization);
    var check = element("button", "btn primary", "Check");
    check.type = "submit";
    form.appendChild(check);
    var status = element("p", "muted");
    var found = element("ul");
    form.appendChild(status);
    form.appendChild(found);
    panel.appendChild(form);
    var stop = document.getElementById("stop");
    stop.parentNode.insertBefore(panel, stop);
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      busy = true;
      setButtonsDisabled(true);
      status.textContent = "Reading GitHub secrets...";
      found.replaceChildren();
      checkRoles(organization.value, send)
        .then(function (result) {
          if (!result.ok) { status.textContent = refused(result.body); return; }
          var lines = roleCheckLines(result.body);
          status.textContent = lines.length ? "Checked " + new Date(result.body.checkedAt).toLocaleString() : "No role secrets found.";
          lines.forEach(function (line) {
            var item = element("li", line.kind === "ok" ? "muted" : "say " + line.kind, line.text);
            if (line.title) { item.title = line.title; }
            found.appendChild(item);
          });
          killSwitchRows(result.body).forEach(function (row) { found.appendChild(killSwitchItem(row)); });
        })
        .catch(function (error) { status.textContent = "Request failed: " + error; })
        .then(function () { busy = false; setButtonsDisabled(false); });
    });

    // One kill switch, with its toggle when it has one. A flip that went
    // through redraws the row from the server's re-read.
    function killSwitchItem(row) {
      var item = element("li", row.kind === "ok" ? "muted" : "say " + row.kind, row.text);
      if (!row.toggle) { return item; }
      item.appendChild(document.createTextNode(" "));
      item.appendChild(actionButton(row.toggle.label, "secondary sm", function () {
        busy = true;
        setButtonsDisabled(true);
        toggleKillSwitch(row.toggle, function (question) { return window.confirm(question); }, send)
          .then(function (result) {
            if (!result) { return; }
            if (!result.ok) { showToast(refused(result.body), "error"); return; }
            item.replaceWith(killSwitchItem(killSwitchRows({ killSwitches: [result.body] })[0]));
          })
          .catch(function (error) { showToast("Request failed: " + error, "error"); })
          .then(function () { busy = false; setButtonsDisabled(false); });
      }));
      return item;
    }
  })();

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
    mountHistory();
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

  // The lane history: read only when the operator opens it, never on load or
  // by timer, and only the last 30 days.
  function mountHistory() {
    if (document.querySelector("details.history")) { return; }
    var panel = element("details", "history");
    panel.appendChild(element("summary", "quiet sm", "History"));
    var list = element("ul", "warnings");
    panel.appendChild(list);
    panel.addEventListener("toggle", function () {
      if (!panel.open) { return; }
      var since = new Date(Date.now() - 30 * 86400000).toISOString();
      fetch("/api/lane-history?since=" + encodeURIComponent(since), { headers: withBearer({ "Accept": "application/json" }) })
        .then(function (response) {
          if (response.status === 401) { loseToken(); return null; }
          if (!response.ok) { return { ok: false, status: response.status }; }
          return response.json().then(function (entries) { return { ok: true, entries: entries }; });
        })
        .catch(function () { return { ok: false }; })
        .then(function (read) {
          if (read === null) { return; }
          list.innerHTML = "";
          historyReadLines(read).forEach(function (line) { list.appendChild(element("li", null, line)); });
        });
    });
    cards.parentNode.appendChild(panel);
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
