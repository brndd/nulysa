#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
engine=${CONTAINER_ENGINE:-docker}
# Always use the local profile, never an inherited production connection.
export DOTNET_ENVIRONMENT=Development
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Database='Host=127.0.0.1;Port=55432;Database=nulysa;Username=nulysa;Password=synthetic-local-password;GSS Encryption Mode=Disable'
export DataProtection__Path="$root/.local/data-protection"
export ASPNETCORE_URLS=http://127.0.0.1:5000

cd "$root"
"$engine" compose -f "$root/compose.yaml" up -d postgres
ready=false
for attempt in $(seq 1 60); do
    if "$engine" compose -f "$root/compose.yaml" exec -T postgres pg_isready -U nulysa -d nulysa > /dev/null 2>&1; then ready=true; break; fi
    sleep 1
done
if ! "$ready"; then echo 'Local PostgreSQL did not become ready within 60 seconds.' >&2; exit 1; fi

dotnet tool restore
dotnet restore src/Nulysa/Nulysa.csproj --locked-mode
dotnet build src/Nulysa/Nulysa.csproj --no-restore
app="$root/src/Nulysa/bin/Debug/net10.0/Nulysa.dll"
# Both hosts load the same application settings as well as the environment above.
cd "$root/src/Nulysa"
dotnet "$app" migrate
dotnet "$app" install-schedules
if (( $# )); then exec dotnet "$app" "$@"; fi

pids=()
cleanup() {
    if (( ${#pids[@]} )); then
        kill -TERM "${pids[@]}" 2>/dev/null || true
        wait "${pids[@]}" 2>/dev/null || true
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
dotnet "$app" web &
pids+=("$!")
dotnet "$app" worker &
pids+=("$!")
echo 'Development: http://127.0.0.1:5000 — Ctrl+C stops web/worker; PostgreSQL and its data are kept.'
if wait -n "${pids[@]}"; then
    echo 'A development process exited unexpectedly.' >&2
    exit 1
else
    exit $?
fi
