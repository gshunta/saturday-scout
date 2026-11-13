#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${CONFIGURATION:-Debug}"
api_project="$repo_root/src/SaturdayScout.Api/SaturdayScout.Api.csproj"
api_assembly="$repo_root/src/SaturdayScout.Api/bin/$configuration/net10.0/SaturdayScout.Api.dll"
contract_path="$repo_root/docs/api/openapi-v1.json"

command -v dotnet >/dev/null || { echo "dotnet is required." >&2; exit 1; }
command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 is required." >&2; exit 1; }

port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')"
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/saturday-scout-openapi.XXXXXX")"
api_pid=""

cleanup() {
    if [[ -n "$api_pid" ]]; then
        kill "$api_pid" 2>/dev/null || true
        wait "$api_pid" 2>/dev/null || true
    fi
    rm -rf "$temp_dir"
}
trap cleanup EXIT INT TERM

mkdir -p "$temp_dir/dpkeys" "$(dirname "$contract_path")"

dotnet build "$api_project" --configuration "$configuration" --no-restore -m:1 /nodeReuse:false

# The API only registers OpenAPI in Development. Its startup requires a configured
# connection string but does not open a PostgreSQL connection while serving OpenAPI.
# Supply inert values only to this child process; no credentials or key material are used.
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS="http://127.0.0.1:$port" \
SATURDAY_SCOUT_DB_CONNECTION='Host=127.0.0.1;Port=1;Database=contract_generation;Username=unused;Password=unused' \
SATURDAY_SCOUT_DP_KEYS_PATH="$temp_dir/dpkeys" \
    dotnet "$api_assembly" >"$temp_dir/api.log" 2>&1 &
api_pid=$!

openapi_url="http://127.0.0.1:$port/openapi/v1.json"
openapi_json="$temp_dir/openapi-v1.json"
ready=false
for _ in $(seq 1 80); do
    if curl --fail --silent "$openapi_url" --output "$openapi_json"; then
        ready=true
        break
    fi
    if ! kill -0 "$api_pid" 2>/dev/null; then
        cat "$temp_dir/api.log" >&2
        echo "SaturdayScout.Api exited before serving the OpenAPI document." >&2
        exit 1
    fi
    sleep 0.25
done

if [[ "$ready" != true ]]; then
    cat "$temp_dir/api.log" >&2
    echo "Timed out waiting for the Development OpenAPI endpoint." >&2
    exit 1
fi

python3 - "$openapi_json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    document = json.load(source)

if document.get("openapi", "").split(".", 1)[0] != "3":
    raise SystemExit("Expected an OpenAPI 3 document.")
if document.get("info", {}).get("version") != "1.0.0":
    raise SystemExit("Expected Saturday Scout API contract version 1.0.0.")
paths = document.get("paths", {})
required = {
    "/api/v1/leagues/me",
    "/api/v1/leagues/{leagueId}/slates/active",
    "/api/v1/leagues/{leagueId}/slates/weeks",
    "/api/v1/slates/{slateId}/picks/me",
    "/api/v1/slates/{slateId}/picks",
    "/api/v1/slates/{slateId}/submit",
    "/api/v1/leagues/{leagueId}/leaderboard",
}
missing = sorted(required - paths.keys())
if missing:
    raise SystemExit("Required Player API paths are missing: " + ", ".join(missing))
if any(not path.startswith("/api/v1/") for path in paths):
    raise SystemExit("The generated document contains a path outside /api/v1.")
PY

cp "$openapi_json" "$contract_path"
echo "Generated $contract_path"
