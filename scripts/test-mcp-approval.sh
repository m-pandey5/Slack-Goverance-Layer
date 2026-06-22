#!/usr/bin/env bash
set -euo pipefail

curl -sS \
  -X POST "http://localhost:5000/mcp-proxy" \
  -H "Content-Type: application/json" \
  --data '{"jsonrpc":"2.0","id":"req-1","method":"tools/call","params":{"name":"conversations.archive","arguments":{"channel":"C_TEST"}}}'
