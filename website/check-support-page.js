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

// #635: the ids are part of the support-mail interface (the message names them on its
// "Answer ids:" line and the support system links to them). They may be ADDED to; none of
// these may be renamed or removed without telling the support system first.
const REQUIRED_IDS = [
    "a-adobe-reader",
    "a-cant-edit-text",
    "a-checkbox",
    "a-cover-vs-redact",
    "a-crash",
    "a-language",
    "a-large",
    "a-markdown",
    "a-minimum",
    "a-pages",
    "a-password",
    "a-print",
    "a-privacy",
    "a-saved-where",
    "a-signature",
    "a-signed",
    "a-update",
    "a-version",
];

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
    {
        // #635: the same answers by id, in the same order, on one line after the titles.
        const m = /\nAnswer ids: ([a-z0-9-]+(?:, [a-z0-9-]+)*)\n/.exec(report.value);
        const ids = m ? m[1].split(", ") : [];
        const nTitles = (report.value.match(/\n- /g) || []).length;
        check("the message carries the suggested answers' ids on one line",
              !!m && ids.length === nTitles && ids.every((i) => doc.getElementById(i)
                  && doc.getElementById(i).classList.contains("answer")),
              JSON.stringify(report.value.slice(-400)));
    }

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

    // #635: the mailbox is read by an Outlook rule and by an in-house support system, both
    // keyed on the subject. The subject's shape is an interface (website/README.md, 'The
    // support mailbox'), so every platform and kind the page offers is pinned here, exactly.
    {
        const PLAT = { windows: "Windows", mac: "Mac", ios: "iPhone / iPad",
                       android: "Android", linux: "Linux" };
        const KIND = { bug: "Something is broken", question: "How do I\u2026?",
                       idea: "An idea or a request" };
        const subjectOf = () => {
            const m = /[?&]subject=([^&]*)/.exec(doc.getElementById("mailbtn").href);
            return m ? decodeURIComponent(m[1]) : null;
        };
        const refNow = doc.getElementById("refshow").textContent;
        const bad = [];
        for (const [pv, pn] of Object.entries(PLAT)) {
            for (const [kv, kn] of Object.entries(KIND)) {
                type(doc, win, "x", pv, kv);
                plat(pv);
                await settle();
                const want = `MegaPDF support ${refNow} [${pn}] [${kn}]`;
                if (subjectOf() !== want) { bad.push(`${pv}/${kv}: ${subjectOf()}`); }
            }
        }
        check("the subject is 'MegaPDF support <ref> [platform] [kind]' for every choice",
              bad.length === 0, bad.join("; "));
        check("every platform and kind the page offers is one this test knows",
              [...doc.querySelectorAll("input[name=plat]")].every((r) => r.value in PLAT) &&
              [...doc.querySelectorAll("input[name=kind]")].every((r) => r.value in KIND));
        check("the reference in the subject is the one in the body's first line",
              report.value.split("\n")[0] === "Reference: " + refNow &&
              /^MP-\d{6}-[ACDEFGHJKLMNPQRTUVWXY34679]{4}$/.test(refNow), refNow);

        const dom3 = load();
        await settle();
        const doc3 = dom3.window.document;
        const m3 = /[?&]subject=([^&]*)/.exec(doc3.getElementById("mailbtn").href);
        check("with nothing chosen the subject is the bare prefix and reference, no empty tags",
              !!m3 && decodeURIComponent(m3[1]) ===
              "MegaPDF support " + doc3.getElementById("refshow").textContent);
    }

    // #635: the page used to promise "one person" and "no ticket robot". The mailbox is now
    // sorted by software and decided by a person (privacy policy section 4, support-triage), so
    // those two phrases would contradict the policy the page is deployed with.
    check("the page no longer promises 'ticket robot' or 'to one person'",
          !/ticket robot/i.test(HTML) && !/to one person/i.test(HTML),
          [/ticket robot/i.test(HTML) && "ticket robot", /to one person/i.test(HTML) && "to one person"]
              .filter(Boolean).join(", "));

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

    // The "Common questions" links in front of the form (#635 follow-up): every one must
    // land on an answer that exists, clicking one opens it, and an answer that does not apply
    // to the chosen platform is left off the list by the same data-plat rule the matcher uses.
    {
        const dom4 = load();
        const doc4 = dom4.window.document;
        const win4 = dom4.window;
        await settle();
        const links = [...doc4.querySelectorAll(".common a[href^='#a-']")];
        const bad = links.filter((a) => {
            const t = doc4.getElementById(a.getAttribute("href").slice(1));
            return !(t && t.classList.contains("answer"));
        }).map((a) => a.getAttribute("href"));
        check("every Common questions link points at a real answer",
              links.length >= 8 && bad.length === 0, bad.join(", "));
        const target = doc4.getElementById("a-password");
        check("an answer starts closed", !target.open);
        links.find((a) => a.getAttribute("href") === "#a-password")
            .dispatchEvent(new win4.MouseEvent("click", { bubbles: true, cancelable: true }));
        check("clicking a Common questions link opens that answer", target.open);
        const crash = () => links.find((a) => a.getAttribute("href") === "#a-crash").parentElement;
        const r = (v) => {
            const el = doc4.querySelector(`input[name=plat][value=${v}]`);
            el.checked = true;
            el.dispatchEvent(new win4.Event("change", { bubbles: true }));
        };
        r("ios");
        check("a desktop-only answer is left off the Common questions list on iOS", crash().hidden);
        r("linux");
        check("and listed on Linux", !crash().hidden);
        const priv = doc4.querySelector("details.privacy");
        check("the privacy explanation is on the page, collapsed, and says nothing leaves the browser",
              !!priv && !priv.open && /Nothing leaves your browser/.test(priv.textContent) &&
              /A person decides what happens to your message/.test(priv.textContent));
    }

    {
        const missing = REQUIRED_IDS.filter((i) => !new RegExp('<details class="answer" id="' + i + '"').test(HTML));
        check("every answer id the support system links to still exists", missing.length === 0,
              "missing: " + missing.join(", "));
    }
    console.log(fails ? `\n${fails} failure(s)` : "\nall checks passed");
    process.exit(fails ? 1 : 0);
})();
