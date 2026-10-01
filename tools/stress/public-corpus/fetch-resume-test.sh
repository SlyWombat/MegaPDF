#!/usr/bin/env bash
# #612 regression test for fetch.sh's transfer: it must bound a transfer by SPEED rather
# than by wall clock, resume instead of restarting, and keep the hash check mandatory on
# the resumed path.
#
#   tools/stress/public-corpus/fetch-resume-test.sh
#
# Nothing here touches the network or the real manifest: a local HTTP server on 127.0.0.1
# serves one generated document under a handful of deliberately awkward behaviours, and the
# test drives fetch.sh against a one-row manifest pointing at it. Not wired into CI (it
# spends about a minute in sleeps proving the slow-link cases, which is the wrong trade on
# every push) -- run it by hand after changing fetch_one.
#
# Cases, each one a thing that was broken or newly relied on by #612:
#   1  plain       a whole document fetched and verified, nothing clever  (regression guard)
#   2  slow-ok     bytes trickling well under the OLD 300s wall clock but above the new
#                  speed floor -- MUST succeed, and is exactly what `--max-time 300` failed
#   3  stall       a connection that delivers a little and then stops -- MUST be abandoned,
#                  which is the job the wall clock was presumably doing
#   4  cut         a connection dropped mid-document -- MUST be resumed, not restarted, and
#                  the proof is a byte counter on the server: the second request asks for a
#                  Range, and the total bytes served is about one document, not two
#   5  norange     a server that refuses Range -- MUST NOT leave the row unfetchable; the
#                  partial is discarded and the document fetched whole
#   6  badtail     a Range response that serves the wrong bytes -- MUST be caught by the
#                  hash (never accepted), and MUST be re-fetched whole once before the row
#                  is called a mismatch, so #612's resume cannot manufacture a #525
#   7  stale       a complete-but-wrong partial left on disk -- same: hashed, not trusted
set -uo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
FETCH="$HERE/fetch.sh"
[ -x "$FETCH" ] || { echo "not executable: $FETCH" >&2; exit 2; }
command -v python3 >/dev/null || { echo "python3 not found" >&2; exit 2; }

WORK=$(mktemp -d) || exit 2
trap 'kill "${SERVER_PID:-0}" 2>/dev/null; rm -rf "$WORK"' EXIT

# ---- the document under test -----------------------------------------------------
# 3 MB: big enough that a cut in the middle leaves a partial worth resuming and that the
# trickle cases take measurable time, small enough to keep the whole test inside a minute.
python3 - "$WORK/doc.pdf" <<'PY'
import sys, random
random.seed(612)
open(sys.argv[1], 'wb').write(bytes(random.getrandbits(8) for _ in range(3 * 1024 * 1024)))
PY
read -r WANT SIZE < <(python3 - "$WORK/doc.pdf" <<'PY'
import sys, hashlib
d = open(sys.argv[1], 'rb').read()
print(hashlib.sha256(d).hexdigest(), len(d))
PY
)

# ---- the awkward server ----------------------------------------------------------
# One behaviour per URL path, so a single server covers every case and the byte counter it
# keeps is what case 4 reads back to prove resumption actually resumed.
cat > "$WORK/server.py" <<'PY'
import http.server, os, socketserver, sys, threading, time

DOC = sys.argv[1]
DATA = open(DOC, 'rb').read()
served = {}       # case -> bytes actually written to a socket
requests = {}     # case -> requests seen
ranged = {}       # case -> requests that carried a Range header
lock = threading.Lock()


def count(path, n):
    with lock:
        served[path] = served.get(path, 0) + n


class H(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *a):
        pass

    def _range(self):
        # Only the "bytes=N-" form fetch.sh's `-C -` sends; anything else is a full body.
        r = self.headers.get('Range')
        if not r or not r.startswith('bytes='):
            return None
        try:
            return int(r[len('bytes='):].split('-')[0])
        except ValueError:
            return None

    def do_GET(self):
        case = self.path.strip('/').split('?')[0]
        start = self._range()
        with lock:
            requests[case] = requests.get(case, 0) + 1
            nth = requests[case]
            if start is not None:
                ranged[case] = ranged.get(case, 0) + 1
        if case == 'norange':
            # Range ignored on purpose: answer 200 with the whole body, which is what curl
            # refuses to append to (exit 33).
            start = None
        body = DATA if start is None else DATA[start:]
        if case == 'badtail' and start is not None:
            body = bytes((b ^ 0xFF) for b in body)     # right length, wrong bytes
        status = 200 if start is None else 206
        self.send_response(status)
        self.send_header('Content-Type', 'application/pdf')
        self.send_header('Accept-Ranges', 'none' if case == 'norange' else 'bytes')
        if case in ('cut', 'stall'):
            # Promise the whole thing and then do not deliver it, so curl is left waiting on
            # a body that never finishes -- the real failure shape, not a clean short read.
            self.send_header('Content-Length', str(len(body)))
        else:
            self.send_header('Content-Length', str(len(body)))
        if start is not None:
            self.send_header('Content-Range',
                             'bytes %d-%d/%d' % (start, len(DATA) - 1, len(DATA)))
        self.end_headers()

        if case == 'stall':
            # A dead connection: a handful of bytes, then silence forever. The speed floor
            # is what has to notice this; a wall clock would too, but so would it notice a
            # healthy 2 GB transfer.
            self.wfile.write(body[:4096]); self.wfile.flush(); count(case, 4096)
            time.sleep(600)
            return
        if case == 'cut' and nth == 1:
            # Half the document, then the connection goes away -- once. Every later request
            # is answered in full, so the only question the test is asking is whether the
            # second request picks up where the first stopped or starts again from zero.
            half = len(body) // 2
            self.wfile.write(body[:half]); self.wfile.flush(); count(case, half)
            self.close_connection = True
            try:
                self.wfile.close()
            except Exception:
                pass
            return
        if case == 'slow':
            # Slower than any real link but comfortably above the 1 KB/s floor: ~64 KB/s,
            # so 3 MB takes about 48 seconds -- under the new rule this must SUCCEED, and
            # under the old `--max-time 300` a 2 GB document at this rate could not.
            step = 16 * 1024
            for i in range(0, len(body), step):
                self.wfile.write(body[i:i + step]); self.wfile.flush()
                count(case, len(body[i:i + step]))
                time.sleep(0.25)
            return
        self.wfile.write(body); count(case, len(body))

    def do_HEAD(self):
        self.send_response(200)
        self.send_header('Content-Length', str(len(DATA)))
        self.end_headers()


class S(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def handle_error(self, request, client_address):
        # Every interesting case here ends with a socket being torn down mid-body, by this
        # server (cut) or by curl giving up (stall, and the abandoned tail of badtail), so
        # the broken pipes are the test working. Swallowed, or the real results are buried
        # in tracebacks.
        pass


srv = S(('127.0.0.1', 0), H)
port = srv.server_address[1]
open(sys.argv[2], 'w').write(str(port))


def dump(signum=None, frame=None):
    with lock:
        lines = []
        for case in sorted(set(served) | set(requests)):
            lines.append('%s %d %d %d\n' % (case, served.get(case, 0),
                                            requests.get(case, 0), ranged.get(case, 0)))
        open(sys.argv[3], 'w').write(''.join(lines))


import signal
signal.signal(signal.SIGUSR1, dump)
srv.serve_forever()
PY

python3 "$WORK/server.py" "$WORK/doc.pdf" "$WORK/port" "$WORK/served" &
SERVER_PID=$!
for _ in $(seq 1 50); do [ -s "$WORK/port" ] && break; sleep 0.2; done
[ -s "$WORK/port" ] || { echo "server did not start" >&2; exit 2; }
PORT=$(cat "$WORK/port")

manifest() {  # <case> -> a one-row manifest for that URL path
    printf 'url\tsha256\tbytes\tsource\tlicence\tcategory\tpath\n' > "$WORK/m-$1.tsv"
    printf 'http://127.0.0.1:%s/%s\t%s\t%s\ttest\tpublic-domain\tform\tdoc.pdf\n' \
        "$PORT" "$1" "$WANT" "$SIZE" >> "$WORK/m-$1.tsv"
}

fails=0
check() {  # <name> <expected: ok|fail> <extra fetch.sh args...>
    local name=$1 expect=$2; shift 2
    local dest="$WORK/dest-$name"
    rm -rf "$dest"
    manifest "$name"
    local out rc=0
    out=$("$FETCH" "$dest" --manifest "$WORK/m-$name.tsv" --jobs 1 "$@" 2>&1) || rc=$?
    local got=ok; [ "$rc" -ne 0 ] && got=fail
    if [ "$got" != "$expect" ]; then
        fails=$((fails + 1))
        echo "FAIL $name: expected the run to $expect, it did not (exit $rc)"
        printf '%s\n' "$out" | sed 's/^/    | /'
        return 1
    fi
    if [ "$expect" = ok ]; then
        local have; have=$(sha256sum "$dest/doc.pdf" 2>/dev/null | cut -d' ' -f1)
        if [ "$have" != "$WANT" ]; then
            fails=$((fails + 1))
            echo "FAIL $name: the file that landed does not match the manifest hash"
            return 1
        fi
    fi
    echo "pass $name ($expect)"
    printf '%s\n' "$out" > "$WORK/out-$name.txt"
    return 0
}

echo "document: $SIZE bytes, server on 127.0.0.1:$PORT"
echo

# 1 -- the ordinary path still works.
check plain ok

# 2 -- the case the old wall clock got wrong: slow but alive. Also the reason this test is
# not in CI. A 3 MB document at ~64 KB/s is ~48s; `--max-time 300` would pass this one and
# fail the manifest's 2.07 GB row at the same healthy rate, which is #612 in one line.
check slow ok

# 3 -- the case the wall clock got right, which the speed floor must still catch. Bounded
# well under the old 300s so a regression here shows up as a hang, not a long wait.
check stall fail --stall-time 5 --min-speed 4096
if [ -f "$WORK/out-stall.txt" ] && ! grep -q 'FETCH-FAILED' "$WORK/out-stall.txt"; then
    echo "FAIL stall: the run failed, but not with FETCH-FAILED"; fails=$((fails + 1))
fi

# 4 -- resumed, not restarted. The server drops the first connection halfway through and
# answers everything after that in full, so both a resuming and a restarting fetch end up
# with the right document -- and the server's own byte counter is what tells them apart.
# Resuming serves half plus half: one document. Restarting serves half plus a whole one:
# half again as much, and on the manifest's 2.07 GB row that difference is the issue.
check cut ok
kill -USR1 "$SERVER_PID"; sleep 0.5
read -r _ served_cut reqs_cut ranged_cut < <(awk '$1 == "cut"' "$WORK/served" 2>/dev/null)
if [ -n "${served_cut:-}" ]; then
    echo "     cut document: $served_cut bytes served over $reqs_cut request(s), $ranged_cut ranged (document is $SIZE)"
    if [ "${ranged_cut:-0}" -lt 1 ]; then
        echo "FAIL cut: no request carried a Range header -- the retry restarted"
        fails=$((fails + 1))
    fi
    # 1.25x allows the odd re-read a redirect or a retried header could add; a restart
    # costs 1.5x on this fixture and more on a document cut later than halfway.
    if awk -v s="$served_cut" -v n="$SIZE" 'BEGIN{exit !(s > n * 1.25)}'; then
        echo "FAIL cut: served ${served_cut} for a ${SIZE}-byte document -- that is restarting"
        fails=$((fails + 1))
    fi
else
    echo "FAIL cut: the server recorded nothing for this case"; fails=$((fails + 1))
fi

# 5 -- a server that will not serve a Range must not leave the row unfetchable. Seeded with
# a partial by hand, since this server cannot both refuse ranges and cut a connection.
rm -rf "$WORK/dest-norange"; mkdir -p "$WORK/dest-norange"
head -c 1000000 "$WORK/doc.pdf" > "$WORK/dest-norange/doc.pdf.part"
check_norange_seeded=1
manifest norange
if out=$("$FETCH" "$WORK/dest-norange" --manifest "$WORK/m-norange.tsv" --jobs 1 2>&1); then
    have=$(sha256sum "$WORK/dest-norange/doc.pdf" | cut -d' ' -f1)
    if [ "$have" = "$WANT" ] && printf '%s' "$out" | grep -q 'RESUME-REFUSED'; then
        echo "pass norange (ok, after discarding the partial)"
    else
        echo "FAIL norange: completed, but not by the documented route"
        printf '%s\n' "$out" | sed 's/^/    | /'; fails=$((fails + 1))
    fi
else
    echo "FAIL norange: a server that refuses Range left the row unfetchable"
    printf '%s\n' "$out" | sed 's/^/    | /'; fails=$((fails + 1))
fi

# 6 -- the hash check on the resumed path, which is the whole reason resuming is allowed.
# Seeded with a good partial; the server then serves a wrong-but-right-length tail to the
# Range request and the correct body to a plain request, so the ONLY way this row can land
# is: notice the bad tail by hash, throw the partial away, fetch whole, verify.
rm -rf "$WORK/dest-badtail"; mkdir -p "$WORK/dest-badtail"
head -c 1000000 "$WORK/doc.pdf" > "$WORK/dest-badtail/doc.pdf.part"
manifest badtail
if out=$("$FETCH" "$WORK/dest-badtail" --manifest "$WORK/m-badtail.tsv" --jobs 1 2>&1); then
    have=$(sha256sum "$WORK/dest-badtail/doc.pdf" | cut -d' ' -f1)
    if [ "$have" = "$WANT" ] && printf '%s' "$out" | grep -q 'RESUMED-COPY-BAD'; then
        echo "pass badtail (a corrupted resumed tail was caught and re-fetched whole)"
    else
        echo "FAIL badtail: landed, but the corrupted tail was not the thing that was caught"
        printf '%s\n' "$out" | sed 's/^/    | /'; fails=$((fails + 1))
    fi
else
    echo "FAIL badtail: could not recover from a corrupted resumed tail"
    printf '%s\n' "$out" | sed 's/^/    | /'; fails=$((fails + 1))
fi

# 7 -- a stale partial that is already the full length and entirely wrong. Nothing is
# transferred, so the hash is the only thing standing between it and the corpus.
rm -rf "$WORK/dest-stale"; mkdir -p "$WORK/dest-stale"
tr '\000-\377' '\001' < "$WORK/doc.pdf" > "$WORK/dest-stale/doc.pdf.part" 2>/dev/null \
    || python3 -c "import sys; open(sys.argv[1],'wb').write(b'\x01'*int(sys.argv[2]))" \
         "$WORK/dest-stale/doc.pdf.part" "$SIZE"
manifest stale
if out=$("$FETCH" "$WORK/dest-stale" --manifest "$WORK/m-stale.tsv" --jobs 1 2>&1); then
    have=$(sha256sum "$WORK/dest-stale/doc.pdf" | cut -d' ' -f1)
    if [ "$have" = "$WANT" ]; then
        echo "pass stale (a full-length wrong partial was not trusted)"
    else
        echo "FAIL stale: a wrong partial was promoted into the corpus"; fails=$((fails + 1))
    fi
else
    echo "FAIL stale: could not recover from a full-length wrong partial"
    printf '%s\n' "$out" | sed 's/^/    | /'; fails=$((fails + 1))
fi

echo
if [ "$fails" -eq 0 ]; then echo "all cases pass"; exit 0; fi
echo "$fails case(s) failed"; exit 1
