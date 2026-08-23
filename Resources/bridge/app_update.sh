#!/bin/zsh

set -euo pipefail

staged_app="${1:-}"
destination="${2:-}"
running_pid="${3:-}"
expected_version="${4:-}"
log_dir="${HOME}/.codex/log"
log_file="${log_dir}/feishu-bridge-app-update.log"

/bin/mkdir -p "${log_dir}"
/bin/chmod 700 "${log_dir}"
exec >>"${log_file}" 2>&1
/bin/chmod 600 "${log_file}"
print "$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ') update helper started version=${expected_version:-unknown}"

allowed_system="/Applications/Codex 飞书桥接.app"
allowed_user="${HOME}/Applications/Codex 飞书桥接.app"
if [[ "${destination}" != "${allowed_system}" && "${destination}" != "${allowed_user}" ]]; then
    print "refused destination outside Applications"
    exit 2
fi
if [[ ! -d "${staged_app}" || "${running_pid}" != <-> || -z "${expected_version}" ]]; then
    print "refused invalid update arguments"
    exit 2
fi
if [[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${staged_app}/Contents/Info.plist")" != "com.deepori.codex-feishu-bridge" ]]; then
    exit 2
fi
if [[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${staged_app}/Contents/Info.plist")" != "${expected_version}" ]]; then
    exit 2
fi
/usr/bin/codesign --verify --deep --strict "${staged_app}"
/usr/bin/lipo -verify_arch arm64 x86_64 "${staged_app}/Contents/MacOS/CodexFeishuBridge"

for _attempt in {1..300}; do
    if ! /bin/kill -0 "${running_pid}" 2>/dev/null; then
        break
    fi
    /bin/sleep 0.1
done
if /bin/kill -0 "${running_pid}" 2>/dev/null; then
    print "old app did not exit before timeout"
    exit 3
fi

replacement="${destination}.incoming"
previous="${destination}.previous"
if [[ -e "${replacement}" ]]; then
    /bin/rm -rf "${replacement}"
fi
if [[ -e "${previous}" ]]; then
    /bin/rm -rf "${previous}"
fi
/usr/bin/ditto "${staged_app}" "${replacement}"
/usr/bin/codesign --verify --deep --strict "${replacement}"

if [[ -e "${destination}" ]]; then
    /bin/mv "${destination}" "${previous}"
fi
if ! /bin/mv "${replacement}" "${destination}"; then
    if [[ -e "${previous}" ]]; then
        /bin/mv "${previous}" "${destination}"
    fi
    print "replacement failed; previous app restored"
    exit 4
fi

if ! /usr/bin/open "${destination}"; then
    /bin/rm -rf "${destination}"
    if [[ -e "${previous}" ]]; then
        /bin/mv "${previous}" "${destination}"
        /usr/bin/open "${destination}" || true
    fi
    print "new app failed to open; previous app restored"
    exit 5
fi
/bin/sleep 2
if [[ -e "${previous}" ]]; then
    /bin/rm -rf "${previous}"
fi
/bin/rm -rf "${staged_app:h:h}"
print "update completed"
