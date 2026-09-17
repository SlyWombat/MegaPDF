#!/bin/bash
# Unattended finish of #151 (PDFium patch 0025) on a Linux server, inside the
# megapdf-toolbox container started by tools/remote/run-151.sh.
#
# Stages, each gated on the one before and recorded as $STATE/markers/<stage>.done so
# a restarted container resumes where it stopped:
#   1 clone        clone the repository, check out wip/151-pdfium-0025
#   2 wait-147     poll until #147 (patch 0024 and its release) is on main, up to 12 h
#   3 rebase       rebase this branch's own commits onto main and push
#   4 release      dispatch the PDFium release for series 1-25 and wait for it
#   5 install      tools/pdfium/install-release.sh, commit, push
#   6 core-tests   build the core and run its tests on Linux against the release
#   7 ci           the five CI workflows green on the pushed full SHA
#   8 battery      edit battery, p24 then p25 on this machine, compared
#   9 merge        fast-forward main, close #151, write DONE
#
# A failure writes $STATE/FAILED and comments on #151 with how to resume. With DONE or
# FAILED present the container idles. To resume: fix the cause, delete FAILED (and any
# marker of a stage to redo), then `docker restart megapdf-151`.
#
# Auth: gh reads a read-only hosts.yml (GH_CONFIG_DIR) and git uses gh as its credential
# helper. No token is ever put on a command line, in the environment or in a log.
# The corpus holds personal documents: battery output stays under $STATE, and only
# counts reach comments.
set -uo pipefail

STATE=${STATE:-/state}
CORPUS=${CORPUS:-/corpus}
REPO=$STATE/repo
MARKS=$STATE/markers
VARS=$STATE/vars
BAT=$STATE/battery
GH_REPO=SlyWombat/MegaPDF
ISSUE=151
BRANCH=wip/151-pdfium-0025
# The tip of wip/147-large-files this branch was built on; only commits after it are ours.
BASE_147=7aeae7e94e51da5747ae4b28a1e82ed22029cbca
PATCH_24=tools/pdfium/patches/0024-parser-keep-large-streams-in-file.patch
WAIT_LIMIT_S=$((12 * 3600))
WORKFLOWS=("CI" "Core tests" "iOS CI" "Android CI" "macOS app")
TRAILER='Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019L96ep3qyYPtJ6cxdvq1ya'

export GH_REPO
mkdir -p "$MARKS" "$VARS" "$BAT" "$STATE/tmp" "$HOME"
exec >>"$STATE/pipeline.log" 2>&1

log() { echo "$(date -u +%FT%TZ) $*"; }
status() { echo "$(date -u +%FT%TZ) $*" >"$STATE/STATUS"; log "STATUS: $*"; }
is_done() { [ -f "$MARKS/$1.done" ]; }
mark() { date -u +%FT%TZ >"$MARKS/$1.done"; log "stage $1 done"; }
setvar() { printf '%s' "$2" >"$VARS/$1"; }
getvar() { cat "$VARS/$1" 2>/dev/null; }

idle() {
    status "idle: $1"
    exec sleep infinity
}

comment() {
    local f="$STATE/tmp/comment.md"
    printf '%s\n' "$1" >"$f"
    gh issue comment "$ISSUE" --body-file "$f" >/dev/null || log "comment failed"
}

fail() {
    local stage=$1 why=$2
    log "FAILED at $stage: $why"
    printf 'stage=%s\n%s\n' "$stage" "$why" >"$STATE/FAILED"
    comment "**kdocker2 pipeline stopped at stage \`$stage\`.**

$why

**To resume:** \`ssh kdocker2\`, fix the cause (log: \`~/megapdf/state/pipeline.log\`), then \`rm ~/megapdf/state/FAILED\` and \`docker restart megapdf-151\`. Stages already marked in \`~/megapdf/state/markers/\` are skipped; delete a marker to redo that stage."
    idle "failed at $stage"
}

[ -f "$STATE/DONE" ] && idle "done"
[ -f "$STATE/FAILED" ] && idle "failed (see FAILED)"

git config --global credential.helper '!gh auth git-credential'
git config --global user.name "David Seaman"
git config --global user.email "dave@drscapital.com"
git config --global advice.detachedHead false

latest_run() {  # <workflow name> <full sha> -> "id status conclusion" of the newest run on that sha
    gh run list --workflow "$1" --limit 60 --json databaseId,headSha,status,conclusion,createdAt \
        --jq "[.[] | select(.headSha == \"$2\")] | sort_by(.createdAt) | last | select(. != null) | \"\(.databaseId) \(.status) \(.conclusion)\""
}

wait_run() {  # <run id> <limit seconds> -> echoes conclusion
    local id=$1 limit=$2 start
    start=$(date +%s)
    while :; do
        local s
        s=$(gh run view "$id" --json status,conclusion --jq '"\(.status) \(.conclusion)"' 2>/dev/null)
        case "$s" in completed*) echo "${s#completed }"; return 0 ;; esac
        [ $(($(date +%s) - start)) -gt "$limit" ] && { echo "timeout"; return 1; }
        sleep 60
    done
}

# ---------------------------------------------------------------- 1 clone
stage_clone() {
    is_done 1-clone && return
    status "1 clone"
    rm -rf "$REPO"
    git clone -q "https://github.com/$GH_REPO.git" "$REPO" || fail 1-clone "git clone failed."
    cd "$REPO" && git checkout -q -B "$BRANCH" "origin/$BRANCH" || fail 1-clone "checkout of $BRANCH failed."
    git merge-base --is-ancestor "$BASE_147" HEAD || fail 1-clone "$BRANCH does not contain the #147 base $BASE_147."
    mark 1-clone
    comment "kdocker2 pipeline, stage 1 of 9: cloned and checked out \`$BRANCH\` at $(git rev-parse HEAD). Next: waiting for #147 on main."
}

# ---------------------------------------------------------------- 2 wait for #147
main_has_147() {
    git cat-file -e "origin/main:$PATCH_24" 2>/dev/null || return 1
    local series release
    series=$(for f in $(git ls-tree --name-only origin/main tools/pdfium/patches/ | grep '\.patch$' | LC_ALL=C sort); do
        git show "origin/main:$f"; done | sha256sum | cut -c1-12)
    release=$(git show origin/main:libs/pdfium/RELEASE | tr -d '[:space:]')
    [[ "$release" == *"-megapdf-$series" ]]
}

stage_wait() {
    is_done 2-wait-147 && return
    cd "$REPO" || fail 2-wait-147 "no clone"
    [ -f "$VARS/wait-started" ] || date +%s >"$VARS/wait-started"
    local started polls=0
    started=$(cat "$VARS/wait-started")
    while :; do
        git fetch -q origin || log "fetch failed"
        if main_has_147; then break; fi
        local waited=$(($(date +%s) - started))
        if [ "$waited" -gt "$WAIT_LIMIT_S" ]; then
            fail 2-wait-147 "#147 was not on main after 12 hours of polling (main is $(git rev-parse origin/main)). Nothing was rebased or released. To wait again, also \`rm ~/megapdf/state/vars/wait-started\`."
        fi
        status "2 wait-147: polling, $((waited / 60)) min so far, main $(git rev-parse --short origin/main)"
        polls=$((polls + 1))
        sleep 300
    done
    setvar main-at-147 "$(git rev-parse origin/main)"
    setvar p24-release "$(git show origin/main:libs/pdfium/RELEASE | tr -d '[:space:]')"
    mark 2-wait-147
    comment "kdocker2 pipeline, stage 2 of 9: #147 is on main at $(git rev-parse origin/main), with its patch 0024 and release \`$(basename "$(getvar p24-release)")\`. Next: rebasing \`$BRANCH\` onto main."
}

# ---------------------------------------------------------------- 3 rebase
stage_rebase() {
    is_done 3-rebase && return
    status "3 rebase"
    cd "$REPO" || fail 3-rebase "no clone"
    git fetch -q origin || fail 3-rebase "git fetch failed."
    git checkout -q -B "$BRANCH" "origin/$BRANCH"
    if git ls-tree --name-only origin/main tools/pdfium/patches/ | grep -q '/0025-'; then
        fail 3-rebase "main already has a patch numbered 0025, so this branch's 0025 needs renumbering and its tests re-gating before it can be released."
    fi
    local before
    before=$(git rev-parse HEAD)
    if ! git merge-base --is-ancestor origin/main HEAD; then
        if ! git rebase -q --onto origin/main "$BASE_147" 2>"$STATE/tmp/rebase.err"; then
            local conflicts
            conflicts=$(git diff --name-only --diff-filter=U | sed 's/^/- `/; s/$/`/')
            git rebase --abort
            fail 3-rebase "Rebasing \`$BRANCH\` (commits after $BASE_147) onto main $(git rev-parse origin/main) conflicts in:
$conflicts
Resolve by hand, push the branch, then resume."
        fi
    fi
    [ "$(ls tools/pdfium/patches/*.patch | wc -l)" = 25 ] || fail 3-rebase "after the rebase the series does not have 25 patches."
    if [ "$(git rev-parse HEAD)" != "$before" ]; then
        git push -q --force-with-lease="$BRANCH:$before" origin "HEAD:$BRANCH" || fail 3-rebase "push of the rebased branch failed."
    fi
    mark 3-rebase
    comment "kdocker2 pipeline, stage 3 of 9: \`$BRANCH\` is on main at $(git rev-parse HEAD). Next: releasing PDFium series 1–25."
}

# ---------------------------------------------------------------- 4 release
stage_release() {
    is_done 4-release && return
    status "4 release"
    cd "$REPO" || fail 4-release "no clone"
    git fetch -q origin && git checkout -q -B "$BRANCH" "origin/$BRANCH"
    local head series prefix tag
    head=$(git rev-parse HEAD)
    series=$(cat tools/pdfium/patches/*.patch | sha256sum | cut -c1-12)
    prefix=$(basename "$(tr -d '[:space:]' <libs/pdfium/RELEASE)" | sed 's/-megapdf-.*//')
    tag="$prefix-megapdf-$series"
    setvar p25-tag "$tag"
    if ! gh release view "$tag" >/dev/null 2>&1; then
        local id
        id=$(getvar release-run)
        if [ -z "$id" ] || [ "$(getvar release-run-sha)" != "$head" ]; then
            local t0
            t0=$(date -u +%FT%TZ)
            gh workflow run pdfium-build.yml --ref "$BRANCH" -f release=true || fail 4-release "dispatching the PDFium build failed."
            for _ in $(seq 1 20); do
                sleep 30
                id=$(gh run list --workflow pdfium-build.yml --limit 20 --json databaseId,headSha,createdAt,event \
                    --jq "[.[] | select(.headSha == \"$head\" and .event == \"workflow_dispatch\" and .createdAt >= \"$t0\")] | first | .databaseId // empty")
                [ -n "$id" ] && break
            done
            [ -n "$id" ] || fail 4-release "the dispatched PDFium build did not appear for $head."
            setvar release-run "$id"
            setvar release-run-sha "$head"
            comment "kdocker2 pipeline, stage 4 of 9: PDFium release build dispatched, run $id (series $series). Waiting for it."
        fi
        status "4 release: waiting on run $id"
        local c
        c=$(wait_run "$id" $((5 * 3600)))
        [ "$c" = success ] || fail 4-release "PDFium release run $id ended \`$c\`. Delete \`~/megapdf/state/vars/release-run\` to dispatch again."
        gh release view "$tag" >/dev/null 2>&1 || fail 4-release "run $id succeeded but release \`$tag\` does not exist."
    fi
    setvar p25-release "https://github.com/$GH_REPO/releases/download/$tag"
    mark 4-release
    comment "kdocker2 pipeline, stage 4 of 9: released \`$tag\` (run $(getvar release-run))."
}

# ---------------------------------------------------------------- 5 install
stage_install() {
    is_done 5-install && return
    status "5 install"
    cd "$REPO" || fail 5-install "no clone"
    git fetch -q origin && git checkout -q -B "$BRANCH" "origin/$BRANCH"
    local url tag
    url=$(getvar p25-release)
    tag=$(getvar p25-tag)
    if [ "$(tr -d '[:space:]' <libs/pdfium/RELEASE)" != "$url" ]; then
        echo "$url" >libs/pdfium/RELEASE
        tools/pdfium/install-release.sh >"$STATE/tmp/install.log" 2>&1 || fail 5-install "install-release.sh failed; see \`~/megapdf/state/tmp/install.log\`."
        grep -q '^MEGAPDF_PATCHES=25$' libs/pdfium/win-x64/VERSION || fail 5-install "installed VERSION does not say 25 patches."
        local stray
        stray=$(git status --porcelain | grep -v ' libs/pdfium/' || true)
        [ -z "$stray" ] || fail 5-install "install touched files outside libs/pdfium."
        git add libs/pdfium
        git commit -q -m "Switch every platform to PDFium with patch 25 (#151)

RELEASE is $tag (run $(getvar release-run); every archive carries the same
VERSION, 25 patches). 0025 keeps a reduced copy of a huge image in the page's
image cache, so renders after the first no longer decode the whole image.
Switched by the kdocker2 pipeline (tools/remote/pipeline-151.sh).

$TRAILER" || fail 5-install "commit failed."
        git push -q origin "HEAD:$BRANCH" || fail 5-install "push failed."
    fi
    mark 5-install
    comment "kdocker2 pipeline, stage 5 of 9: every platform switched to \`$tag\` in $(git rev-parse HEAD). Next: core tests on Linux."
}

# ---------------------------------------------------------------- 6 core tests
run_core_tests() {  # -> 0 on pass; output in $STATE/tmp/core-tests.log
    cd "$REPO" || return 1
    rm -rf "$STATE/tmp/core-tests" libs/pdfium/linux-x64
    {
        tools/fetch-pdfium-linux.sh &&
        grep -q '^MEGAPDF_PATCHES=25$' libs/pdfium/linux-x64/VERSION &&
        python3 tools/gen_test_fixtures.py "$STATE/tmp/core-tests/fixtures" &&
        cmake -S core -B "$STATE/tmp/core-tests/build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
            -DMEGAPDF_CORE_TESTS=ON -DMEGAPDF_FIXTURES_DIR="$STATE/tmp/core-tests/fixtures" &&
        cmake --build "$STATE/tmp/core-tests/build" &&
        (cd "$STATE/tmp/core-tests/build" && ctest --output-on-failure -V)
    } >"$STATE/tmp/core-tests.log" 2>&1
}

stage_core_tests() {
    is_done 6-core-tests && return
    status "6 core tests"
    cd "$REPO" && git fetch -q origin && git checkout -q -B "$BRANCH" "origin/$BRANCH"
    run_core_tests || fail 6-core-tests "Linux core tests failed on $(git -C "$REPO" rev-parse HEAD); see \`~/megapdf/state/tmp/core-tests.log\`."
    local line
    line=$(grep -o 'huge image cache: first render.*' "$STATE/tmp/core-tests.log" | head -1)
    setvar core-tests-line "$line"
    mark 6-core-tests
    comment "kdocker2 pipeline, stage 6 of 9: core tests pass on Linux against the release ($(git -C "$REPO" rev-parse HEAD)). \`$line\`. Next: CI on all five workflows."
}

# ---------------------------------------------------------------- 7 CI
# Only the known flaky macOS self-test check failed in this run.
only_more_button_flake() {
    local id=$1 steps fails
    steps=$(gh run view "$id" --json jobs --jq '.jobs[] | select(.conclusion == "failure") | .steps[] | select(.conclusion == "failure") | .name')
    [ -n "$steps" ] || return 1
    echo "$steps" | grep -qv 'self-test' && return 1
    fails=$(gh run view "$id" --log-failed 2>/dev/null | grep -o '\[FAIL\] .*')
    [ -n "$fails" ] || return 1
    echo "$fails" | grep -qv 'does not also press the focused More button' && return 1
    return 0
}

main_has_same_flake() {
    local main id c
    git -C "$REPO" fetch -q origin
    main=$(git -C "$REPO" rev-parse origin/main)
    read -r id _ c <<<"$(latest_run "macOS app" "$main")"
    if [ -z "${id:-}" ]; then
        gh workflow run macos-app.yml --ref main || return 1
        for _ in $(seq 1 20); do
            sleep 30
            read -r id _ c <<<"$(latest_run "macOS app" "$main")"
            [ -n "${id:-}" ] && break
        done
        [ -n "${id:-}" ] || return 1
    fi
    c=$(wait_run "$id" $((3 * 3600)))
    log "macOS app on main $main: run $id $c"
    [ "$c" = failure ] && only_more_button_flake "$id"
}

run_ci() {  # <stage name> -> 0 when all five are green on HEAD; sets $CI_SUMMARY
    local stage=$1 head pr
    cd "$REPO" || return 1
    head=$(git rev-parse HEAD)
    pr=$(gh pr list --head "$BRANCH" --state open --json number --jq '.[0].number // empty')
    if [ -z "$pr" ]; then
        printf '%s\n' "PDFium patch 0025 (#151): a render that needs at most a quarter of a huge image's pixels keeps an area-averaged copy in the page's image cache, instead of decoding the whole image on every render. Details and numbers on #151.

Opened by the kdocker2 pipeline (\`tools/remote/pipeline-151.sh\`) so CI runs on the branch; main is fast-forwarded when everything is green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_019L96ep3qyYPtJ6cxdvq1ya" >"$STATE/tmp/pr.md"
        gh pr create --draft --base main --head "$BRANCH" --title "#151: keep huge images reduced in PDFium's page image cache (patch 0025)" \
            --body-file "$STATE/tmp/pr.md" >/dev/null || fail "$stage" "could not open the draft PR that runs CI."
    fi
    local start
    start=$(date +%s)
    declare -A rerun=() dispatched=() flaky_ok=()
    CI_SUMMARY=""
    while :; do
        local all_done=1 bad="" summary="" elapsed=$(($(date +%s) - start))
        for wf in "${WORKFLOWS[@]}"; do
            local id st c
            read -r id st c <<<"$(latest_run "$wf" "$head")"
            if [ -z "${id:-}" ]; then
                all_done=0
                if [ "$elapsed" -gt 900 ] && [ -z "${dispatched[$wf]:-}" ] && [ "$wf" != "CI" ]; then
                    local file
                    file=$(gh workflow list --json name,path --jq ".[] | select(.name == \"$wf\") | .path")
                    gh workflow run "$(basename "$file")" --ref "$BRANCH" && dispatched[$wf]=1 && log "dispatched $wf"
                fi
                [ "$elapsed" -gt 2700 ] && bad+="- $wf: no run on $head after 45 min"$'\n'
                continue
            fi
            if [ "$st" != completed ]; then all_done=0; continue; fi
            if [ "$c" = success ]; then summary+="$wf $id ✓; "; continue; fi
            if [ "${flaky_ok[$wf]:-}" = "$id" ]; then
                summary+="$wf $id: only the known flaky More-button check, which fails the same way on main; "
                continue
            fi
            if [ -z "${rerun[$wf]:-}" ]; then
                log "$wf run $id ended $c; rerunning failed jobs once"
                gh run rerun "$id" --failed && rerun[$wf]=1
                all_done=0
                sleep 60
                continue
            fi
            if [ "$wf" = "macOS app" ] && only_more_button_flake "$id"; then
                status "$stage: macOS app failed only the More-button check; checking main"
                if main_has_same_flake; then
                    flaky_ok[$wf]=$id
                    summary+="$wf $id: only the known flaky More-button check, which fails the same way on main; "
                    continue
                fi
            fi
            bad+="- $wf: run $id ended \`$c\` (after one rerun of failed jobs)"$'\n'
        done
        [ -n "$bad" ] && { CI_SUMMARY=$bad; return 1; }
        if [ "$all_done" = 1 ]; then CI_SUMMARY=$summary; return 0; fi
        [ "$elapsed" -gt $((6 * 3600)) ] && { CI_SUMMARY="- CI still not finished on $head after 6 h"; return 1; }
        status "$stage: waiting on CI for $head ($((elapsed / 60)) min)"
        sleep 120
    done
}

stage_ci() {
    is_done 7-ci && return
    status "7 ci"
    cd "$REPO" && git fetch -q origin && git checkout -q -B "$BRANCH" "origin/$BRANCH"
    run_ci 7-ci || fail 7-ci "CI is not green on $(git rev-parse HEAD):
$CI_SUMMARY"
    setvar ci-summary "$CI_SUMMARY"
    mark 7-ci
    comment "kdocker2 pipeline, stage 7 of 9: CI green on $(git rev-parse HEAD): $CI_SUMMARY
Next: edit battery on kdocker2, p24 then p25."
}

# ---------------------------------------------------------------- 8 battery
build_harness() {  # <label> <release url>
    local label=$1 url=$2 dir="$BAT/$1"
    [ -x "$dir/harness/MegaPDF.Stress" ] && [ -f "$dir/harness/libpdfium.so" ] && return 0
    rm -rf "$dir" && mkdir -p "$dir/pdfium"
    curl -fsSL "$url/pdfium-linux-x64.tgz" -o "$dir/pdfium.tgz" && tar xzf "$dir/pdfium.tgz" -C "$dir/pdfium" || return 1
    cmake -S "$REPO/core" -B "$dir/core" -G Ninja -DCMAKE_BUILD_TYPE=Release -DMEGAPDF_PDFIUM_DIR="$dir/pdfium" >"$dir/core.log" 2>&1 &&
        cmake --build "$dir/core" --target megapdf_core >>"$dir/core.log" 2>&1 || return 1
    dotnet build "$REPO/tools/stress/MegaPDF.Stress" -c Release -o "$dir/harness" \
        -p:MegaPdfCoreBuildCommand=true -p:MegaPdfCoreArtifact="$dir/core/libmegapdf_core.so" >"$dir/dotnet.log" 2>&1 || return 1
    cp "$dir/core/libmegapdf_core.so" "$dir/pdfium/lib/libpdfium.so" "$dir/harness/"
}

battery_finished() {  # <out dir>
    local total
    total=$(wc -l <"$1/files.txt" 2>/dev/null) || return 1
    tail -n 5 "$1/run.log" 2>/dev/null | grep -q "finished: done=$total/$total "
}

run_battery() {  # <label>
    local label=$1 out="$BAT/core-edits-$1" h="$BAT/$1/harness/MegaPDF.Stress"
    export TMPDIR="$STATE/tmp"
    for attempt in 1 2 3; do
        battery_finished "$out" && break
        status "8 battery $label: run (attempt $attempt)"
        "$h" run --root "$CORPUS" --out "$out" --workers 16 --scale 1 --phases edits >>"$BAT/core-edits-$label.out.log" 2>&1
    done
    battery_finished "$out" || return 1
    # Hangs are rerun solo once before they count: drop their rows, resume with one worker.
    if [ ! -f "$out/.solo-rerun" ]; then
        local hangs
        hangs=$(python3 - "$out/results.jsonl" <<'PY'
import json, sys
path = sys.argv[1]
rows, hangs = [], 0
for line in open(path, encoding="utf-8"):
    try:
        r = json.loads(line)
    except json.JSONDecodeError:
        continue
    if r.get("outcome") == "hang":
        hangs += 1
        continue
    rows.append(line if line.endswith("\n") else line + "\n")
if hangs:
    import shutil
    shutil.copy(path, path + ".before-solo")
    open(path, "w", encoding="utf-8").writelines(rows)
print(hangs)
PY
)
        echo "$hangs" >"$out/.solo-rerun"
        if [ "${hangs:-0}" -gt 0 ]; then
            status "8 battery $label: rerunning $hangs hang(s) solo"
            "$h" run --root "$CORPUS" --out "$out" --workers 1 --scale 1 --phases edits >>"$BAT/core-edits-$label.out.log" 2>&1
            battery_finished "$out" || return 1
        fi
    fi
}

stage_battery() {
    is_done 8-battery && return
    cd "$REPO" && git fetch -q origin && git checkout -q -B "$BRANCH" "origin/$BRANCH"
    if ! is_done 8a-battery-p24; then
        status "8 battery: building the p24 harness"
        build_harness p24 "$(getvar p24-release)" || fail 8-battery "building the p24 harness failed; see \`~/megapdf/state/battery/p24/\`."
        run_battery p24 || fail 8-battery "the p24 battery did not finish; see \`~/megapdf/state/battery/core-edits-p24/run.log\`."
        mark 8a-battery-p24
    fi
    if ! is_done 8b-battery-p25; then
        status "8 battery: building the p25 harness"
        build_harness p25 "$(getvar p25-release)" || fail 8-battery "building the p25 harness failed; see \`~/megapdf/state/battery/p25/\`."
        run_battery p25 || fail 8-battery "the p25 battery did not finish; see \`~/megapdf/state/battery/core-edits-p25/run.log\`."
        mark 8b-battery-p25
    fi
    python3 "$REPO/tools/stress/compare_runs.py" "$BAT/core-edits-p24" "$BAT/core-edits-p25" >"$BAT/compare.txt" 2>&1 ||
        fail 8-battery "compare_runs.py failed."
    local outcomes edits p24out p25out
    outcomes=$(grep -o 'document outcome changes: .*' "$BAT/compare.txt")
    edits=$(grep -oE '[0-9]+ edits compared, [0-9]+ changes' "$BAT/compare.txt")
    p24out=$(sed -n '/^== core-edits-p24:/,/outcomes:/p' "$BAT/compare.txt" | grep -o 'outcomes: .*')
    p25out=$(sed -n '/^== core-edits-p25:/,/outcomes:/p' "$BAT/compare.txt" | grep -o 'outcomes: .*')
    setvar battery-summary "p24 $p24out (hangs rerun solo: $(cat "$BAT/core-edits-p24/.solo-rerun")); p25 $p25out (hangs rerun solo: $(cat "$BAT/core-edits-p25/.solo-rerun")); $outcomes; $edits"
    local transitions
    transitions=$(sed -n '/^== transitions/,$p' "$BAT/compare.txt")
    if [ "$outcomes" != "document outcome changes: none" ] || ! [[ "$edits" =~ \ 0\ changes$ ]]; then
        fail 8-battery "Battery \`core-edits-p25\` against \`core-edits-p24\` differs:
\`\`\`
$transitions
\`\`\`
Look at \`~/megapdf/state/battery/compare.txt\` on kdocker2. If the change is explained and accepted, \`touch ~/megapdf/state/markers/8-battery.done\` there and resume."
    fi
    mark 8-battery
    comment "kdocker2 pipeline, stage 8 of 9: battery \`core-edits-p25\` against \`core-edits-p24\` on kdocker2 (16 workers, same corpus):
\`\`\`
$transitions
\`\`\`
$(getvar battery-summary)

Next: fast-forward main."
}

# ---------------------------------------------------------------- 9 merge
stage_merge() {
    is_done 9-merge && return
    cd "$REPO" || fail 9-merge "no clone"
    for attempt in 1 2 3; do
        status "9 merge (attempt $attempt)"
        git fetch -q origin && git checkout -q -B "$BRANCH" "origin/$BRANCH"
        if git merge-base --is-ancestor origin/main HEAD; then
            if git push -q origin "HEAD:main"; then
                mark 9-merge
                break
            fi
            log "push to main rejected; main moved"
            continue
        fi
        # Main moved: rebase, then test and CI again (the battery stands unless PDFium changed).
        if ! git diff --quiet "$(getvar main-at-147)" origin/main -- tools/pdfium libs/pdfium/RELEASE; then
            fail 9-merge "main changed its PDFium patches or release since #147 merged, so series 1–25 must be rebuilt on top of it."
        fi
        local before
        before=$(git rev-parse HEAD)
        if ! git rebase -q origin/main; then
            local conflicts
            conflicts=$(git diff --name-only --diff-filter=U | sed 's/^/- `/; s/$/`/')
            git rebase --abort
            fail 9-merge "Rebasing onto main $(git rev-parse origin/main) before the merge conflicts in:
$conflicts"
        fi
        git push -q --force-with-lease="$BRANCH:$before" origin "HEAD:$BRANCH" || fail 9-merge "push of the rebased branch failed."
        comment "kdocker2 pipeline, stage 9: main moved, so \`$BRANCH\` was rebased to $(git rev-parse HEAD). Running the core tests and CI again."
        run_core_tests || fail 9-merge "Linux core tests failed after rebasing onto main; see \`~/megapdf/state/tmp/core-tests.log\`."
        run_ci 9-merge || fail 9-merge "CI is not green after rebasing onto main:
$CI_SUMMARY"
        setvar ci-summary "$CI_SUMMARY"
    done
    is_done 9-merge || fail 9-merge "main kept moving; three merge attempts failed."
    local head
    head=$(git rev-parse HEAD)
    comment "**Done: on main at $head** (fast-forward, by the kdocker2 pipeline).

- **PDFium patch 0025** keeps a reduced copy of a huge image in the page's image cache, and release \`$(getvar p25-tag)\` (run $(getvar release-run)) is installed on every platform.
- **\`huge-image-page.pdf\`, Linux x64:** the first render is unchanged. Every later render is 80–330 ms at every zoom, down from 1.0–1.7 s. Peak memory is 452 → 530 MB.
- **Linux core tests** pass against the release: \`$(getvar core-tests-line)\`
- **CI:** $(getvar ci-summary)
- **Battery on kdocker2:** $(getvar battery-summary)
- **Corpus renders** (earlier, p24 against p25): identical at 1,200 px. At 2,000–4,000 px, 5 documents with scanned or print-resolution images lose halftone moiré, and their 200 px renders stay within 4 levels."
    gh issue close "$ISSUE" >/dev/null || log "closing the issue failed"
    date -u +%FT%TZ >"$STATE/DONE"
    idle "done"
}

log "pipeline start (pid $$)"
stage_clone
stage_wait
stage_rebase
stage_release
stage_install
stage_core_tests
stage_ci
stage_battery
stage_merge
