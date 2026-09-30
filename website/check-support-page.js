#!/usr/bin/env node
/* Drives megapdf/support/index.html in a real DOM and checks that its triage behaves
 * the way the page tells the reader it does (#418). Run by hand, from the repository
 * root, after editing the page or its answers:
 *
 *     npm install jsdom          # once, anywhere; or `npm install -g jsdom`
 *     node website/check-support-page.js
 *
 * It is deliberately not in CI: the website has no build, and the whole site is four
 * static files. `website/deploy.py --dry-run --support --privacy` is the check that
 * always runs, and it covers the things a deploy must not get wrong (the privacy
 * disclosure, the answers' attributes, and that nothing on the page can send what
 * someone typed). This script covers the part only a browser can answer: whether the
 * matcher actually surfaces the right answer for a sentence a person would type.
 *
 * The promises being tested are the ones that matter if the matcher is WRONG:
 *   - a weak match shows nothing, rather than guessing;
 *   - a suggestion never blocks, hides or replaces the form below it;
 *   - marking one "that was it" does not close anything;
 *   - whatever was suggested, and what the reader said about it, lands in the message,
 *     because that is the only way we ever find out a rule is bad.
 */
"use strict";
const fs = require("fs");
const path = require("path");

let JSDOM;
try {
    ({ JSDOM } = require("jsdom"));
} catch (e) {
    console.error("This script needs jsdom:  npm install jsdom");
    process.exit(2);
}

const FILE = path.join(__dirname, "megapdf", "support", "index.html");
const HTML = fs.readFileSync(FILE, "utf8");
const EXPECTED_ANSWERS = (HTML.match(/<details class="answer"/g) || []).length;

let fails = 0;
function check(name, ok, extra) {
    if (ok) { console.log("ok    " + name); return; }
    fails++;
    console.log("FAIL  " + name + (extra ? "  — " + extra : ""));
}

function load() {
    return new JSDOM(HTML, {
        runScripts: "dangerously",
        url: "https://electricrv.ca/megapdf/support/",
        pretendToBeVisual: true,
    });
}

const settle = () => new Promise((r) => setTimeout(r, 400));   // the page debounces at 220ms

function type(doc, win, text, plat, kind) {
    const what = doc.getElementById("what");
    what.value = text;
    if (plat) { doc.querySelector(`input[name=plat][value=${plat}]`).checked = true; }
    if (kind) { doc.querySelector(`input[name=kind][value=${kind}]`).checked = true; }
    what.dispatchEvent(new win.Event("input", { bubbles: true }));
}

const titles = (doc) => [...doc.querySelectorAll("#hits .hit h4")].map((h) => h.textContent);

(async () => {
    const dom = load();
    const win = dom.window, doc = win.document;
    await settle();

    const report = doc.getElementById("report"), suggest = doc.getElementById("suggest");
    check("the answers list is there",
          doc.querySelectorAll("details.answer").length === EXPECTED_ANSWERS);
    check("a message is composed before anything is typed", /^Reference: MP-/.test(report.value),
          JSON.stringify(report.value.slice(0, 40)));
    check("the reference shown on the page is the one in the message",
          report.value.includes(doc.getElementById("refshow").textContent) &&
          /^MP-\d{6}-[A-Z0-9]{4}$/.test(doc.getElementById("refshow").textContent),
          doc.getElementById("refshow").textContent);
    check("nothing is suggested for an empty box", suggest.hidden);

    // A weak match must show nothing at all. "save" is in several answers' terms as an
    // ordinary word; one ordinary word may never surface an answer.
    type(doc, win, "save", "windows", "bug");
    await settle();
    check("one ordinary word surfaces nothing", suggest.hidden,
          doc.getElementById("hits").textContent.slice(0, 80));

    type(doc, win, "I ticked two boxes and pressed save but I cannot find the file anywhere",
         "windows", "bug");
    await settle();
    check("a sentence a person would type surfaces something", titles(doc).length > 0);
    check("and it is the right answer",
          titles(doc).some((t) => /Where your saved file went/.test(t)), titles(doc).join(" | "));
    check("never more than three at once", titles(doc).length <= 3, String(titles(doc).length));

    // The platform filter: crash recovery is a desktop answer and must not be offered
    // to someone on a phone, where there is no journal to recover from.
    type(doc, win, "megapdf crashed and I lost my changes, can I recover them", "ios", "bug");
    await settle();
    check("a desktop-only answer is withheld on iOS",
          !titles(doc).some((t) => /closed before I saved/.test(t)), titles(doc).join(" | "));
    type(doc, win, "megapdf crashed and I lost my changes, can I recover them", "linux", "bug");
    await settle();
    check("and offered on Linux", titles(doc).some((t) => /closed before I saved/.test(t)),
          titles(doc).join(" | "));

    check("the send button is a live mailto while suggestions are up",
          /^mailto:info@electricrv\.ca\?subject=/.test(doc.getElementById("mailbtn").href));
    check("the message says what the page suggested",
          /The support page suggested\n- /.test(report.value),
          JSON.stringify(report.value.slice(-300)));

    const before = doc.querySelectorAll("#hits .hit").length;
    const act = (label) => [...doc.querySelectorAll("#hits .hit .acts button")]
        .find((b) => b.textContent === label);
    act("Not it").click();
    check("Not it drops that suggestion",
          doc.querySelectorAll("#hits .hit").length === before - 1);
    check("Not it is recorded in the message, so a bad rule is visible to us",
          /\[reader: not it\]/.test(report.value), JSON.stringify(report.value.slice(-300)));
    check("the message is still sendable afterwards",
          report.value.length > 100 && doc.getElementById("mailbtn").href.includes("body="));

    if (act("That was it")) {
        act("That was it").click();
        check("That was it is recorded", /\[reader: that was it\]/.test(report.value));
        check("That was it closes nothing: the message and the form are intact",
              /What happened/.test(report.value) && report.value.length > 100 &&
              !doc.getElementById("report").closest("section").hidden);
    }

    const plat = (p) => {
        const r = doc.querySelector(`input[name=plat][value=${p}]`);
        r.checked = true;
        r.dispatchEvent(new win.Event("change", { bubbles: true }));
    };
    plat("mac");
    await settle();
    check("the 'where to find your version' line follows the platform",
          /MegaPDF menu/.test(doc.getElementById("verhelp").textContent),
          doc.getElementById("verhelp").textContent);

    doc.getElementById("ver").value = "2.1.1";
    doc.getElementById("ver").dispatchEvent(new win.Event("input", { bubbles: true }));
    await settle();
    const ticked = [...doc.querySelectorAll("#checklist li.ok")].map((li) => li.dataset.need);
    check("the 'what a report needs' list ticks what has been given",
          ticked.includes("ver") && ticked.includes("plat"), ticked.join(","));
    check("the version reaches the message", /App: MegaPDF 2\.1\.1/.test(report.value));

    // Every answer must be reachable by its own terms, or it is dead weight on the page.
    // A fresh page, because "Not it" above is remembered for the rest of that visit.
    const dom2 = load();
    const win2 = dom2.window, doc2 = win2.document;
    await settle();
    const unreachable = [];
    for (const el of doc2.querySelectorAll("details.answer")) {
        const terms = el.getAttribute("data-match").split("|")
            .concat((el.getAttribute("data-strong") || "").split("|").filter(Boolean));
        const typed = terms.find((t) => t.includes(" ")) || terms.slice(0, 2).join(" ");
        const plats = el.getAttribute("data-plat").split(/\s+/);
        type(doc2, win2, typed + " and it is a problem",
             plats[0] === "all" ? "windows" : plats[0], "bug");
        await settle();
        const got = [...doc2.querySelectorAll("#hits .hit h4")].map((h) => h.textContent);
        if (!got.includes(el.querySelector("summary").textContent.trim())) {
            unreachable.push(`${el.id} (typed: ${typed})`);
        }
    }
    check("every answer can be surfaced by its own terms", unreachable.length === 0,
          unreachable.join("; "));

    console.log(fails ? `\n${fails} failure(s)` : "\nall checks passed");
    process.exit(fails ? 1 : 0);
})();
