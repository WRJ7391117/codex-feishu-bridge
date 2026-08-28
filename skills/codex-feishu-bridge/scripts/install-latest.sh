#!/bin/zsh

set -euo pipefail

repository="WRJ7391117/codex-feishu-bridge"
asset_name="Codex-Feishu-Bridge-macOS-universal.zip"
expected_bundle_id="com.deepori.codex-feishu-bridge"
temporary_dir="$(/usr/bin/mktemp -d -t codex-feishu-bridge)"
release_json="${temporary_dir}/release.json"
metadata="${temporary_dir}/metadata.txt"
archive="${temporary_dir}/bridge.zip"
trap '/bin/rm -rf "${temporary_dir}"' EXIT

/usr/bin/curl -fsSL \
    -H "Accept: application/vnd.github+json" \
    "https://api.github.com/repos/${repository}/releases/latest" \
    -o "${release_json}"

/usr/bin/python3 - "${release_json}" "${asset_name}" >"${metadata}" <<'PY'
import json
from pathlib import Path
import sys

release = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
asset = next(
    (item for item in release.get("assets", []) if item.get("name") == sys.argv[2]),
    None,
)
tag = str(release.get("tag_name") or "")
digest = str((asset or {}).get("digest") or "")
url = str((asset or {}).get("browser_download_url") or "")
if not tag.startswith("v") or not digest.startswith("sha256:") or not url.startswith("https://github.com/"):
    raise SystemExit("latest release metadata is incomplete")
print(tag[1:])
print(digest.split(":", 1)[1])
print(url)
PY

release_version="$(/usr/bin/sed -n '1p' "${metadata}")"
expected_sha256="$(/usr/bin/sed -n '2p' "${metadata}")"
release_url="$(/usr/bin/sed -n '3p' "${metadata}")"

/usr/bin/curl -fL "${release_url}" -o "${archive}"
actual_sha256="$(/usr/bin/shasum -a 256 "${archive}" | /usr/bin/awk '{print $1}')"
if [[ "${actual_sha256}" != "${expected_sha256}" ]]; then
    print -u2 "下载包 SHA-256 校验失败"
    exit 1
fi

/usr/bin/ditto -x -k "${archive}" "${temporary_dir}/unpacked"
source_app="${temporary_dir}/unpacked/Codex 飞书桥接.app"
source_info="${source_app}/Contents/Info.plist"
source_binary="${source_app}/Contents/MacOS/CodexFeishuBridge"

if [[ ! -d "${source_app}" || ! -f "${source_info}" || ! -f "${source_binary}" ]]; then
    print -u2 "发布包中没有完整的 Codex 飞书桥接.app"
    exit 1
fi

bundle_id="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${source_info}")"
bundle_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${source_info}")"
bundle_build="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "${source_info}")"
if [[ "${bundle_id}" != "${expected_bundle_id}" || "${bundle_version}" != "${release_version}" ]]; then
    print -u2 "发布包身份或版本与 GitHub Release 不一致"
    exit 1
fi

architectures="$(/usr/bin/lipo -archs "${source_binary}")"
if [[ " ${architectures} " != *" arm64 "* || " ${architectures} " != *" x86_64 "* ]]; then
    print -u2 "发布包不是 arm64 + x86_64 Universal App"
    exit 1
fi
/usr/bin/codesign --verify --deep --strict "${source_app}"

if [[ -w /Applications ]]; then
    destination="/Applications/Codex 飞书桥接.app"
else
    /bin/mkdir -p "${HOME}/Applications"
    destination="${HOME}/Applications/Codex 飞书桥接.app"
fi

if [[ -L "${destination}" ]]; then
    print -u2 "安装目标是符号链接，已拒绝覆盖"
    exit 1
fi
if [[ -d "${destination}" ]]; then
    installed_info="${destination}/Contents/Info.plist"
    installed_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${installed_info}" 2>/dev/null || print '0.0.0')"
    installed_build="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "${installed_info}" 2>/dev/null || print '0')"
    /usr/bin/python3 - "${installed_version}" "${installed_build}" "${bundle_version}" "${bundle_build}" <<'PY'
import sys

def version(value):
    parts = value.split(".")
    if len(parts) != 3 or any(not part.isdigit() for part in parts):
        raise SystemExit("installed App version is invalid")
    return tuple(map(int, parts))

installed = version(sys.argv[1])
candidate = version(sys.argv[3])
installed_build = int(sys.argv[2])
candidate_build = int(sys.argv[4])
if installed > candidate or (installed == candidate and installed_build > candidate_build):
    raise SystemExit("refusing to downgrade the installed App")
PY
fi

incoming="${destination}.incoming"
previous="${destination}.previous"
if [[ -e "${incoming}" || -e "${previous}" ]]; then
    print -u2 "安装目录存在未完成的旧更新，请先在 Finder 中检查 .incoming/.previous 备份"
    exit 1
fi
/usr/bin/ditto "${source_app}" "${incoming}"
/usr/bin/codesign --verify --deep --strict "${incoming}"
if [[ -e "${destination}" ]]; then
    /bin/mv "${destination}" "${previous}"
fi
if ! /bin/mv "${incoming}" "${destination}"; then
    if [[ -e "${previous}" ]]; then
        /bin/mv "${previous}" "${destination}"
    fi
    print -u2 "安装失败，旧版本已恢复"
    exit 1
fi
if ! /usr/bin/open "${destination}"; then
    /bin/rm -rf "${destination}"
    if [[ -e "${previous}" ]]; then
        /bin/mv "${previous}" "${destination}"
        /usr/bin/open "${destination}" || true
    fi
    print -u2 "新版本无法打开，旧版本已恢复"
    exit 1
fi
if [[ -e "${previous}" ]]; then
    /bin/rm -rf "${previous}"
fi
print "${destination}"
