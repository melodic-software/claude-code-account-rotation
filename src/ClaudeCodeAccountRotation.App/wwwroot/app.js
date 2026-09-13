(function () {
  "use strict";

  var POLL_MS = 10000;
  var BROWSERS = ["", "chrome", "edge", "brave"];
  // The select value that means "this profile is not in the list"; every other
  // value is an index into profileCatalog, because a directory name can hold
  // any character and a joined key would break on the separator.
  var OTHER = "other";
  var cards = document.getElementById("cards");
  var banner = document.getElementById("banner");
  var warnings = document.getElementById("warnings");
  var captured = document.getElementById("captured");
  var refreshAllButton = document.getElementById("refresh-all");
  var refreshStateLine = document.getElementById("refresh-state");
  var toast = document.getElementById("toast");
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

  // One row per field: a visible label wrapping its own control, so no id is
  // needed and the Add form's rows cannot collide with a card's Edit rows.
  function labelled(container, text, field) {
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

  // The alias, browser, and profile fields, built once and used both by the Add
  // form and by each card's Edit panel, so the two can never drift apart.
  function accountFields(container, entry) {
    var values = entry || {};
    var alias = element("input");
    alias.type = "text";
    alias.placeholder = "optional, e.g. weekly";
    alias.value = values.alias || "";
    labelled(container, "Alias", alias);

    var browser = element("select");
    BROWSERS.forEach(function (name) {
      var option = element("option", null, name === "" ? "no browser mapped" : name);
      option.value = name;
      browser.appendChild(option);
    });
    browser.value = values.browser || "";
    labelled(container, "Browser", browser);

    var profile = profileSelect();
    labelled(container, "Browser profile", profile);

    var typed = element("input");
    typed.type = "text";
    typed.placeholder = "e.g. Profile 3";
    var typedRow = labelled(container, "Profile directory", typed);

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
    var options = {
      method: method,
      headers: { "X-Claude-Code-Account-Rotation": "1", "Accept": "application/json" }
    };
    if (body) {
      options.headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    return fetch(path, options).then(function (response) {
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
        if (!result.ok) {
          showToast(refused(result.body), "error");
          return null;
        }
        openLoginPanel(email, result.body);
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

  function editPanel(account) {
    var panel = element("details", "edit");
    panel.appendChild(element("summary", null, "Edit"));
    var form = element("form", "roster-form");
    var fields = accountFields(form, account.roster);
    var save = element("button", null, "Save");
    save.type = "submit";
    form.appendChild(save);
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      panel.open = false;
      mutate(accountPath(account.email), "PATCH", fields.read(), function (result) {
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

  function relative(instant, from) {
    var seconds = secondsUntil(instant, from);
    if (seconds < 60) { return "in " + seconds + " s"; }
    var minutes = Math.round(seconds / 60);
    if (minutes < 60) { return "in " + minutes + " min"; }
    var hours = Math.floor(minutes / 60);
    return hours < 48 ? "in " + hours + " h " + (minutes % 60) + " min" : "in " + Math.round(hours / 24) + " d";
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

  function limitRow(limit, at) {
    var row = element("div", "limit");
    row.appendChild(element("span", "label", limit.label));
    var bar = element("div", "bar");
    var fill = element("div", "fill");
    fill.style.width = barWidth(limit) + "%";
    fill.setAttribute("data-severity", limit.severity || "");
    bar.appendChild(fill);
    row.appendChild(bar);
    row.appendChild(element("span", "reading", reading(limit)));
    // A window that has already reset has no reset to count down to: "resets in
    // 0 s" beside "window reset since last read" is the same stale figure said
    // twice.
    if (limit.resetsAt && !limit.windowReset) { row.appendChild(element("span", "resets", "resets " + relative(limit.resetsAt, at))); }
    // Only a row taken from another source than the card's own says where it
    // came from; otherwise the card's single "as of" line speaks for it.
    if (limit.source) { row.appendChild(element("span", "asof", asOf(limit.capturedAt, limit.source, at))); }
    return row;
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

  function usage(account, dashboard) {
    var section = element("div", "usage");
    var at = new Date(dashboard.capturedAt).getTime();
    account.usage.limits.forEach(function (limit) { section.appendChild(limitRow(limit, at)); });
    if (account.usage.source) {
      section.appendChild(element("p", "asof", asOf(account.usage.capturedAt, account.usage.source, at)));
    }
    if (account.usage.credits) { section.appendChild(element("p", "asof", creditsLine(account.usage.credits))); }
    if (account.usageNote) { section.appendChild(element("p", "muted", account.usageNote)); }
    var state = refreshState(account, dashboard);
    if (state) { section.appendChild(element("p", "refresh-state", state)); }
    return section;
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

  function render(dashboard, force) {
    // The header is not part of any card, so it is written before the guard
    // below and on every poll: an operator with an Edit panel open would
    // otherwise watch the page's own timestamp, the pass's progress, and the
    // Refresh all button freeze at whatever they said when the panel opened.
    captured.textContent = "as of " + new Date(dashboard.capturedAt).toLocaleTimeString();
    refreshStateLine.textContent = passState(dashboard);
    refreshAllButton.disabled = busy || dashboard.refresh.inProgress;
    // Rendering rebuilds every card, so the ten-second poll would otherwise wipe an
    // Edit panel, or a half-typed login code, out from under whoever is typing it.
    // A mutation's own render passes force, since that one has to show the result.
    if (!force && cards.querySelector("details.edit[open], details.login[open]")) { return; }
    banner.hidden = !dashboard.banner;
    banner.textContent = dashboard.banner || "";
    warnings.innerHTML = "";
    warnings.hidden = dashboard.warnings.length === 0;
    dashboard.warnings.forEach(function (warning) { warnings.appendChild(element("li", null, warning)); });

    cards.innerHTML = "";
    cardNodes = {};
    dashboard.accounts.forEach(function (account) {
      var roster = account.roster;
      var paused = !!(roster && roster.paused);
      var card = element("section", "card" + (account.isLive ? " live" : "") + (paused ? " paused" : ""));
      card.appendChild(element("h2", null, (roster && roster.alias) ? roster.alias + " (" + account.email + ")" : account.email));

      var badges = element("div", "badges");
      if (account.isLive) { badges.appendChild(element("span", "badge live", "live")); }
      if (paused) { badges.appendChild(element("span", "badge paused", "paused")); }
      if (!account.hasCredentials && !account.isLive) { badges.appendChild(element("span", "badge needs-login", "needs login")); }
      if (!roster) { badges.appendChild(element("span", "badge off-roster", "not on roster")); }
      card.appendChild(badges);

      if (roster && roster.browser) {
        card.appendChild(element("p", "muted", roster.browser + (roster.browserProfileDirectory ? " / " + profileCardLabel(roster.browser, roster.browserProfileDirectory) : "")));
      }

      card.appendChild(usage(account, dashboard));

      var actions = element("div", "actions");
      var switchButton = actionButton(account.isLive ? "Live now" : "Switch", "switch", function () { switchTo(account.email); });
      // A paused account is out of the ranked queue, not off the page: the operator
      // can still switch to it by hand. A stranded folder is the exception: its
      // credential file is the dead lineage, and switching would move it live
      // where no restore can reach it.
      switchButton.disabled = account.isLive || !account.hasCredentials || busy || !!dashboard.banner
        || dashboard.refresh.inProgress || account.refresh.state === "stranded";
      actions.appendChild(switchButton);

      var refreshButton = actionButton("Refresh", "secondary", function () { refreshAccount(account.email); });
      refreshButton.disabled = busy || dashboard.refresh.inProgress;
      actions.appendChild(refreshButton);

      if (roster) {
        actions.appendChild(actionButton(paused ? "Resume" : "Pause", "secondary", function () { setPaused(account.email, !paused); }));
      } else if (account.isLive) {
        actions.appendChild(actionButton("Adopt", "secondary", function () { adopt(account.email); }));
      }

      // The live account is signed in already; logging it into its parked folder
      // would leave one account holding two logins.
      if (roster && !account.isLive) {
        actions.appendChild(actionButton(
          account.hasCredentials ? "Log in again" : "Login",
          "secondary",
          function () { startLogin(account.email); }));
      }

      if (!account.isLive) {
        actions.appendChild(actionButton("Remove", "danger", function () { remove(account.email); }));
      }

      card.appendChild(actions);
      if (roster) { card.appendChild(editPanel(account)); }
      cardNodes[account.email] = card;
      cards.appendChild(card);
    });
  }

  function refresh(force) {
    return fetch("/api/dashboard", { headers: { "Accept": "application/json" } })
      .then(function (response) { return response.json(); })
      .then(function (dashboard) { render(dashboard, force); })
      .catch(function (error) { showToast("Dashboard unavailable: " + error, "error"); });
  }

  function setButtonsDisabled(disabled) {
    // Synchronously, at click time: a second click during the lock wait would
    // otherwise send a second request whose refusal toast overwrote the outcome of
    // the first. The re-enable is unconditional and the following render applies
    // the per-account state, so a failed request can never leave a button dead.
    Array.prototype.forEach.call(document.querySelectorAll("button"), function (button) { button.disabled = disabled; });
  }

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

  addEmail.addEventListener("input", function () {
    if (addFields) { addFields.autoMatch(addEmail.value); }
  });

  // Before the first render, so the Add form and every card's Edit panel are
  // built from the same list. An enumeration that fails is an empty list, not a
  // dead page: the typed field is still there.
  fetch("/api/browser-profiles", { headers: { "Accept": "application/json" } })
    .then(function (response) { return response.ok ? response.json() : []; })
    .catch(function () { return []; })
    .then(function (found) {
      profileCatalog = Array.isArray(found) ? found : [];
      addFields = accountFields(document.getElementById("add-fields"), null);
      refresh();
      setInterval(refresh, POLL_MS);
    });
})();
