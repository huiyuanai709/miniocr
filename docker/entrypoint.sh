#!/bin/sh
set -eu

if [ -z "${MINIOCR_LLM_API_KEY:-}" ]; then
  echo "miniocr: MINIOCR_LLM_API_KEY is empty; text NER stays off" >&2
fi
if [ -z "${MINIOCR_CLUSTER_TOKEN:-}" ]; then
  echo "miniocr: MINIOCR_CLUSTER_TOKEN is empty; cluster stays off" >&2
fi

port="${MINIOCR_PORT:-5080}"
cd /opt/miniocr
exec /opt/miniocr/MiniOcr --urls "http://0.0.0.0:${port}"
