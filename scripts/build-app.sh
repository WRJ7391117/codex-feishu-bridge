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
frameworks_dir="${contents}/Frameworks"
sdk="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"
signing_identity="${CODE_SIGN_IDENTITY:--}"
notary_profile="${NOTARY_PROFILE:-}"
notary_keychain="${NOTARY_KEYCHAIN:-}"

/bin/rm -rf "${build_dir}" "${dist_dir}"
/bin/mkdir -p "${macos_dir}" "${resources_dir}" "${frameworks_dir}" "${dist_dir}"

bundled_lark_cli="${build_dir}/vendor/lark-cli"
"${project_dir}/scripts/build-lark-cli.sh" "${bundled_lark_cli}"

/usr/bin/swift build --package-path "${project_dir}" \
    -c release --arch arm64 --product CodexFeishuBridge
/usr/bin/swift build --package-path "${project_dir}" \
    -c release --arch x86_64 --product CodexFeishuBridge
/usr/bin/lipo -create \
    "${project_dir}/.build/arm64-apple-macosx/release/CodexFeishuBridge" \
    "${project_dir}/.build/x86_64-apple-macosx/release/CodexFeishuBridge" \
    -output "${macos_dir}/CodexFeishuBridge"
sparkle_framework="${project_dir}/.build/artifacts/sparkle/Sparkle/Sparkle.xcframework/macos-arm64_x86_64/Sparkle.framework"
if [[ ! -d "${sparkle_framework}" ]]; then
    print -u2 "Sparkle.framework was not resolved by SwiftPM"
    exit 1
fi
/usr/bin/ditto "${sparkle_framework}" "${frameworks_dir}/Sparkle.framework"

/bin/cp "${project_dir}/Resources/Info.plist" "${contents}/Info.plist"
/bin/cp "${project_dir}/THIRD_PARTY_NOTICES.md" "${resources_dir}/THIRD_PARTY_NOTICES.md"
/bin/cp -R "${project_dir}/Resources/bridge" "${resources_dir}/bridge"
/usr/bin/xcrun clang -Wall -Wextra -Werror -O2 \
    -target arm64-apple-macos13.0 -isysroot "${sdk}" \
    "${project_dir}/Sources/PromLightHelper/main.c" \
    -framework IOKit -framework CoreFoundation \
    -o "${build_dir}/promlight-helper-arm64"
/usr/bin/xcrun clang -Wall -Wextra -Werror -O2 \
    -target x86_64-apple-macos13.0 -isysroot "${sdk}" \
    "${project_dir}/Sources/PromLightHelper/main.c" \
    -framework IOKit -framework CoreFoundation \
    -o "${build_dir}/promlight-helper-x86_64"
/usr/bin/lipo -create \
    "${build_dir}/promlight-helper-arm64" \
    "${build_dir}/promlight-helper-x86_64" \
    -output "${resources_dir}/bridge/promlight-helper"
/usr/bin/lipo "${resources_dir}/bridge/promlight-helper" -verify_arch arm64 x86_64
/bin/mkdir -p "${resources_dir}/CodexSkills"
/bin/cp -R \
    "${project_dir}/skills/deepori-bridge-setup" \
    "${resources_dir}/CodexSkills/deepori-bridge-setup"
/bin/rm -rf "${resources_dir}/bridge/__pycache__"
/bin/rm -f \
    "${resources_dir}/bridge/workflow_notifications.py" \
    "${resources_dir}/bridge/workflow_notify.py" \
    "${resources_dir}/bridge/workflow_config.py"
/usr/bin/install -m 755 "${bundled_lark_cli}" "${resources_dir}/bridge/lark-cli"
/bin/chmod 755 \
    "${resources_dir}/bridge/"*.sh \
    "${resources_dir}/bridge/feishu_codex_bridge.py" \
    "${resources_dir}/bridge/promlight-helper"

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

codesign_args=(--force --sign "${signing_identity}")
if [[ "${signing_identity}" != "-" ]]; then
    codesign_args+=(--options runtime --timestamp)
fi
/usr/bin/codesign "${codesign_args[@]}" "${resources_dir}/bridge/lark-cli"
/usr/bin/codesign "${codesign_args[@]}" "${resources_dir}/bridge/promlight-helper"
sparkle_version="${frameworks_dir}/Sparkle.framework/Versions/B"
for nested_bundle in \
    "${sparkle_version}/XPCServices/Downloader.xpc" \
    "${sparkle_version}/XPCServices/Installer.xpc" \
    "${sparkle_version}/Updater.app"; do
    /usr/bin/codesign "${codesign_args[@]}" \
        --preserve-metadata=entitlements "${nested_bundle}"
done
/usr/bin/codesign "${codesign_args[@]}" "${sparkle_version}/Autoupdate"
/usr/bin/codesign "${codesign_args[@]}" "${frameworks_dir}/Sparkle.framework"
/usr/bin/codesign "${codesign_args[@]}" \
    --identifier "com.deepori.codex-feishu-bridge" "${app_dir}"
/usr/bin/plutil -lint "${contents}/Info.plist" >/dev/null
/usr/bin/codesign --verify --deep --strict "${app_dir}"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent \
    "${app_dir}" "${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip"

if [[ -n "${notary_profile}" ]]; then
    if [[ "${signing_identity}" == "-" ]]; then
        print -u2 "NOTARY_PROFILE requires a Developer ID Application signature"
        exit 1
    fi
    notary_args=(--keychain-profile "${notary_profile}")
    if [[ -n "${notary_keychain}" ]]; then
        notary_args+=(--keychain "${notary_keychain}")
    fi
    /usr/bin/xcrun notarytool submit \
        "${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip" \
        "${notary_args[@]}" --wait
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
