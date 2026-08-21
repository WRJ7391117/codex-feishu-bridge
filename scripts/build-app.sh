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

/bin/rm -rf "${build_dir}" "${dist_dir}"
/bin/mkdir -p "${macos_dir}" "${resources_dir}" "${dist_dir}"

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
/bin/cp -R "${project_dir}/Resources/bridge" "${resources_dir}/bridge"
/bin/chmod 755 "${resources_dir}/bridge/"*.sh "${resources_dir}/bridge/feishu_codex_bridge.py"

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

/usr/bin/codesign --force --deep --sign - \
    --identifier "com.deepori.codex-feishu-bridge" "${app_dir}"
/usr/bin/plutil -lint "${contents}/Info.plist" >/dev/null
/usr/bin/codesign --verify --deep --strict "${app_dir}"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent \
    "${app_dir}" "${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip"

print "${app_dir}"
print "${dist_dir}/Codex-Feishu-Bridge-macOS-universal.zip"
