#!/usr/bin/env node
/* Drives the support pages in a real DOM and checks that their triage behaves the way
 * each page tells the reader it does (#418). There are three pages, one per language
 * (#859): megapdf/support/ (English), megapdf/fr/support/ (fr-CA) and
 * megapdf/es/support/ (es-419). Every check runs on every page, and each page has its
 * own sentences, written in its own language, that must surface the right answer.
 * Run by hand, from the repository root, after editing a page or its answers:
 *
 *     npm install jsdom          # once, anywhere; or `npm install -g jsdom`
 *     node website/check-support-page.js
 *
 * It is deliberately not in CI: the website has no build, and the whole site is static
 * files. `website/deploy.py --dry-run --support --privacy` is the check that always
 * runs, and it covers the things a deploy must not get wrong (the privacy disclosure,
 * the answers' attributes, the same ids in every language, and that nothing on a page
 * can send what someone typed). This script covers the part only a browser can answer:
 * whether the matcher actually surfaces the right answer for a sentence a person would
 * type.
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

// #635: the ids are part of the support-mail interface (the message names them on its
// "Answer ids:" line and the support system links to them). They may be ADDED to; none of
// these may be renamed or removed without telling the support system first. #859: the
// same ids on every language's page, since the system links support/#<id> in any of them.
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

// What each page says in its own words. `cases` are sentences a person would type, in
// that language, with the answers they must (`want`) and must not (`not`) surface. Several
// French and Spanish ones are typed without accents on purpose: people do, and the matcher
// folds accents away on both sides.
const PAGES = [
    {
        lang: "en",
        file: path.join(__dirname, "megapdf", "support", "index.html"),
        url: "https://megapdf.ca/support/",
        htmlLang: "en",
        written: "Written on megapdf.ca/support/",
        yes: "That was it", no: "Not it",
        oneWord: "save",
        filler: " and it is a problem",
        whatHeading: "What happened",
        macVerhelp: /MegaPDF menu/,
        privacy: [/Nothing leaves your browser/, /A person decides what happens to your message/],
        crash: "megapdf crashed and I lost my changes, can I recover them",
        cases: [
            { text: "I ticked two boxes and pressed save but I cannot find the file anywhere",
              want: ["a-saved-where"] },
            { text: "the form says it needs Adobe Reader", want: ["a-adobe-reader"] },
            { text: "the document has a digital signature and it will be invalid",
              want: ["a-signed"], not: ["a-signature"] },
            { text: "the app is in English, how do I get it in Spanish", want: ["a-language"] },
        ],
    },
    {
        lang: "fr",
        file: path.join(__dirname, "megapdf", "fr", "support", "index.html"),
        url: "https://megapdf.ca/fr/support/",
        htmlLang: "fr-CA",
        written: "Rédigé sur megapdf.ca/fr/support/",
        yes: "C'était ça", no: "Ce n'est pas ça",
        oneWord: "enregistrer",
        filler: " et c'est un problème",
        whatHeading: "Ce qui s'est passé",
        macVerhelp: /menu MegaPDF/,
        privacy: [/Rien ne quitte votre navigateur/, /Une personne décide de la suite donnée à votre message/],
        crash: "MegaPDF a planté et j'ai perdu mes modifications. Est-ce que je peux les récupérer?",
        cases: [
            { text: "J'ai coché deux cases et j'ai enregistré, mais je ne trouve pas le fichier nulle part",
              want: ["a-saved-where"] },
            { text: "ou est passe mon fichier apres l enregistrement", want: ["a-saved-where"] },
            { text: "Le PDF est protege par un mot de passe et je ne peux pas l'ouvrir",
              want: ["a-password"] },
            { text: "J'ai mis du correcteur sur mon numéro, mais on peut encore le copier",
              want: ["a-cover-vs-redact"] },
            { text: "Le formulaire du gouvernement dit qu'il faut Adobe Reader",
              want: ["a-adobe-reader"] },
            { text: "Comment ajouter ma signature à partir d'une photo?", want: ["a-signature"] },
            { text: "Le document a une signature numérique et MegaPDF dit qu'elle sera invalide",
              want: ["a-signed"], not: ["a-signature"] },
            { text: "Mon imprimante n'apparaît pas quand je veux imprimer", want: ["a-print"] },
            { text: "L'application est en anglais, comment la mettre en français?",
              want: ["a-language"] },
            { text: "Comment faire pivoter une page qui est à l'envers", want: ["a-pages"] },
            { text: "Je veux exporter en Markdown mais les images manquent", want: ["a-markdown"] },
        ],
    },
    {
        lang: "es",
        file: path.join(__dirname, "megapdf", "es", "support", "index.html"),
        url: "https://megapdf.ca/es/support/",
        htmlLang: "es-419",
        written: "Escrito en megapdf.ca/es/support/",
        yes: "Era eso", no: "No es eso",
        oneWord: "guardar",
        filler: " y es un problema",
        whatHeading: "Qué pasó",
        macVerhelp: /menú MegaPDF/,
        privacy: [/Nada sale de tu navegador/, /Una persona decide qué se hace con tu mensaje/],
        crash: "MegaPDF se cerró de repente y perdí mis cambios, ¿puedo recuperarlos?",
        cases: [
            { text: "Marqué dos casillas y guardé, pero no encuentro el archivo por ningún lado",
              want: ["a-saved-where"] },
            { text: "donde quedo el archivo despues de guardar", want: ["a-saved-where"] },
            { text: "El PDF tiene contrasena y no puedo abrirlo", want: ["a-password"] },
            { text: "Puse corrector sobre mi número de cuenta pero todavía se puede copiar",
              want: ["a-cover-vs-redact"] },
            { text: "El formulario del gobierno dice que necesita Adobe Reader",
              want: ["a-adobe-reader"] },
            { text: "¿Cómo pongo mi firma desde una foto?", want: ["a-signature"] },
            { text: "El documento tiene una firma digital y MegaPDF dice que va a quedar inválida",
              want: ["a-signed"], not: ["a-signature"] },
            { text: "Mi impresora no aparece cuando quiero imprimir", want: ["a-print"] },
            // Fable's #859 review: "se tilda" (crash, Argentina) sits inside "no se tilda"
            // (checkbox); the checkbox answer must win and the crash answer stay out.
            { text: "La casilla no se tilda cuando la toco", want: ["a-checkbox"], not: ["a-crash"] },
            // "clave" is the everyday Latin American word for a PDF password.
            { text: "El pdf me pide una clave para abrirlo", want: ["a-password"] },
            { text: "MegaPDF se cierra solo cuando abro un archivo grande", want: ["a-crash"] },
            { text: "La aplicación está en inglés, ¿cómo la pongo en español?",
              want: ["a-language"] },
            { text: "¿Cómo giro una página que quedó de cabeza?", want: ["a-pages"] },
            { text: "el archivo es muy pesado y tarda mucho en abrir", want: ["a-large"] },
        ],
    },
];

let fails = 0;
let prefix = "";
function check(name, ok, extra) {
    if (ok) { console.log("ok    " + prefix + name); return; }
    fails++;
    console.log("FAIL  " + prefix + name + (extra ? "  — " + extra : ""));
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
// The ids behind the suggestions shown, by matching each title to its answer's summary.
const hitIds = (doc) => titles(doc).map((t) => {
    const a = [...doc.querySelectorAll("details.answer")]
        .find((el) => el.querySelector("summary").textContent.trim() === t);
    return a ? a.id : "?" + t;
});
const answerAttrs = (doc) => [...doc.querySelectorAll("details.answer")]
    .map((el) => el.id + "=" + el.getAttribute("data-plat"));

async function runPage(cfg, english) {
    prefix = `[${cfg.lang}] `;
    const HTML = fs.readFileSync(cfg.file, "utf8");
    const EXPECTED_ANSWERS = (HTML.match(/<details class="answer"/g) || []).length;
    const load = () => new JSDOM(HTML, { runScripts: "dangerously", url: cfg.url,
                                         pretendToBeVisual: true });

    const dom = load();
    const win = dom.window, doc = win.document;
    await settle();

    const report = doc.getElementById("report"), suggest = doc.getElementById("suggest");
    check("the page says which language it is in",
          doc.documentElement.getAttribute("lang") === cfg.htmlLang,
          doc.documentElement.getAttribute("lang"));
    check("the answers list is there",
          doc.querySelectorAll("details.answer").length === EXPECTED_ANSWERS);
    if (english) {
        check("the same answer ids, in the same order and for the same platforms, as the English page",
              JSON.stringify(answerAttrs(doc)) === JSON.stringify(english),
              answerAttrs(doc).filter((x) => !english.includes(x)).join(", "));
    }
    {
        // The switcher and the hreflang alternates name all three pages.
        const langs = [...doc.querySelectorAll("nav.langs a")].map((a) => a.getAttribute("hreflang"));
        const alts = [...doc.querySelectorAll('link[rel=alternate]')].map((l) => l.hreflang + " " + l.href);
        const current = doc.querySelector("nav.langs a[aria-current=page]");
        check("the language switcher links all three languages and marks this one",
              ["en", "fr", "es"].every((l) => langs.includes(l)) && !!current &&
              current.getAttribute("hreflang") === cfg.lang, langs.join(","));
        check("hreflang alternates name the three support pages on megapdf.ca",
              alts.includes("en https://megapdf.ca/support/") &&
              alts.includes("fr https://megapdf.ca/fr/support/") &&
              alts.includes("es https://megapdf.ca/es/support/") &&
              doc.querySelector("link[rel=canonical]").href === cfg.url, alts.join(" | "));
    }
    check("a message is composed before anything is typed", /^Reference: MP-/.test(report.value),
          JSON.stringify(report.value.slice(0, 40)));
    check("the reference shown on the page is the one in the message",
          report.value.includes(doc.getElementById("refshow").textContent) &&
          /^MP-\d{6}-[A-Z0-9]{4}$/.test(doc.getElementById("refshow").textContent),
          doc.getElementById("refshow").textContent);
    check("the message says which page wrote it", report.value.includes(cfg.written),
          JSON.stringify(report.value.slice(-120)));
    check("nothing is suggested for an empty box", suggest.hidden);

    // A weak match must show nothing at all. One ordinary word may never surface an answer.
    type(doc, win, cfg.oneWord, "windows", "bug");
    await settle();
    check("one ordinary word surfaces nothing", suggest.hidden,
          doc.getElementById("hits").textContent.slice(0, 80));

    for (const c of cfg.cases) {
        type(doc, win, c.text, c.plat || "windows", c.kind || "bug");
        await settle();
        const got = hitIds(doc);
        check(`"${c.text.slice(0, 60)}" surfaces ${c.want.join(", ")}` +
              (c.not ? ` and not ${c.not.join(", ")}` : ""),
              c.want.every((i) => got.includes(i)) && !(c.not || []).some((i) => got.includes(i)) &&
              got.length <= 3, got.join(" | ") || "(nothing)");
    }

    // The platform filter: crash recovery is a desktop answer and must not be offered
    // to someone on a phone, where there is no journal to recover from.
    type(doc, win, cfg.crash, "ios", "bug");
    await settle();
    check("a desktop-only answer is withheld on iOS", !hitIds(doc).includes("a-crash"),
          hitIds(doc).join(" | "));
    type(doc, win, cfg.crash, "linux", "bug");
    await settle();
    check("and offered on Linux", hitIds(doc).includes("a-crash"), hitIds(doc).join(" | "));

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
    check("each suggestion offers its two buttons in this page's language",
          !!act(cfg.no) && !!act(cfg.yes));
    if (act(cfg.no)) { act(cfg.no).click(); }
    check("Not it drops that suggestion",
          doc.querySelectorAll("#hits .hit").length === before - 1);
    check("Not it is recorded in the message, so a bad rule is visible to us",
          /\[reader: not it\]/.test(report.value), JSON.stringify(report.value.slice(-300)));
    check("the message is still sendable afterwards",
          report.value.length > 100 && doc.getElementById("mailbtn").href.includes("body="));

    if (act(cfg.yes)) {
        act(cfg.yes).click();
        check("That was it is recorded", /\[reader: that was it\]/.test(report.value));
        check("That was it closes nothing: the message and the form are intact",
              report.value.includes(cfg.whatHeading) && report.value.length > 100 &&
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
          cfg.macVerhelp.test(doc.getElementById("verhelp").textContent),
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
    // support mailbox'), so every platform and kind the page offers is pinned here, exactly,
    // and it is the same English subject whatever language the page is in (#859).
    {
        const PLAT = { windows: "Windows", mac: "Mac", ios: "iPhone / iPad",
                       android: "Android", linux: "Linux" };
        const KIND = { bug: "Something is broken", question: "How do I…?",
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
                const head = report.value.split("\n").slice(0, 4).join("\n");
                if (head !== `Reference: ${refNow}\nApp: MegaPDF 2.1.1\nPlatform: ${pn}\nKind: ${kn}`) {
                    bad.push(`${pv}/${kv} body: ${JSON.stringify(head)}`);
                }
            }
        }
        check("the subject is 'MegaPDF support <ref> [platform] [kind]' and the first four lines " +
              "are the English interface, for every choice", bad.length === 0, bad.join("; "));
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
        check("with nothing suggested the message says so in the interface's words",
              /\nThe support page suggested nothing\.\n/.test(doc3.getElementById("report").value));
    }

    if (cfg.lang === "en") {
        // #635: the page used to promise "one person" and "no ticket robot". The mailbox is
        // now sorted by software and decided by a person (privacy policy section 4,
        // support-triage), so those two phrases would contradict the policy.
        check("the page no longer promises 'ticket robot' or 'to one person'",
              !/ticket robot/i.test(HTML) && !/to one person/i.test(HTML),
              [/ticket robot/i.test(HTML) && "ticket robot", /to one person/i.test(HTML) && "to one person"]
                  .filter(Boolean).join(", "));
    }

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
        type(doc2, win2, typed + cfg.filler, plats[0] === "all" ? "windows" : plats[0], "bug");
        await settle();
        if (!hitIds(doc2).includes(el.id)) { unreachable.push(`${el.id} (typed: ${typed})`); }
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
              !!priv && !priv.open && cfg.privacy.every((re) => re.test(priv.textContent)));
    }

    {
        const missing = REQUIRED_IDS.filter((i) => !new RegExp('<details class="answer" id="' + i + '"').test(HTML));
        check("every answer id the support system links to still exists", missing.length === 0,
              "missing: " + missing.join(", "));
    }
    return answerAttrs(doc);
}

(async () => {
    let english = null;
    for (const cfg of PAGES) {
        const attrs = await runPage(cfg, english);
        if (!english) { english = attrs; }
    }
    console.log(fails ? `\n${fails} failure(s)` : "\nall checks passed");
    process.exit(fails ? 1 : 0);
})();
