#!/usr/bin/env bash
# Cross-platform release-artifact smoke: status, MCP initialize, proxy bytes, and embedded UI.
set -euo pipefail

artifact_dir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --artifact-dir) artifact_dir="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$artifact_dir" && -d "$artifact_dir" ]] || { echo "--artifact-dir is required" >&2; exit 2; }
exe="$artifact_dir/vessel"
[[ -x "$exe" ]] || exe="$artifact_dir/vessel.exe"
[[ -f "$exe" ]] || { echo "no Vessel executable in $artifact_dir" >&2; exit 1; }

work="$(mktemp -d)"
vessel_pid=""
stub_pid=""
cleanup() {
  [[ -n "$vessel_pid" ]] && kill "$vessel_pid" 2>/dev/null || true
  [[ -n "$stub_pid" ]] && kill "$stub_pid" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

free_port() { node -e 'const n=require("net");const s=n.createServer();s.listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close()})'; }
port="$(free_port)"
stub_port="$(free_port)"
config="$work/vessel.json"
cat > "$config" <<EOF
{"listen":"127.0.0.1:${port}","defaultBackend":"stub","backends":{"stub":{"baseUrl":"http://127.0.0.1:${stub_port}","type":"ollama"}}}
EOF

STUB_PORT="$stub_port" node -e 'require("http").createServer((q,r)=>r.end("smoke-ok:"+q.url)).listen(process.env.STUB_PORT,"127.0.0.1")' &
stub_pid="$!"
"$exe" --config "$config" --no-open >"$work/stdout.log" 2>"$work/stderr.log" &
vessel_pid="$!"

base="http://127.0.0.1:${port}"
for _ in $(seq 1 60); do
  if curl --fail --silent "$base/vessel/api/status" >"$work/status.json"; then break; fi
  sleep 0.25
done
[[ -s "$work/status.json" ]] || { cat "$work/stderr.log" >&2; exit 1; }
grep -q '"stub"' "$work/status.json"

mcp_response="$(curl --fail --silent -H 'Accept: application/json, text/event-stream' -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"publish-smoke","version":"1"}}}' \
  "$base/vessel/mcp")"
grep -q '"name":"vessel"' <<<"$mcp_response"
[[ "$(curl --fail --silent "$base/smoke/echo?x=1")" == 'smoke-ok:/smoke/echo?x=1' ]]
ui="$(curl --fail --silent "$base/vessel/")"
grep -q '<title>Vessel</title>' <<<"$ui"
grep -q '/vessel/assets/' <<<"$ui"
echo "Publish smoke passed: $artifact_dir"
