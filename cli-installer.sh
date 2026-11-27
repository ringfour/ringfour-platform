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
cleanup() {
  rm -rf "$tmp"
}
trap cleanup EXIT
curl -fsSL "$assets/SHA256SUMS" -o "$tmp/SHA256SUMS"
wheel=$(awk '$2 ~ /^ringfour_cli-[[:alnum:].+_]+-py3-none-any\.whl$/ {print $2}' "$tmp/SHA256SUMS")
case "$wheel" in
  ''|*[!a-zA-Z0-9._+-]*) echo "Expected one CLI wheel in release $tag" >&2; exit 1 ;;
esac
curl -fsSL "$assets/$wheel" -o "$tmp/$wheel"
check_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -c "$1"
  else
    shasum -a 256 -c "$1"
  fi
}
(
  cd "$tmp"
  awk -v asset="$wheel" '$2 == asset {print; count++} END {exit count != 1}' SHA256SUMS > asset.sha256
  check_sha256 asset.sha256
)

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
