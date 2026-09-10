# Localisation — how the strings work on each platform

MegaPDF ships in English, French (Canada) and French (France) since issue #91.
Canadian French is the translated one; France French is derived from it by
rule (see the glossary's last section), so there is one French to maintain. This is the
reference for where the strings live, how a platform picks a language, and what
to do when you add a string or a language. The vocabulary itself is in
[localisation-glossary.md](localisation-glossary.md): same English concept, same
French word on every platform.

## The shape, in one paragraph

Each platform keeps its own catalogue in its own idiom — the apps have different
UX and share no UI code — and a static test in `MegaPDF.Core.Tests`
(`StringCatalogueTests`) reads all four off disk and fails CI when a key exists
in one language and not the other, when a French value is byte-identical to the
English outside a documented allowlist, or when the placeholders differ. The
engine, `MegaPDF.Core`, has no resource lookup at all: its exception messages
are technical English for a developer, and each app turns a typed reason into a
sentence in the user's language before showing anything.

| Platform | Catalogue | Locale folder | Code access | Language comes from |
|---|---|---|---|---|
| Windows (WinUI 3) | `src/MegaPDF.App/Strings/en-US/Resources.resw` | `Strings/fr-CA/`, `Strings/fr-FR/` (derived) | generated `Strings.g.cs` over MRT | Windows display language; `--language`; the Language setting |
| macOS (Avalonia) | `src/MegaPDF.Avalonia/Strings/Strings.resx` | `Strings.fr-CA.resx`, `Strings.fr.resx` (derived, neutral) | generated `Strings.g.cs` over `ResourceManager` | macOS preferred language (CoreFoundation); `--language` |
| Android (Compose) | `android/app/src/main/res/values/strings.xml` | `values-fr-rCA/`, `values-fr/` (derived, neutral) | `stringResource`, `getString` | system / per-app language (Android 13+) |
| iOS (SwiftUI) | `ios/MegaPDF/Localizable.xcstrings` | `fr-CA` and `fr` (derived) inside the catalog | implicit `LocalizedStringKey`, `String(localized:)` | system / per-app language |

The neutral French (`fr`, `values-fr`) is the France variant, because that is
what every other French locale (Belgium, Switzerland, Africa) falls back to on
Android and .NET; Canada is the exact-match region on all four platforms.

## Windows

- **XAML** uses `x:Uid`. A key is `Uid.Property`; attached properties use the
  `[using:Namespace]Class.Property` form, e.g.
  `OpenButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`. Items
  that cannot carry an `x:Uid` (`<x:String>` in a `RadioButtons`) became
  `TextBlock`s.
- **Code** calls the generated `Strings` class: `Strings.Open`, or
  `Strings.PageOf(current, total)` for keys with `{0}` placeholders. A key with
  a dot gets no accessor. Regenerate with `python3 tools/gen_strings.py` after
  editing the catalogue; the test fails if the class is stale.
- **Error dialogs** go through `UserFacing.Describe(ex)`: a localised lead
  sentence chosen from `PdfLoadException.ErrorCode`, `TextEditException.Reason`
  or the exception type, with the engine's message underneath only when nothing
  better is known. Windows' own IO messages are already in the user's language
  and are shown as they are.
- **The Store package** declares `en-us`, `en-ca`, `fr-ca` and `fr-fr` in
  `Package.appxmanifest`; the app description and the `.pdf` file-type name are
  `ms-resource:` references so Explorer localises them too.
- **Language override.** `--language fr-CA` on the command line, or the
  Language setting in the Settings flyout (stored in `settings.json`, applied at
  the next start), sets `ApplicationLanguages.PrimaryLanguageOverride`. That is
  the packaged build. The unpackaged dev build has no package identity, so the
  override reaches code strings only there — XAML labels follow Windows'
  display language. To see the whole window in French, install the package and
  set the Language setting (the harness README covers this).
- **The toolbar breakpoint is measured**, not a constant: `ApplyToolbarLayout`
  asks the bar for its desired width with every label showing and Save wearing
  its dot, and sheds labels below that. `--screenshot` prints the resolved
  labels and the measured breakpoint to stderr (`toolbar: …`) so a language's
  number can be read off a run. English measures about 1430 effective px,
  French about 1610 (measured on the installed package, where every label is
  French; the dev build's figure is lower because its x:Uid labels stay
  English). The Store frame, 2500 px at 150 % = 1667 effective, clears it.
- **Automation.** Every toolbar and flyout control the screenshot harness
  touches has an `AutomationProperties.AutomationId` (`OpenButton`,
  `ShrinkButton`, `SignaturesButton`, …). The UIA `Name` is what Narrator
  reads and is localised; the id never is.

## macOS

- **AXAML** uses `{x:Static loc:Strings.Key}` with `xmlns:loc="using:MegaPDF.Avalonia"`.
- **Code** calls the same generated `Strings` class; plurals use
  `Strings.Plural(n, one, other)` (French treats 0 and 1 as singular).
- The `fr` and `fr-CA` satellite assemblies are embedded in the single-file publish, so the
  bundle needs no `fr/` folder; `tools/build-macos-app.sh` writes
  `CFBundleDevelopmentRegion`, `CFBundleLocalizations` and the three
  `.lproj/InfoPlist.strings` (Finder's "Document PDF") before signing, and
  `macos-app.yml` checks they are there.
- At startup `Program.ApplyLanguage` honours `--language <tag>` and otherwise
  reads the first entry of `CFLocaleCopyPreferredLanguages`, because a
  Finder-launched process does not inherit `LANG`. `--self-test` pins en-US so
  its assertions stay stable on a French machine.

## Android

- `strings.xml` keys are snake_case with positional `%1$s` / `%1$d` arguments;
  `<plurals>` where a count changes the wording. `translatable="false"` marks
  the brand strings the parity test ignores.
- `androidResources { generateLocaleConfig = true }` plus
  `res/resources.properties` (`unqualifiedResLocale=en-US`) generate the locale
  config Android 13+ uses for its per-app language picker.
- `ViewerViewModel` is an `AndroidViewModel`, so its toasts use
  `getApplication<Application>().getString(...)`. The four sites that used to
  append `${e.message}` now show a plain sentence.

## iOS

- The String Catalog uses the English text as the key (SwiftUI convention).
  Literal strings passed to `Text`, `Button`, `.alert`, `.navigationTitle`
  and friends are localised without code changes; `String` values shown
  through `Text(someString)` go through `String(localized:)`.
- `PdfError` conforms to `LocalizedError`; `ViewerModel` no longer interpolates
  a raw error into a status message.
- `project.yml` sets `developmentLanguage: en` and `SWIFT_EMIT_LOC_STRINGS`.
  `ios-ci.yml` runs the test suite a second time with `-testLanguage fr
  -testRegion CA`.

## Adding a string

1. Add it to the English catalogue of the platform, then to the Canadian
   French one with the same key. Follow the glossary; if the term is new, add
   a row, and a France row if the word differs there.
2. Run `python3 tools/gen_strings.py fr-fr`: it regenerates the desktop
   accessors and derives the France catalogues. Use `Strings.Key` in code.
3. Run the Core tests (`dotnet test MegaPDF.sln`) — `StringCatalogueTests`
   tells you exactly which key is missing or untranslated on which platform.

## Adding a language

1. Windows: `Strings/<tag>/Resources.resw` and `<Resource Language="<tag>" />`
   in `Package.appxmanifest`; a Store listing in that language (Partner Center
   asks for one per declared language — today en-US, en-CA, fr-CA, fr-FR);
   rebuild x64 and ARM64.
2. macOS: `Strings/Strings.<lang>.resx`; add the tag to `CFBundleLocalizations`
   and an `InfoPlist.strings` in `tools/build-macos-app.sh`.
3. Android: `values-<lang>/strings.xml`; a Play listing in the Console.
4. iOS: a new localization in `Localizable.xcstrings`; an App Store
   localisation in App Store Connect.
5. Extend `StringCatalogueTests` to read the new language, and the `Choices`
   list in `AppLanguage.cs` if the Windows Settings flyout should offer it.

## Checking layouts before a translation exists

`python3 tools/gen_strings.py pseudo` writes a `qps-ploc` pseudo-locale for the
two desktop apps — every letter accented, every string a third longer, wrapped
in brackets so a hard-coded string stands out as the only plain one. It is
gitignored and picked up by the next build; run the Windows app with
`--language qps-ploc` or the Mac app with the same flag.

## Store listings

French copy for the three stores is in `docs/microsoft-store-listing.md`,
`docs/app-store-listing.md` and `android/RELEASING.md`, each under a
"Français (Canada)" heading. It was translated by the build assistant; have a
francophone read it before pasting it into a store. Screenshots exist per language on every
platform: Windows sets live in `artifacts/store/screenshots/{fr-CA,fr-FR}/`
(harness README § French screenshots); the Android and iOS capture workflows run
a language matrix and open a French demo agreement (`demo-fr.pdf`, from
`tools/gen_test_fixtures.py`) whose names and search term come from the string
catalogues (`screenshot_*` keys on Android, the demo keys in the String Catalog
on iOS).
