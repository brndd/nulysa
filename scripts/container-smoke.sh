#!/usr/bin/env bash
set -euo pipefail

# All names and the database are created by this run; never point this at production.
engine=${CONTAINER_ENGINE:-docker}
pg=${NULYSA_TEST_POSTGRES_CONTAINER:-$($engine compose ps -q postgres)}
test -n "$pg"
name="nulysa-smoke-$$"
database="nulysa_container_$$"
mkdir -p "${TMPDIR:-/tmp/opencode}"
directory=$(mktemp -d "${TMPDIR:-/tmp/opencode}/nulysa-container-XXXXXX")
port=${NULYSA_SMOKE_PORT:-58081}
cleanup() {
    $engine rm --force "$name-web" "$name-worker" > /dev/null 2>&1 || true
    $engine volume rm "$name-keys" > /dev/null 2>&1 || true
    $engine exec "$pg" psql -U nulysa postgres -c "DROP DATABASE IF EXISTS $database WITH (FORCE)" > /dev/null
    rm -f "$directory/protection.key" "$directory/protection.crt" "$directory/protection.pfx"
    rmdir "$directory"
}
trap cleanup EXIT

$engine build -t nulysa:s0 .
$engine exec "$pg" psql -U nulysa postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $database"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$directory/protection.key" -out "$directory/protection.crt" -days 1 -subj /CN=NulysaSynthetic > /dev/null 2>&1
openssl pkcs12 -export -inkey "$directory/protection.key" -in "$directory/protection.crt" -out "$directory/protection.pfx" -passout pass:synthetic-certificate-password
chmod 644 "$directory/protection.pfx"

options=(--network host
    -e DOTNET_ENVIRONMENT=Production
    -e "ConnectionStrings__Database=Host=127.0.0.1;Port=55432;Database=$database;Username=nulysa;Password=synthetic-local-password;GSS Encryption Mode=Disable"
    -e "ASPNETCORE_URLS=http://127.0.0.1:$port"
    -e AllowedHosts=127.0.0.1 -e ReverseProxy__KnownProxy=127.0.0.1
    -e DataProtection__CertificatePath=/run/nulysa.pfx
    -e DataProtection__CertificatePassword=synthetic-certificate-password
    -v "$directory/protection.pfx:/run/nulysa.pfx:ro,z" -v "$name-keys:/keys")
$engine run --rm "${options[@]}" nulysa:s0 migrate
$engine run --rm "${options[@]}" nulysa:s0 install-schedules
$engine run --detach --name "$name-web" "${options[@]}" nulysa:s0 web > /dev/null
ready=false
for attempt in $(seq 1 60); do
    if curl --fail --silent -H 'X-Forwarded-Proto: https' "http://127.0.0.1:$port/health/live" > /dev/null; then ready=true; break; fi
    sleep 1
done
test "$ready" = true
curl --fail --silent -H 'X-Forwarded-Proto: https' "http://127.0.0.1:$port/Account/Login" > /dev/null
$engine exec "$name-web" sh -c 'grep -q encryptedSecret /keys/*.xml'
# The web image must initialize the store without acquiring/executing scheduled jobs.
test "$($engine exec "$pg" psql -U nulysa "$database" -Atc 'SELECT count(*) FROM "Observations"')" = 0
$engine exec "$pg" psql -U nulysa "$database" -v ON_ERROR_STOP=1 -c "UPDATE qrtz_triggers SET next_fire_time = (extract(epoch from now() - interval '2 days') * 10000000)::bigint + 621355968000000000 WHERE trigger_name = 'authentication-cleanup'" > /dev/null
$engine run --detach --name "$name-worker" "${options[@]}" nulysa:s0 worker > /dev/null
ready=false
for attempt in $(seq 1 60); do
    if test "$($engine exec "$pg" psql -U nulysa "$database" -Atc 'SELECT count(*) FROM "Observations"')" = 2; then ready=true; break; fi
    sleep 1
done
test "$ready" = true
$engine run --rm "${options[@]}" nulysa:s0 status
phase=$($engine exec "$pg" psql -U nulysa "$database" -Atc "SELECT next_fire_time FROM qrtz_triggers WHERE trigger_name = 'authentication-cleanup'")
$engine run --rm "${options[@]}" nulysa:s0 migrate
$engine run --rm "${options[@]}" nulysa:s0 install-schedules
test "$phase" = "$($engine exec "$pg" psql -U nulysa "$database" -Atc "SELECT next_fire_time FROM qrtz_triggers WHERE trigger_name = 'authentication-cleanup'")"
$engine stop --time 25 "$name-worker" > /dev/null
echo 'Container foundation smoke passed.'
