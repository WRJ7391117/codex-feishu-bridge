#!/bin/zsh

set -euo pipefail

project_dir="${0:A:h:h}"
dist_dir="${project_dir}/dist"
tag="${1:-}"
archive_name="Codex-Feishu-Bridge-macOS-universal.zip"
archive="${dist_dir}/${archive_name}"
tools_dir="${project_dir}/.build/artifacts/sparkle/Sparkle/bin"
generator="${tools_dir}/generate_appcast"
work_dir="${dist_dir}/appcast-work"

if [[ -z "${tag}" || "${tag}" != v* ]]; then
    print -u2 "usage: generate-appcast.sh vX.Y.Z"
    exit 2
fi
if [[ ! -f "${archive}" || ! -x "${generator}" ]]; then
    print -u2 "build the release archive and resolve Sparkle before generating appcast.xml"
    exit 2
fi

/bin/rm -rf "${work_dir}"
/bin/mkdir -p "${work_dir}"
/bin/cp "${archive}" "${work_dir}/${archive_name}"
/bin/cp "${project_dir}/RELEASE_NOTES.md" \
    "${work_dir}/Codex-Feishu-Bridge-macOS-universal.md"

"${generator}" \
    --ed-key-file - \
    --download-url-prefix \
    "https://github.com/WRJ7391117/codex-feishu-bridge/releases/download/${tag}/" \
    --embed-release-notes \
    --maximum-versions 1 \
    --maximum-deltas 0 \
    -o "${work_dir}/appcast.xml" \
    "${work_dir}"

/bin/cp "${work_dir}/appcast.xml" "${dist_dir}/appcast.xml"

read -r signature version build url length <<< "$(/usr/bin/python3 - \
    "${dist_dir}/appcast.xml" "${project_dir}/Resources/Info.plist" <<'PY'
import os
import plistlib
import sys
import xml.etree.ElementTree as ET

appcast_path, plist_path = sys.argv[1:]
sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle"
root = ET.parse(appcast_path).getroot()
items = root.findall("./channel/item")
if len(items) != 1:
    raise SystemExit("appcast must contain exactly one update")
enclosure = items[0].find("enclosure")
if enclosure is None:
    raise SystemExit("appcast update is missing an enclosure")
with open(plist_path, "rb") as handle:
    info = plistlib.load(handle)
signature = enclosure.attrib.get(f"{{{sparkle}}}edSignature", "")
version_node = items[0].find(f"{{{sparkle}}}shortVersionString")
build_node = items[0].find(f"{{{sparkle}}}version")
version = "" if version_node is None else (version_node.text or "")
build = "" if build_node is None else (build_node.text or "")
url = enclosure.attrib.get("url", "")
length = enclosure.attrib.get("length", "")
if not signature:
    raise SystemExit("appcast enclosure is not signed")
if version != str(info["CFBundleShortVersionString"]):
    raise SystemExit("appcast short version does not match Info.plist")
if build != str(info["CFBundleVersion"]):
    raise SystemExit("appcast build does not match Info.plist")
if not length.isdigit():
    raise SystemExit("appcast enclosure length is invalid")
print(signature, version, build, url, length)
PY
)"

expected_url="https://github.com/WRJ7391117/codex-feishu-bridge/releases/download/${tag}/${archive_name}"
[[ "${url}" == "${expected_url}" ]]
[[ "${length}" == "$(/usr/bin/stat -f %z "${archive}")" ]]

public_key="$(/usr/libexec/PlistBuddy -c 'Print :SUPublicEDKey' \
    "${project_dir}/Resources/Info.plist")"
SPARKLE_PUBLIC_KEY="${public_key}" SPARKLE_SIGNATURE="${signature}" \
    /usr/bin/swift -e '
        import CryptoKit
        import Foundation
        let environment = ProcessInfo.processInfo.environment
        let key = try! Curve25519.Signing.PublicKey(
            rawRepresentation: Data(base64Encoded: environment["SPARKLE_PUBLIC_KEY"]!)!
        )
        let signature = Data(base64Encoded: environment["SPARKLE_SIGNATURE"]!)!
        let archive = try! Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1]))
        guard key.isValidSignature(signature, for: archive) else { exit(1) }
    ' "${archive}"

print "${dist_dir}/appcast.xml"
