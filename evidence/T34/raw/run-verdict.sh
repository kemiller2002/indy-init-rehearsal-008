#!/usr/bin/env bash
# Exact command for the computed verdict (T34 completion criterion 3). Run from the repository root.
set -u
SP=${INDY_BASELINE_SOURCE:-/home/user/Indy-init}  # a git checkout containing commit 62df28a559c054226dd4022e9744cf1db06e954f
INDY=/home/user/Indy-init
B=$INDY/requirements/causa-requirements-baseline-v4.json
A=$INDY/kickoff/rehearsal-007/approvals.causa.json
M=evidence/T34/evidence-manifest.json
SHA=$(python3 -c "import json;print(json.load(open('evidence/T34/ledger.json'))['source_commit_sha'])")
python3 $INDY/tools/compute-acceptance-verdict.py evidence/T34/ledger.json \
  --repo . \
  --baseline "$B" --baseline-sha256 "$(sha256sum "$B" | cut -d' ' -f1)" --baseline-source "$SP" \
  --approvals "$A" --approvals-sha256 "$(sha256sum "$A" | cut -d' ' -f1)" \
  --evidence-manifest "$M" --evidence-manifest-sha256 "$(sha256sum "$M" | cut -d' ' -f1)" \
  --evidence-root evidence/T34 \
  --trusted-source-sha "$SHA" \
  --trusted-operator-id kemiller2002 \
  ${REPORT:+--report "$REPORT"}
