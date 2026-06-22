#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 '<signing-secret>' '<json-body>'" >&2
  exit 1
fi

secret="$1"
body="$2"
timestamp="$(date +%s)"
base="v0:${timestamp}:${body}"
signature="v0=$(printf "%s" "$base" | openssl dgst -sha256 -hmac "$secret" -hex | awk '{print $2}')"

printf "X-Slack-Request-Timestamp: %s\n" "$timestamp"
printf "X-Slack-Signature: %s\n" "$signature"
