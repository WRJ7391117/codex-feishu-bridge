#!/bin/zsh

set -euo pipefail

project_dir="${0:A:h:h}"
build_dir="${project_dir}/build"
dist_dir="${project_dir}/dist"
app_name="Codex 飞书桥接.app"
app_dir="${build_dir}/${app_name}"
contents="${app_dir}/Contents"
macos_dir="${contents}/MacOS"
resources_dir="${contents}/Resources"
sdk="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"
signing_identity="${CODE_SIGN_IDENTITY:--}"
notary_profile="${NOTARY_PROFILE:-}"

/bin/rm -rf "${build_dir}" "${dist_dir}"
/bin/mkdir -p "${macos_dir}" "${resources_dir}" "${dist_dir}"

bundled_lark_cli="${build_dir}/vendor/lark-cli"
"${project_dir}/scripts/build-lark-cli.sh" "${bundled_lark_cli}"

source_file="${project_dir}/Sources/CodexFeishuBridgeApp/main.swift"
/usr/bin/xcrun swiftc -O -target arm64-apple-macos13.0 -sdk "${sdk}" \
    "${source_file}" -o "${build_dir}/CodexFeishuBridge-arm64"
/usr/bin/xcrun swiftc -O -target x86_64-apple-macos13.0 -sdk "${sdk}" \
    "${source_file}" -o "${build_dir}/CodexFeishuBridge-x86_64"
/usr/bin/lipo -create \
    "${build_dir}/CodexFeishuBridge-arm64" \
    "${build_dir}/CodexFeishuBridge-x86_64" \
    -output "${macos_dir}/CodexFeishuBridge"

/bin/cp "${project_dir}/Resources/Info.plist" "${contents}/Info.plist"
/bin/cp "${project_dir}/THIRD_PARTY_NOTICES.md" "${resources_dir}/THIRD_PARTY_NOTICES.md"
/bin/cp -R "${project_dir}/Resources/bridge" "${resources_dir}/bridge"
/bin/rm -rf "${resources_dir}/bridge/__pycache__"
/usr/bin/install -m 755 "${bundled_lark_cli}" "${resources_dir}/bridge/lark-cli"
/bin/chmod 755 \
    "${resources_dir}/bridge/"*.sh \
    "${resources_dir}/bridge/feishu_codex_bridge.py" \
    "${resources_dir}/bridge/workflow_config.py" \
    "${resources_dir}/bridge/workflow_notify.py"

icon_source="${project_dir}/Resources/AppIcon.svg"
icon_png="${build_dir}/AppIcon.png"
/usr/bin/qlmanage -t -s 1024 -o "${build_dir}" "${icon_source}" >/dev/null 2>&1
/bin/mv "${build_dir}/AppIcon.svg.png" "${icon_png}"
iconset="${build_dir}/AppIcon.iconset"
/bin/mkdir -p "${iconset}"
for size in 16 32 128 256 512; do
    /usr/bin/sips -z "${size}" "${size}" "${icon_png}" \
        --out "${iconset}/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    /usr/bin/sips -z "${double}" "${double}" "${icon_png}" \
        --out "${iconset}/icon_${size}x${size}@2x.png" >/dev/null
done
/usr/bin/iconutil -c icns "${iconset}" -o "${resources_dir}/AppIcon.icns"

codesign_args=(--force --deep --sign "${signing_identity}" \
    --identifier "com.deepori.codex-feishu-bridge")
if [[ "${signing_identity}" != "-" ]]; then
    codesign_args+=(--options runtime --timestamp)
fi
/usr/bin/codesign "${codesign_args[@]}" "${app_dir}"
/usr/bin/plutil -lint "${contents}/Info.plist" >/dev/null
/usr/bin/codesign --verify --deep --strict "${app_dir}"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent \
    "${app_dir}" "${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip"

if [[ -n "${notary_profile}" ]]; then
    if [[ "${signing_identity}" == "-" ]]; then
        print -u2 "NOTARY_PROFILE requires a Developer ID Application signature"
        exit 1
    fi
    /usr/bin/xcrun notarytool submit \
        "${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip" \
        --keychain-profile "${notary_profile}" --wait
    /usr/bin/xcrun stapler staple "${app_dir}"
    /usr/bin/codesign --verify --deep --strict "${app_dir}"
    /bin/rm -f "${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip"
    /usr/bin/ditto -c -k --sequesterRsrc --keepParent \
        "${app_dir}" "${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip"
fi

archive="${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip"
archive_sha256="$(/usr/bin/shasum -a 256 "${archive}" | /usr/bin/awk '{print $1}')"
print "${archive_sha256}  Codex-Feishu-Bridge-macOS-universal.zip" \
    > "${archive}.sha256"
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${contents}/Info.plist")"
build="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "${contents}/Info.plist")"
/usr/bin/python3 - "${dist_dir}/update.json" "${version}" "${build}" "${archive_sha256}" <<'PY'
import json
from pathlib import Path
import sys

destination = Path(sys.argv[1])
version, build, sha256 = sys.argv[2:]
payload = {
    "version": version,
    "build": build,
    "asset": "Codex-Feishu-Bridge-macOS-universal.zip",
    "sha256": sha256,
    "url": (
        "https://github.com/WRJ7391117/codex-feishu-bridge/releases/download/"
        f"v{version}/Codex-Feishu-Bridge-macOS-universal.zip"
    ),
}
destination.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
PY

print "${app_dir}"
print "${archive}"
