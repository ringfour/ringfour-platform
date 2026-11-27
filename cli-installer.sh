#!/bin/sh
set -eu

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64|Linux-aarch64|Darwin-x86_64|Darwin-arm64) ;;
  *) echo "Unsupported platform. Use Linux or macOS." >&2; exit 1 ;;
esac

repo=https://github.com/ringfour/ringfour-platform
release_url=$(curl -fsSL -o /dev/null -w '%{url_effective}' "$repo/releases/latest")
tag=${release_url##*/}
printf '%s\n' "$tag" | grep -Eq '^main-[0-9]+-[0-9]+$' || {
  echo "No Ringfour main release found" >&2
  exit 1
}
assets="$repo/releases/download/$tag"

tmp=$(mktemp -d)
stage=
lock=
cleanup() {
  if [ -n "$lock" ]; then rmdir "$lock"; fi
  if [ -n "$stage" ]; then rm -rf "$stage"; fi
  rm -rf "$tmp"
}
trap cleanup EXIT
curl -fsSL "$assets/SHA256SUMS" -o "$tmp/SHA256SUMS"
wheel=$(awk '$2 ~ /^ringfour_cli-[[:alnum:].+_]+-py3-none-any\.whl$/ {print $2}' "$tmp/SHA256SUMS")
case "$wheel" in
  ''|*[!a-zA-Z0-9._+-]*) echo "Expected one CLI wheel in release $tag" >&2; exit 1 ;;
esac
curl -fsSL "$assets/$wheel" -o "$tmp/$wheel"
bundle=python-artifacts.tar.gz
curl -fsSL "$assets/$bundle" -o "$tmp/$bundle"
check_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -c "$1"
  else
    shasum -a 256 -c "$1"
  fi
}
(
  cd "$tmp"
  for asset in "$wheel" "$bundle"; do
    awk -v asset="$asset" '$2 == asset {print; count++} END {exit count != 1}' SHA256SUMS > asset.sha256
    check_sha256 asset.sha256
  done
)

bundle_root="${XDG_DATA_HOME-$HOME/.local/share}/ringfour/python-artifacts"
mkdir -p "$bundle_root"
stage=$(mktemp -d "$bundle_root/.install.XXXXXX")
tar -xzf "$tmp/$bundle" -C "$stage"
set -- "$stage"/*
if [ "$#" -ne 1 ] || [ ! -f "$1/SHA256SUMS" ]; then
  echo "Invalid App wheel bundle in release $tag" >&2
  exit 1
fi
version=${1##*/}
destination="$bundle_root/$version"
(cd "$1" && check_sha256 SHA256SUMS >/dev/null)
lock_path="$bundle_root/.install.lock"
mkdir "$lock_path" || { echo "Another App wheel bundle installation is in progress." >&2; exit 1; }
lock="$lock_path"
if [ -e "$destination" ] || [ -L "$destination" ]; then
  cmp -s "$1/SHA256SUMS" "$destination/SHA256SUMS" || {
    echo "A different App wheel bundle already exists at $destination" >&2
    exit 1
  }
  (cd "$destination" && check_sha256 SHA256SUMS >/dev/null)
else
  mv "$1" "$destination"
fi
rmdir "$lock"
lock=

uv_bin=$(command -v uv || true)
if [ -z "$uv_bin" ]; then
  uv_bin="$HOME/.local/bin/uv"
  if [ ! -x "$uv_bin" ]; then
    curl -fsSL https://astral.sh/uv/0.12.1/install.sh -o "$tmp/install-uv.sh"
    UV_INSTALL_DIR="$HOME/.local/bin" UV_NO_MODIFY_PATH=1 sh "$tmp/install-uv.sh"
  fi
fi
"$uv_bin" tool install --force --python 3.13 "$tmp/$wheel"
"$uv_bin" tool update-shell
tool_dir=$("$uv_bin" tool dir --bin)
case ":$PATH:" in
  *":$tool_dir:"*) printf 'Run ringfour-cli login\n' ;;
  *) printf 'Open a new shell and run ringfour-cli login, or run "%s/ringfour-cli" login now.\n' "$tool_dir" ;;
esac
