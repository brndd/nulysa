#!/usr/bin/env bash
set -euo pipefail
# Pin the renderer and its embedded fonts in host/CI/container profiles.
case "$(uname -m)" in
    x86_64) target=x86_64-unknown-linux-musl; checksum=59b207df01be2dab9f13e80f73d04d7ff8273ffd46b3dd1b9eef5c60f3eeabea ;;
    aarch64) target=aarch64-unknown-linux-musl; checksum=cdf50ffc7b8ba759ed02200632eda3d78eb8b99aacb6611f4f75684990647620 ;;
    *) echo 'Unsupported Typst architecture' >&2; exit 1 ;;
esac
destination=${1:-"$HOME/.local/bin"}
temporary=$(mktemp -d "${TMPDIR:-/tmp}/nulysa-typst.XXXXXXXX")
trap 'rm -rf "$temporary"' EXIT
curl --fail --location --retry 2 "https://github.com/typst/typst/releases/download/v0.15.0/typst-$target.tar.xz" --output "$temporary/typst.tar.xz"
echo "$checksum  $temporary/typst.tar.xz" | sha256sum --check --status
tar -xJf "$temporary/typst.tar.xz" -C "$temporary"
install -d "$destination"
install -m 755 "$temporary/typst-$target/typst" "$destination/typst"
"$destination/typst" --version
