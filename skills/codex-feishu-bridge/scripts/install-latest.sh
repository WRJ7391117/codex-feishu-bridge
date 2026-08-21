#!/bin/zsh

set -euo pipefail

release_url="https://github.com/WRJ7391117/codex-feishu-bridge/releases/latest/download/Codex-Feishu-Bridge-macOS-universal.zip"
temporary_dir="$(/usr/bin/mktemp -d -t codex-feishu-bridge)"
archive="${temporary_dir}/bridge.zip"
trap '/bin/rm -rf "${temporary_dir}"' EXIT

/usr/bin/curl -fL "${release_url}" -o "${archive}"
/usr/bin/ditto -x -k "${archive}" "${temporary_dir}/unpacked"
source_app="${temporary_dir}/unpacked/Codex 飞书桥接.app"

if [[ ! -d "${source_app}" ]]; then
    print -u2 "发布包中没有找到 Codex 飞书桥接.app"
    exit 1
fi

if [[ -w /Applications ]]; then
    destination="/Applications/Codex 飞书桥接.app"
else
    /bin/mkdir -p "${HOME}/Applications"
    destination="${HOME}/Applications/Codex 飞书桥接.app"
fi

/usr/bin/ditto "${source_app}" "${destination}"
/usr/bin/open "${destination}"
print "${destination}"
