(function () {
  "use strict";

  var POLL_MS = 10000;
  var BROWSERS = ["", "chrome", "edge", "brave"];
  // The window the refresh pass renews a paused login in, so the page's "soon"
  // and the pass's "renew now" name one span.
  var LOGIN_WARN_SECONDS = 7 * 24 * 3600;
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
  var sidesLine = document.getElementById("sides");
  // The accounts the last dashboard named, which is what a side's switch control
  // offers: the ones parked in the store, so there is a pair to hand over.
  var lastAccounts = [];
  // The side rows on the page, by side name, kept across polls so an open
  // picker is never rebuilt under whoever is using it.
  var sideRows = {};
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

  // The alias, browser, and profile fields, built once and used both by the Add
  // form and by each card's Edit panel, so the two can never drift apart.
  function accountFields(container, entry) {
    var values = entry || {};
    var alias = element("input");
    alias.type = "text";
    alias.placeholder = "optional, e.g. weekly";
    alias.value = values.alias || "";
    labeled(container, "Alias", alias);

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
            if (retry.ok) { clearSidePicker(side); }
            showToast(retry.ok
              ? "The " + retry.body.side + " side holds nothing; a superseded family was quarantined at " + retry.body.quarantinedAt
              : refused(retry.body), retry.ok ? "warn" : "error");
          });
          return;
        }
        showToast(refused(result.body), "error");
        return;
      }
      // The account just handed back is now on offer. Leaving the picker on it,
      // or on whichever account sorts first, would hand a pair straight back out.
      clearSidePicker(side);
      showToast("The " + result.body.side + " side holds nothing; " + (result.body.parkedAs || holding) + " is parked here", "ok");
    });
  }

  // A release succeeded: the picker returns to "choose an account". Switch is
  // disabled here, in the same turn, because the request's re-enable runs before
  // the refresh and a failed refresh would otherwise leave it clickable.
  function clearSidePicker(side) {
    var row = sideRows[side];
    if (!row || !row.pick) { return; }
    row.pick.value = "";
    if (row.button) { row.button.disabled = true; }
  }

  // One line per configured side, and on it that side's switch control: which
  // parked account it should take. A selection survives the poll's redraw, and a
  // redraw is skipped while the picker is in use, so the ten-second poll never
  // changes the target under the operator.
  // Which accounts a side may be handed is the server's verdict, per card, and
  // the same one the card's own chip is drawn from.
  function offerable(side) {
    return lastAccounts.filter(function (account) {
      return (account.offeredTo || []).indexOf(side.side) !== -1;
    });
  }

  // Alias-first, the way a card's heading is. liveAccount is the address from
  // GET /api/sides; the alias is roster.alias on the dashboard account with that
  // email. An absent address is "holding nothing" only when the side answered
  // and said so. An offline read also leaves the address null, because the
  // dashboard could not be read, and that side may still hold an account.
  function heldAccount(email) {
    var account = lastAccounts.filter(function (candidate) { return candidate.email === email; })[0];
    var alias = account && account.roster && account.roster.alias;
    return alias ? "holding " + alias + " (" + email + ")" : "holding " + email;
  }

  function sideStateText(side) {
    var prefix = side.side + " side: " + side.detail;
    if (!side.liveAccount) {
      if (!side.online) { return prefix; }
      var nothing = "holding nothing";
      if (typeof side.detail === "string" && side.detail.slice(-nothing.length) === nothing) { return prefix; }
      return prefix + ", " + nothing;
    }
    return prefix + ", " + heldAccount(side.liveAccount);
  }

  // Updated in place rather than rebuilt, so nothing here is ever taken from
  // under the pointer: the picker keeps its selection, its focus and its open
  // menu across every poll, and no guard has to suppress a redraw to protect
  // it. Its options are replaced only when the accounts on offer change, and
  // the control is rebuilt only when the side goes on or offline.
  function renderSides(sides) {
    sidesLine.hidden = sides.length === 0;
    var present = {};
    sides.forEach(function (side) {
      present[side.side] = true;
      var row = sideRows[side.side];
      if (!row) {
        row = { node: element("div", "side"), state: element("span", "side-state"), mode: null };
        row.node.appendChild(row.state);
        sideRows[side.side] = row;
        sidesLine.appendChild(row.node);
      }

      row.state.textContent = sideStateText(side);
      var mode = side.online ? "switch" : (side.canStart ? "start" : "none");
      if (row.mode !== mode) {
        if (row.control) { row.node.removeChild(row.control); }
        row.mode = mode;
        row.control = element("span", "side-control");
        row.pick = null;
        row.release = null;
        // With the picker goes what it was holding: a side that went offline and
        // came back while the parked accounts were unchanged would otherwise
        // match the old signature, leave the new picker empty, and keep Switch
        // disabled until the account list happened to change.
        row.offers = null;
        if (mode === "switch") {
          row.pick = document.createElement("select");
          row.pick.name = side.side;
          row.control.appendChild(row.pick);
          row.button = actionButton("Switch " + side.side + " side", "switch", function () { switchSide(side.side, row.pick.value); });
          // The poll is what disables Switch on an empty value. A choice has to
          // enable it immediately, or the picker stays dead until the next one.
          row.pick.addEventListener("change", function () {
            row.button.disabled = busy || !row.pick.value;
          });
          // One control for the park-back, beside the switch and only on a side
          // that is answering: it reads what that side holds at click time and
          // the server re-reads it before anything moves.
          row.release = actionButton("Hand back", "secondary", function () { releaseSide(side.side, row.holding); });
          row.control.appendChild(row.release);
        } else if (mode === "start") {
          row.button = actionButton("Start " + side.side + " side", "secondary", function () { mutate(sidePath(side.side, "/start"), "POST", null, null); });
        } else {
          row.button = null;
        }

        if (row.button) { row.control.appendChild(row.button); }
        row.node.appendChild(row.control);
      }

      if (row.pick) {
        var offers = offerable(side);
        var arriving = offers.map(function (account) { return account.email; }).join("\u0000");
        if (row.offers !== arriving) {
          row.offers = arriving;
          var held = row.pick.value;
          row.pick.innerHTML = "";
          // The empty choice is first, and it is the selection whenever the
          // previous address is no longer on offer. Never the first real account.
          var placeholder = element("option", null, "choose an account");
          placeholder.value = "";
          row.pick.appendChild(placeholder);
          offers.forEach(function (account) {
            var option = element("option", null, (account.roster && account.roster.alias) || account.email);
            option.value = account.email;
            row.pick.appendChild(option);
          });
          if (held && arriving.split("\u0000").indexOf(held) !== -1) { row.pick.value = held; }
        }
      }

      if (row.button) { row.button.disabled = busy || (row.pick ? !row.pick.value : false); }
      if (row.release) {
        // A side holding nothing has nothing to hand back, which is the
        // NothingToRelease the server answers when the control is bypassed.
        row.holding = side.liveAccount;
        row.release.hidden = !side.liveAccount;
        row.release.disabled = busy;
      }
    });

    Object.keys(sideRows).forEach(function (name) {
      if (!present[name]) {
        sidesLine.removeChild(sideRows[name].node);
        delete sideRows[name];
      }
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

  // Where the card sits in the list, said in the one word its rows never carry.
  // A usable account's countdown is not repeated here: the seven-day row above
  // already reads "resets in 2 h 14 min", and the same phrase twice on one card
  // reads as two different facts.
  // The chip above states the credential facts, so this line is left off a
  // paused card except the live one, whose chip says "live" and would otherwise
  // leave "paused" unsaid; a parked card whose login has expired is spared a
  // "usable now" its Switch button refuses; the live account keeps its quota
  // standing, which is the operative fact whatever its login says.
  function nextReset(account, at) {
    if (loginExpired(account, at) && !account.isLive) { return null; }
    if (account.standing === "usable") { return "usable now"; }
    if (account.standing === "unread") { return "no usage read yet"; }
    if (account.standing === "paused") { return account.isLive ? "paused" : null; }
    // A spent account whose reset nothing can date has no wait to state, and the
    // rows above already say which figure is missing.
    if (account.standing === "exhausted" && account.nextResetAt) { return "usable " + relative(account.nextResetAt, at); }
    return null;
  }

  function usage(account, dashboard) {
    var section = element("div", "usage");
    var at = new Date(dashboard.capturedAt).getTime();
    account.usage.limits.forEach(function (limit) { section.appendChild(limitRow(limit, at)); });
    if (account.usage.source) {
      section.appendChild(element("p", "asof", asOf(account.usage.capturedAt, account.usage.source, at)));
    }
    var frees = nextReset(account, at);
    if (frees) { section.appendChild(element("p", "next-reset", frees)); }
    if (account.usage.credits) { section.appendChild(element("p", "asof", creditsLine(account.usage.credits))); }
    if (account.usageNote) { section.appendChild(element("p", "muted", account.usageNote)); }
    var state = refreshState(account, dashboard);
    if (state) { section.appendChild(element("p", "refresh-state", state)); }
    var login = loginLine(account, at);
    if (login) {
      var line = element("p", "login-expiry" + (loginSoon(account, at) ? " warn" : ""), login);
      // new Date(null) is the 1970 epoch, which would date every card without an
      // expiry to a lie, so the absolute instant is offered only when there is one.
      if (account.loginExpiresAt) { line.title = new Date(account.loginExpiresAt).toLocaleString(); }
      section.appendChild(line);
    }
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

  // Whether the arriving payload would move a card, which is the only thing the
  // guard below defers for.
  function reordered(arriving) {
    return arriving.length !== renderedOrder.length
      || arriving.some(function (email, index) { return email !== renderedOrder[index]; });
  }

  function render(dashboard, force) {
    // The header, the banner, and the warnings are not part of any card, so they
    // are written before the guards below and on every poll: an operator with an
    // Edit panel open would otherwise watch the page's own timestamp, the pass's
    // progress, the Refresh all button, and a banner that has since been cleared
    // freeze at whatever they said when the panel opened.
    lastAccounts = dashboard.accounts;
    captured.textContent = "as of " + new Date(dashboard.capturedAt).toLocaleTimeString();
    refreshStateLine.textContent = passState(dashboard);
    refreshAllButton.disabled = busy || dashboard.refresh.inProgress;
    banner.hidden = !dashboard.banner;
    banner.textContent = dashboard.banner || "";
    warnings.innerHTML = "";
    warnings.hidden = dashboard.warnings.length === 0;
    dashboard.warnings.forEach(function (warning) { warnings.appendChild(element("li", null, warning)); });
    // Rendering rebuilds every card, so the ten-second poll would otherwise wipe an
    // Edit panel, or a half-typed login code, out from under whoever is typing it.
    // A mutation's own render passes force, since that one has to show the result.
    if (!force && cards.querySelector("details.edit[open], details.login[open]")) { return; }
    // The hazard is a card moving, not a card being redrawn. Switch fires without a
    // confirm, so an order that changes while the pointer rests on the list, or
    // while a button inside it holds focus, sends the operator to whichever account
    // slid under the aim; the pointer resting there is the mouse's ordinary state,
    // so deferring on it alone would freeze the cards for as long as an operator
    // left the cursor on them. An order identical to the one on screen moves no
    // card, so it is rendered in place and a Refresh all pass keeps landing card by
    // card under the pointer.
    var arriving = dashboard.accounts.map(function (account) { return account.email; });
    if (!force && reordered(arriving) && (cards.matches(":hover") || cards.contains(document.activeElement))) { return; }

    cards.innerHTML = "";
    cardNodes = {};
    renderedOrder = arriving;
    dashboard.accounts.forEach(function (account) {
      var roster = account.roster;
      var paused = !!(roster && roster.paused);
      var card = element("section", "card" + (account.isLive ? " live" : "") + (paused ? " paused" : ""));
      var at = new Date(dashboard.capturedAt).getTime();
      var chip = stateChip(account, at);
      var alias = roster && roster.alias;
      card.appendChild(element("h2", null, alias || account.email));
      // The alias is what the operator calls this account and the address is what
      // the tool acts on; on its own line the address costs no heading room.
      if (alias) { card.appendChild(element("p", "address", account.email)); }

      var badges = element("div", "badges");
      // Two axes, and a card shows the second only when it adds something: the
      // store chip says where this account's one pair is, and the credential
      // chip says whether this side can switch to it. "live here" and "parked"
      // already carry "live" and "ready", so those two are not said twice. The
      // class is fixed rather than derived from the text, since a chip can
      // carry "(offline)".
      if (account.chip) { badges.appendChild(element("span", "badge store", account.chip)); }
      if (chip && (!account.chip || (chip !== "live" && chip !== "ready"))) {
        badges.appendChild(element("span", "badge " + chip.replace(/ /g, "-"), chip));
      }
      if (!roster) { badges.appendChild(element("span", "badge off-roster", "not on roster")); }
      card.appendChild(badges);

      card.appendChild(usage(account, dashboard));

      if (roster && roster.browser) {
        card.appendChild(element("p", "muted", roster.browser + (roster.browserProfileDirectory ? " / " + profileCardLabel(roster.browser, roster.browserProfileDirectory) : "")));
      }

      var actions = element("div", "actions");
      var switchButton = actionButton(account.isLive ? "Live now" : "Switch", "switch", function () { switchTo(account.email); });
      // Whether this account can come live on this side is the server's verdict,
      // which is where the slot, the strand and the expiry are all known: a
      // paused account can still be switched to by hand, a stranded or expired
      // one cannot, and neither can one whose pair the other side is holding.
      // What is added here is only what the browser knows: a request in flight,
      // a banner, and a refresh pass.
      switchButton.disabled = !account.canSwitchHere || busy || !!dashboard.banner || dashboard.refresh.inProgress;
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
      // would leave one account holding two logins. A card with a login good for
      // longer than the warning window does not need the button on its face; an
      // expired login is inside that window, so the one test covers both.
      // A slot the other side holds is empty for a reason, and logging into it
      // would put a second token family on the machine. The route refuses it
      // anyway; the button goes so the operator is not sent at a 409.
      var needsLogin = !account.heldAway && (!account.hasCredentials || loginSoon(account, at));
      if (roster && !account.isLive && needsLogin) {
        actions.appendChild(actionButton(
          account.hasCredentials ? "Log in again" : "Login",
          "secondary",
          function () { startLogin(account.email); }));
      }

      card.appendChild(actions);

      // Remove revokes the login and deletes the folder, so it stands in its own
      // group rather than a pointer's width from Switch.
      if (!account.isLive) {
        var danger = element("div", "actions danger-zone");
        var removeButton = actionButton("Remove", "danger", function () { remove(account.email); });
        // Removing a slot the other side holds would skip the logout it cannot
        // reach and delete the record that says the pair exists at all. The
        // route refuses it; the button goes with it.
        removeButton.disabled = account.heldAway;
        danger.appendChild(removeButton);
        card.appendChild(danger);
      }

      if (roster) { card.appendChild(editPanel(account, !account.isLive && !needsLogin)); }
      cardNodes[account.email] = card;
      cards.appendChild(card);
    });
  }

  function refresh(force) {
    return fetch("/api/dashboard", { headers: { "Accept": "application/json" } })
      .then(function (response) { return response.json(); })
      .then(function (dashboard) { render(dashboard, force); })
      .then(function () { return fetch("/api/sides", { headers: { "Accept": "application/json" } }); })
      .then(function (response) { return response.ok ? response.json() : []; })
      .then(function (sides) { renderSides(Array.isArray(sides) ? sides : []); })
      .catch(function (error) { showToast("Dashboard unavailable: " + error, "error"); });
  }

  function setButtonsDisabled(disabled) {
    // Synchronously, at click time: a second click during the lock wait would
    // otherwise send a second request whose refusal toast overwrote the outcome of
    // the first. The re-enable is unconditional and the following render applies
    // the per-account state, so a failed request can never leave a button dead.
    // A side Switch whose picker is the empty "choose an account" is the exception:
    // the re-enable runs before the refresh, and a failed refresh would leave that
    // Switch clickable with nothing selected.
    Array.prototype.forEach.call(document.querySelectorAll("button"), function (button) { button.disabled = disabled; });
    if (!disabled) {
      Object.keys(sideRows).forEach(function (name) {
        var row = sideRows[name];
        if (row.button && row.pick && !row.pick.value) { row.button.disabled = true; }
      });
    }
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
