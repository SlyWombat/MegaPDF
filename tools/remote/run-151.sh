#!/bin/bash
# Starts (or restarts) the unattended #151 pipeline on this Linux host, detached, in its
# own container. Run it on kdocker2 from a copy of tools/remote/:
#
#   scp tools/remote/{Dockerfile,pipeline-151.sh,run-151.sh} kdocker2:megapdf/image/
#   ssh kdocker2 'bash ~/megapdf/image/run-151.sh'
#
# State lives in ~/megapdf/state (markers/, vars/, pipeline.log, STATUS, battery/).
# The container restarts with the Docker daemon and resumes from its markers.
# Watch:  ssh kdocker2 'cat ~/megapdf/state/STATUS; tail ~/megapdf/state/pipeline.log'
# Resume after FAILED: fix the cause, rm ~/megapdf/state/FAILED, docker restart megapdf-151.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
STATE_HOST=${STATE_HOST:-$HOME/megapdf/state}
CORPUS_HOST=${CORPUS_HOST:-/data/pdf-test}
GH_HOSTS=${GH_HOSTS:-$HOME/.config/gh/hosts.yml}

mkdir -p "$STATE_HOST/home"
cp "$HERE/pipeline-151.sh" "$STATE_HOST/pipeline-151.sh"
nice -n 10 docker build -q -t megapdf-toolbox:151 -f "$HERE/Dockerfile" "$HERE" >/dev/null

docker rm -f megapdf-151 >/dev/null 2>&1 || true
# Shared house server: leave room for the other containers.
docker run -d --name megapdf-151 --restart unless-stopped \
    --cpus 24 --memory 40g \
    --user "$(id -u):$(id -g)" \
    -e HOME=/state/home -e STATE=/state -e CORPUS=/corpus -e GH_CONFIG_DIR=/gh \
    -v "$STATE_HOST:/state" \
    -v "$CORPUS_HOST:/corpus:ro" \
    -v "$GH_HOSTS:/gh/hosts.yml:ro" \
    megapdf-toolbox:151 nice -n 10 bash /state/pipeline-151.sh
