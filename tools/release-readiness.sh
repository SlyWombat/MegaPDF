#!/usr/bin/env bash
# What stands between this repo and the next run of docs/RELEASING.md.
#
#   tools/release-readiness.sh [--machines] [--stores]
#
# One line per check, "OK" or "ATTENTION", with the evidence. Exit 1 if anything needs
# attention. Reads the tree it is run from (and says which); touches nothing except
# `git fetch` (refs only). No secrets are read or printed.
#
#   --machines  leftovers on mac-mini, kdocker2, kdocker3 (over ssh) and on this Windows
#               host (via powershell.exe). Off by default: needs ssh.
#   --stores    the RELEASING.md section 4 read-backs: live version at Apple, Google,
#               Microsoft, the APT repository and the Snap Store.
#
# Needs: git, gh (authenticated), python3. curl for --stores, ssh for --machines.
# See docs/RELEASING.md section 0.
set -u

MACHINES=0; STORES=0
for a in "$@"; do
  case "$a" in
    --machines) MACHINES=1 ;;
    --stores) STORES=1 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "unknown argument: $a" >&2; exit 2 ;;
  esac
done

SHARED=/mnt/d/Projects/MegaPDF
ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || { echo "not inside a git checkout" >&2; exit 2; }
cd "$ROOT" || exit 2

ATTN=0
ok()   { printf 'OK         %s\n' "$*"; }
attn() { printf 'ATTENTION  %s\n' "$*"; ATTN=$((ATTN+1)); }
head_() { printf '\n== %s\n' "$*"; }
# Read "OK|ATTENTION<TAB>text" lines on stdin (from the python helpers) and emit them.
emit() {
  local st tx
  while IFS=$'\t' read -r st tx; do
    case "$st" in OK) ok "$tx" ;; ATTENTION) attn "$tx" ;; INFO) printf '           %s\n' "$tx" ;; esac
  done
}

echo "release-readiness: reading $ROOT ($(git rev-parse --abbrev-ref HEAD) @ $(git rev-parse --short HEAD)), $(date -u +%Y-%m-%dT%H:%MZ)"
git fetch -q origin 2>/dev/null || attn "git fetch origin failed; everything below may be stale"
MAIN=$(git rev-parse origin/main)
REPO=$(gh repo view --json nameWithOwner -q .nameWithOwner 2>/dev/null)
[ -n "$REPO" ] || { attn "gh cannot see the repository (not authenticated?)"; exit 1; }

# ---------------------------------------------------------------- 1. main CI
head_ "1. main CI (push runs on main)"
emit < <(gh run list --branch main --event push --limit 100 \
  --json workflowName,conclusion,status,headSha,createdAt 2>/dev/null |
python3 -I -c '
import json, sys
main = sys.argv[1]
runs = json.load(sys.stdin)
seen = {}
for r in runs:                      # newest first
    seen.setdefault(r["workflowName"], r)
if not runs:
    print("ATTENTION\tno push runs on main found")
for name, r in sorted(seen.items()):
    sha = r["headSha"][:9]
    st = r["status"]; cc = r["conclusion"]
    if r["status"] != "completed":
        print(f"ATTENTION\t{name}: {st} on {sha}")
    elif r["conclusion"] not in ("success", "skipped", "neutral"):
        print(f"ATTENTION\t{name}: {cc} on {sha}")
    else:
        print(f"OK\t{name}: {cc} on {sha}" + ("" if r["headSha"] == main else " (not the newest main commit; paths-ignore or no trigger)"))
at_head = [r for r in runs if r["headSha"] == main]
if not at_head:
    print(f"ATTENTION\tno push run at all on the newest main commit {main[:9]}")
elif any(r["status"] != "completed" for r in at_head):
    print(f"ATTENTION\tnewest main commit {main[:9]} has runs still in progress")
' "$MAIN")

# ---------------------------------------------------------------- 2. open PRs
head_ "2. open pull requests"
PRS=$(gh pr list --state open --limit 100 --json number,title,createdAt,headRefOid,headRefName,baseRefName,isDraft,mergeable 2>/dev/null)
NPR=$(printf '%s' "$PRS" | python3 -I -c 'import json,sys;print(len(json.load(sys.stdin)))')
if [ "$NPR" = 0 ]; then ok "no open pull requests"; fi
printf '%s' "$PRS" | python3 -I -c '
import json, sys
for p in json.load(sys.stdin): print(p["number"], p["headRefOid"], p["baseRefName"], p["mergeable"], p["isDraft"], p["createdAt"], p["headRefName"], sep="\t")
' | while IFS=$'\t' read -r n sha base mrg draft created ref; do
  age=$(( ( $(date +%s) - $(date -d "$created" +%s) ) / 86400 ))
  runs=$(gh run list --commit "$sha" --limit 50 --json event,conclusion,status,workflowName 2>/dev/null)
  verdict=$(printf '%s' "$runs" | python3 -I -c '
import json, sys
rs = [r for r in json.load(sys.stdin) if r["event"] == "pull_request"]
if not rs: print("no pull_request run on the head SHA (\"no checks reported\")")
else:
    run = [r for r in rs if r["status"] != "completed"]
    bad = [r["workflowName"] for r in rs if r["status"] == "completed" and r["conclusion"] not in ("success", "skipped", "neutral")]
    if run: print("still running: " + ", ".join(sorted({r["workflowName"] for r in run})))
    elif bad: print("failing: " + ", ".join(sorted(set(bad))))
    else: print("GREEN %d runs" % len(rs))
')
  msg="#$n ${age}d old, $ref -> $base, head ${sha:0:9}: $verdict"
  [ "$draft" = true ] && msg="$msg [draft]"
  problem=0
  case "$verdict" in GREEN*) ;; *) problem=1 ;; esac
  [ "$base" != main ] && { problem=1; msg="$msg; STACKED base (no CI will run)"; }
  [ "$mrg" = CONFLICTING ] && { problem=1; msg="$msg; merge conflicts"; }
  if [ $problem = 1 ]; then printf 'ATTENTION  %s\n' "$msg"; else printf 'OK         %s\n' "$msg"; fi
done > "${TMPDIR:-/tmp}/rr-prs.$$"
cat "${TMPDIR:-/tmp}/rr-prs.$$"; ATTN=$((ATTN + $(grep -c '^ATTENTION' "${TMPDIR:-/tmp}/rr-prs.$$"))); rm -f "${TMPDIR:-/tmp}/rr-prs.$$"

# ---------------------------------------------------------------- 3. branches
head_ "3. wip/* branches (remote and local)"
OPEN_PR_REFS=$(printf '%s' "$PRS" | python3 -I -c 'import json,sys;print("\n".join(p["headRefName"] for p in json.load(sys.stdin)))')
{ git for-each-ref --format='%(refname:short)' 'refs/remotes/origin/wip/*' | sed 's#^origin/##'
  git for-each-ref --format='%(refname:short)' 'refs/heads/wip/*'; } | sort -u > "${TMPDIR:-/tmp}/rr-br.$$"
[ -s "${TMPDIR:-/tmp}/rr-br.$$" ] || ok "no wip/* branches"
while read -r b; do
  where=""
  git show-ref -q --verify "refs/remotes/origin/$b" && where="remote"
  git show-ref -q --verify "refs/heads/$b" && where="${where:+$where+}local"
  ref=$b; git show-ref -q --verify "refs/remotes/origin/$b" && ref=origin/$b
  tip=$(git rev-parse "$ref")
  age=$(( ( $(date +%s) - $(git log -1 --format=%ct "$ref") ) / 86400 ))
  uniq=$(git cherry origin/main "$ref" | grep -c '^+')
  # A branch with no commits of its own sits at some origin/main, today's or an older one: a
  # fresh worktree, not a merged branch. Merged is "no unmerged commits AND a merged PR".
  merged_pr=$(gh pr list --head "$b" --state merged --json number -q 'length' 2>/dev/null)
  if git merge-base --is-ancestor "$tip" origin/main && [ "${merged_pr:-0}" = 0 ]; then ok "$b ($where): at origin/main (${age}d ago), no commits yet"; continue; fi
  if [ "$uniq" = 0 ]; then attn "$b ($where): MERGED, 0 unmerged commits, ${age}d: delete"; continue; fi
  if printf '%s\n' "$OPEN_PR_REFS" | grep -qxF "$b"; then ok "$b ($where): $uniq unmerged, ${age}d, has an open PR"; continue; fi
  iss=$(gh issue list --state open --search "\"$b\" in:title,body,comments" --json number -q '[.[].number|"#"+tostring]|join(" ")' 2>/dev/null)
  if [ -n "$iss" ]; then ok "$b ($where): $uniq unmerged, ${age}d, named by issue $iss"
  else attn "$b ($where): $uniq unmerged, ${age}d, no open PR and no open issue names it: ORPHAN"; fi
done < "${TMPDIR:-/tmp}/rr-br.$$" > "${TMPDIR:-/tmp}/rr-br.out.$$"
cat "${TMPDIR:-/tmp}/rr-br.out.$$"; ATTN=$((ATTN + $(grep -c '^ATTENTION' "${TMPDIR:-/tmp}/rr-br.out.$$"))); rm -f "${TMPDIR:-/tmp}/rr-br.$$" "${TMPDIR:-/tmp}/rr-br.out.$$"

# ---------------------------------------------------------------- 4. worktrees
head_ "4. worktrees"
git worktree list --porcelain | python3 -I -c '
import sys
cur = {}
out = []
for line in sys.stdin.read().split("\n") + [""]:
    if not line:
        if cur: out.append(cur); cur = {}
        continue
    k, _, v = line.partition(" ")
    cur[k] = v
for w in out:
    print(w.get("worktree"), w.get("branch", "").replace("refs/heads/", "") or "(detached)", "prunable" if "prunable" in w else "-", sep="\t")
' > "${TMPDIR:-/tmp}/rr-wt.$$"
while IFS=$'\t' read -r path br prun; do
  [ "$path" = "$SHARED" ] && continue
  if [ "$prun" = prunable ] || [ ! -d "$path" ]; then attn "worktree $path: directory gone (git worktree prune)"; continue; fi
  if [ "$br" = "(detached)" ]; then attn "worktree $path: detached HEAD"; continue; fi
  if ! git show-ref -q --verify "refs/heads/$br"; then attn "worktree $path: branch $br deleted"; continue; fi
  tip=$(git rev-parse "$br"); uniq=$(git cherry origin/main "$br" | grep -c '^+')
  dirty=$(git -C "$path" status --porcelain 2>/dev/null | wc -l)
  merged_pr=$(gh pr list --head "$br" --state merged --json number -q 'length' 2>/dev/null)
  if [ "$uniq" = 0 ] && ! { git merge-base --is-ancestor "$tip" origin/main && [ "${merged_pr:-0}" = 0 ]; }; then attn "worktree $path: branch $br is MERGED: remove the worktree and the branch"
  else ok "worktree $path: $br, $uniq unmerged, $dirty dirty file(s)"; fi
done < "${TMPDIR:-/tmp}/rr-wt.$$" > "${TMPDIR:-/tmp}/rr-wt.out.$$"
cat "${TMPDIR:-/tmp}/rr-wt.out.$$"; ATTN=$((ATTN + $(grep -c '^ATTENTION' "${TMPDIR:-/tmp}/rr-wt.out.$$"))); rm -f "${TMPDIR:-/tmp}/rr-wt.$$" "${TMPDIR:-/tmp}/rr-wt.out.$$"

# ---------------------------------------------------------------- 5. the tree
head_ "5. the trees"
tree_check() {  # path, must-be-main
  local p=$1 mustmain=$2 br behind ahead dirty
  [ -d "$p/.git" ] || [ -f "$p/.git" ] || { attn "$p: not a checkout here"; return; }
  br=$(git -C "$p" rev-parse --abbrev-ref HEAD)
  behind=$(git -C "$p" rev-list --count HEAD..origin/main)
  ahead=$(git -C "$p" rev-list --count origin/main..HEAD)
  dirty=$(git -C "$p" status --porcelain | wc -l)
  local msg="$p on $br: behind $behind, ahead $ahead, $dirty dirty file(s)" bad=0
  [ "$dirty" != 0 ] && bad=1
  [ "$behind" != 0 ] && bad=1
  if [ "$mustmain" = 1 ]; then [ "$br" != main ] && bad=1; [ "$ahead" != 0 ] && bad=1; fi
  if [ $bad = 1 ]; then attn "$msg"; else ok "$msg"; fi
}
tree_check "$ROOT" 0
[ "$ROOT" != "$SHARED" ] && [ -d "$SHARED" ] && tree_check "$SHARED" 1
[ "$ROOT" = "$SHARED" ] && git -C "$ROOT" rev-parse --abbrev-ref HEAD | grep -qx main || { [ "$ROOT" = "$SHARED" ] && attn "shared checkout is not on main"; }

# ---------------------------------------------------------------- 6/7. versions and docs
head_ "6. versions agree (docs/RELEASING.md section 1 table)"
python3 -I - "$ROOT" <<'PY' > "${TMPDIR:-/tmp}/rr-ver.$$"
import re, sys, pathlib
root = pathlib.Path(sys.argv[1])
lines = (root / "docs/RELEASING.md").read_text(encoding="utf-8").split("\n")
VER = r"(\d+\.\d+\.\d+)(?:\.0)?"
start = next(i for i, l in enumerate(lines) if l.startswith("| | File | Field |"))
rows = []
for l in lines[start + 2:]:
    if not l.startswith("|"): break
    cells = [c.strip() for c in l.strip().strip("|").split("|")]
    if len(cells) < 3: continue
    f = re.search(r"`([^`]+)`", cells[1])
    if not f: continue
    rows.append((f.group(1), cells[2], cells[0]))

def regex_from(span):
    ms = list(re.finditer(r"x\.y\.z(?:\.0)?", span))
    if not ms: return None
    end = ms[-1].end()
    if end < len(span) and span[end] in "\"'": end += 1
    span = span[:end]
    parts = re.split(r"(x\.y\.z(?:\.0)?|…|<date>|\s+)", span)
    out = []
    for p in parts:
        if re.fullmatch(r"x\.y\.z(?:\.0)?", p or ""): out.append(VER)
        elif p == "…": out.append(r".*?")
        elif p == "<date>": out.append(r'[^"]*')
        elif p and p.isspace(): out.append(r"[\s=:]*")
        else: out.append(re.escape(p))
    return "".join(out)

def body(path):
    txt = (root / path).read_text(encoding="utf-8", errors="replace")
    return "\n".join(l for l in txt.split("\n") if not l.lstrip().startswith("#"))

found = {}  # (path, regex) -> version(s)
for path, field, _ in rows:
    spans = re.findall(r"`([^`]+)`", field)
    rx = next((regex_from(s) for s in spans if "x.y.z" in s), None)
    key = (path, rx)
    if key in found: continue
    try: txt = body(path)
    except OSError:
        found[key] = None; print(f"ATTENTION\t{path}: file missing"); continue
    if rx is None:      # prose row (the Linux page): every version string in the file
        vs = sorted(set(re.findall(r"\b(\d+\.\d+\.\d+)\b", txt)))
        found[key] = vs
    else:
        m = re.search(rx, txt, re.S)
        found[key] = [m.group(1)] if m else []
        if not m: print(f"ATTENTION\t{path}: pattern not found ({field})")
allv = {}
for (path, rx), vs in found.items():
    for v in (vs or []): allv.setdefault(v, []).append(path)
shown = set()
for (path, rx), vs in found.items():
    if vs and (path, tuple(vs)) not in shown:
        shown.add((path, tuple(vs))); print(f"INFO\t{path}: {', '.join(vs)}")
if len(allv) == 1:
    v = next(iter(allv))
    print(f"OK\tall {len(shown)} distinct locations say {v}")
    print(f"VERSION\t{v}")
else:
    print("ATTENTION\tlocations disagree: " + "; ".join(f"{v} in {', '.join(sorted(set(p)))}" for v, p in sorted(allv.items())))
    if allv: print(f"VERSION\t{sorted(allv, key=lambda s: tuple(map(int, s.split('.'))))[-1]}")
PY
VERSION=$(awk -F'\t' '$1=="VERSION"{print $2}' "${TMPDIR:-/tmp}/rr-ver.$$")
grep -v '^VERSION' "${TMPDIR:-/tmp}/rr-ver.$$" > "${TMPDIR:-/tmp}/rr-ver2.$$"
emit < "${TMPDIR:-/tmp}/rr-ver2.$$"
rm -f "${TMPDIR:-/tmp}/rr-ver.$$" "${TMPDIR:-/tmp}/rr-ver2.$$"

head_ "7. docs name the shipped version ${VERSION:-?}"
README_V=$(grep -o 'Store status as of [0-9][0-9.]*[0-9]' README.md | head -1 | awk '{print $NF}')
SUPPORT_V=$(grep -o 'The current release is <strong>[0-9][0-9.]*[0-9]' website/megapdf/support/index.html | head -1 | sed 's/.*<strong>//')
META_V=$(grep -o '<release version="[0-9][0-9.]*"' tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml | head -1 | sed 's/.*"\(.*\)"/\1/')
for pair in "README.md 'Store status as of':$README_V" "website support page 'The current release is':$SUPPORT_V" "metainfo newest <release>:$META_V"; do
  label=${pair%:*}; val=${pair##*:}
  if [ -z "$val" ]; then attn "$label: not found"
  elif [ "$val" = "$VERSION" ]; then ok "$label: $val"
  else attn "$label says ${val}, shipped version is $VERSION"; fi
done

# ---------------------------------------------------------------- 7b. support links
head_ "7b. store support links (#662)"
emit < <(python3 -I - "$ROOT" <<'PY'
import re, sys
root = sys.argv[1]
S = "https://electricrv.ca/megapdf/support/"
def text(p):
    try:
        return open(f"{root}/{p}", encoding="utf-8").read()
    except OSError:
        return None
def check(label, path, ok_when):
    t = text(path)
    if t is None:
        print(f"ATTENTION\t{path}: file missing"); return
    good = ok_when(t)
    print(("OK" if good else "ATTENTION") + f"\t{label}: {path}" + ("" if good else " does not carry the support page URL"))
check("Apple supportUrl", "tools/asc_publish.py",
      lambda t: f'SUPPORT_URL = "{S}"' in t and '"supportUrl": SUPPORT_URL' in t)
check("Play contactWebsite", "tools/play_listing.py", lambda t: f'CONTACT_WEBSITE = "{S}"' in t)
def ms(t):
    sec = t.split("## Other fields", 1)
    if len(sec) < 2: return False
    blocks = re.findall(r"```[^\n]*\n(.*?)```", sec[1].split("\n## ", 1)[0], re.S)
    return len(blocks) >= 3 and blocks[2].strip() == S
check("Microsoft supportContact (third Other-fields block)", "docs/microsoft-store-listing.md", ms)
check("Snap contact and issues", "tools/linux/snap/snapcraft.yaml.in",
      lambda t: f"\ncontact: {S}\n" in t and f"\nissues: {S}\n" in t)
check("metainfo help and contact", "tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml",
      lambda t: f'<url type="help">{S}</url>' in t and f'<url type="contact">{S}</url>' in t)
PY
)

# ---------------------------------------------------------------- 8. milestone
head_ "8. milestone for ${VERSION:-?} (informational; milestones are Dave's)"
MS=$(gh api "repos/$REPO/milestones?state=open&per_page=100" 2>/dev/null | python3 -I -c '
import json, sys
v = sys.argv[1]; xy = ".".join(v.split(".")[:2])
for m in json.load(sys.stdin):
    if m["title"] in (v, xy): print(m["number"], m["title"], m["open_issues"], m["closed_issues"])
' "${VERSION:-0}")
if [ -z "$MS" ]; then ok "no open milestone titled ${VERSION%.*} or $VERSION"
else
  while read -r num title open closed; do
    nums=$(gh issue list --state open --milestone "$title" --limit 200 --json number -q '[.[].number|"#"+tostring]|join(" ")')
    ok "milestone $title: $open open, $closed closed${nums:+: $nums}"
  done <<< "$MS"
fi

# ---------------------------------------------------------------- 9. coverage map
head_ "9. coverage map"
if out=$(python3 tools/qa/check-matrix.py --check 2>&1); then ok "check-matrix.py --check: $(printf '%s' "$out" | tail -1)"
else attn "check-matrix.py --check failed: $(printf '%s' "$out" | tail -3 | tr '\n' ' ')"; fi

# ---------------------------------------------------------------- 10. machines
if [ $MACHINES = 1 ]; then
  head_ "10. machines (leftovers)"
  SSHO="-o BatchMode=yes -o ConnectTimeout=8"
  remote() {  # host, label, command
    local out
    if ! out=$(ssh $SSHO "$1" bash -s 2>&1 <<<"$3"); then attn "$1: ssh failed or command errored: $(printf '%s' "$out" | head -1)"; return; fi
    if [ -n "$out" ]; then attn "$1 $2: $(printf '%s' "$out" | tr '\n' ' ')"; else ok "$1 $2: nothing"; fi
  }
  remote mac-mini "~/megapdf-* ~/mp-*" 'for f in "$HOME"/megapdf-* "$HOME"/mp-*; do [ -e "$f" ] && echo "$f"; done; true'
  remote mac-mini "simulators" 'xcrun simctl list devices 2>/dev/null | grep -E "Booted|megapdf-|MegaPDF" ; true'
  remote kdocker2 "~/megapdf-* ~/mp-* /data/megapdf-work/*" 'for f in "$HOME"/megapdf-* "$HOME"/mp-* /data/megapdf-work/*; do [ -e "$f" ] && echo "$f"; done; true'
  remote kdocker3 "~/megapdf-* ~/mp-*" 'for f in "$HOME"/megapdf-* "$HOME"/mp-*; do [ -e "$f" ] && echo "$f"; done; true'
  if command -v powershell.exe >/dev/null; then
    o=$(powershell.exe -NoProfile -Command 'Get-ChildItem D:\megapdf-qa -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }' 2>&1 | tr -d '\r')
    if [ -n "$o" ]; then attn "GPD-DAVE D:\\megapdf-qa: $(printf '%s' "$o" | tr '\n' ' ')"; else ok "GPD-DAVE D:\\megapdf-qa: empty"; fi
    o=$(powershell.exe -NoProfile -Command 'Get-ChildItem C:\temp -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*megapdf*" -or $_.Name -like "wack*" } | ForEach-Object { $_.Name }' 2>&1 | tr -d '\r')
    if [ -n "$o" ]; then attn "GPD-DAVE C:\\temp: $(printf '%s' "$o" | tr '\n' ' ')"; else ok "GPD-DAVE C:\\temp: no megapdf/wack scratch"; fi
    o=$(powershell.exe -NoProfile -Command 'Get-AppxPackage ElectricRV.MegaPDF -ErrorAction SilentlyContinue | ForEach-Object { $_.Name + " " + $_.Version }' 2>&1 | tr -d '\r')
    if [ -n "$o" ]; then attn "GPD-DAVE package installed: $o (remove a test build; a Store install is Dave's)"; else ok "GPD-DAVE: no ElectricRV.MegaPDF package installed"; fi
    o=$(powershell.exe -NoProfile -Command 'Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like "MegaPDF*" } | ForEach-Object { $_.TaskName }' 2>&1 | tr -d '\r')
    if [ -n "$o" ]; then attn "GPD-DAVE scheduled task(s): $(printf '%s' "$o" | tr '\n' ' ')"; else ok "GPD-DAVE: no MegaPDF scheduled task"; fi
  else
    attn "powershell.exe not found: Windows host not checked"
  fi
fi

# ---------------------------------------------------------------- 11. stores
if [ $STORES = 1 ]; then
  head_ "11. store read-backs (RELEASING.md section 4)"
  cmp_ver() {  # label live
    if [ -z "$2" ]; then attn "$1: could not read"
    elif [ "$2" = "$VERSION" ] || [ "$2" = "$VERSION.0" ]; then ok "$1: live $2 = repo $VERSION"
    else attn "$1: live $2, repo $VERSION"; fi
  }
  a=$(curl -s -m 20 'https://itunes.apple.com/lookup?id=6799522972' | python3 -I -c 'import json,sys; r=json.load(sys.stdin)["results"][0]; print(r["version"])' 2>/dev/null)
  cmp_ver "Apple App Store (iOS)" "$a"
  code=$(curl -s -m 20 -o /dev/null -w '%{http_code}' 'https://play.google.com/store/apps/details?id=ca.electricrv.megapdf')
  if [ "$code" = 200 ]; then ok "Google Play listing: HTTP 200 (public; the page does not state the version, see tools/play.sh listing status)"
  else attn "Google Play listing: HTTP $code (404 until public; tools/play.sh listing status for the track)"; fi
  l=$(curl -s -m 20 https://electricrv.ca/megapdf/apt/dists/stable/main/binary-amd64/Packages | awk '/^Version:/{print $2}' | sort -V | tail -1)   # the pool keeps the newest three; the newest must be the shipped one
  cmp_ver "APT repository (Linux)" "${l%-*}"
  snapinfo=$(curl -s -m 20 -H 'Snap-Device-Series: 16' 'https://api.snapcraft.io/v2/snaps/info/megapdf?fields=version,revision,links')
  s=$(printf '%s' "$snapinfo" | python3 -I -c 'import json,sys; c=json.load(sys.stdin)["channel-map"]; print(" ".join(sorted({x["channel"]["name"]+"="+x["version"] for x in c})))' 2>/dev/null)
  if [ -n "$s" ]; then
    case " $s " in *"=$VERSION "*) ok "Snap Store: $s" ;; *) attn "Snap Store: $s, repo $VERSION" ;; esac
    sc=$(printf '%s' "$snapinfo" | python3 -I -c 'import json,sys; print((json.load(sys.stdin)["snap"].get("links",{}).get("contact") or [""])[0])' 2>/dev/null)
    if [ "$sc" = "https://electricrv.ca/megapdf/support/" ]; then ok "Snap Store contact link: the support page (#662)"
    else attn "Snap Store contact link is '${sc:-unreadable}', not the support page (#662; the next snap upload carries it)"; fi
  else attn "Snap Store: could not read"; fi
  rel=$(gh release list --limit 40 --json tagName,isDraft -q '.[]|select(.tagName|test("'"$VERSION"'"))|.tagName+(if .isDraft then " (DRAFT)" else "" end)' 2>/dev/null | tr '\n' ' ')
  case "$rel" in *DRAFT*) attn "GitHub releases for $VERSION: $rel" ;; "") attn "GitHub releases for $VERSION: none" ;; *) ok "GitHub releases for $VERSION: $rel" ;; esac
  if [ -x tools/msstore.sh ]; then
    m=$(timeout 90 tools/msstore.sh status 2>&1 | tail -4 | tr -s ' \n' ' ')
    ok "Microsoft Store (tools/msstore.sh status; compare by eye): $m"
  fi
fi

echo
if [ "$ATTN" -gt 0 ]; then echo "release-readiness: $ATTN item(s) need attention"; exit 1; fi
echo "release-readiness: all clear"
