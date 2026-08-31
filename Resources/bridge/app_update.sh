#!/bin/zsh

set -euo pipefail

staged_app="${1:-}"
destination="${2:-}"
running_pid="${3:-}"
expected_version="${4:-}"
log_dir="${HOME}/.codex/log"
log_file="${log_dir}/feishu-bridge-app-update.log"
lock_file="${log_dir}/feishu-bridge-app-update.lock"
label="com.deepori.codex-feishu-bridge"
domain="gui/$(/usr/bin/id -u)"
runtime_status="${HOME}/.codex/feishu-bridge/runtime-status.json"
lock_acquired=0

/bin/mkdir -p "${log_dir}"
/bin/chmod 700 "${log_dir}"
exec >>"${log_file}" 2>&1
/bin/chmod 600 "${log_file}"
print "$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ') update helper started version=${expected_version:-unknown}"

cleanup() {
    if (( lock_acquired )); then
        /bin/rm -f "${lock_file}"
    fi
}
trap cleanup EXIT HUP INT TERM

bundle_value() {
    local key="$1"
    local app="$2"
    if [[ -f "${app}/Contents/Info.plist" ]]; then
        /usr/libexec/PlistBuddy -c "Print :${key}" "${app}/Contents/Info.plist" 2>/dev/null || true
    fi
}

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
if [[ "$(bundle_value CFBundleIdentifier "${staged_app}")" != "com.deepori.codex-feishu-bridge" ]]; then
    exit 2
fi
if [[ "$(bundle_value CFBundleShortVersionString "${staged_app}")" != "${expected_version}" ]]; then
    exit 2
fi
/usr/bin/codesign --verify --deep --strict "${staged_app}"
/usr/bin/lipo "${staged_app}/Contents/MacOS/CodexFeishuBridge" -verify_arch arm64 x86_64
/usr/bin/lipo "${staged_app}/Contents/Resources/bridge/promlight-helper" -verify_arch arm64 x86_64

destination_version_before="$(bundle_value CFBundleShortVersionString "${destination}")"
destination_build_before="$(bundle_value CFBundleVersion "${destination}")"
runtime_was_running=0
if /bin/launchctl print "${domain}/${label}" 2>/dev/null | /usr/bin/grep -q $'^\tstate = running$'; then
    runtime_was_running=1
fi
runtime_updated_before="$(/usr/bin/python3 - "${runtime_status}" <<'PY'
import json
from pathlib import Path
import sys

try:
    payload = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    print(float(payload.get("updated_at") or 0))
except (OSError, TypeError, ValueError, UnicodeDecodeError, json.JSONDecodeError):
    print(0)
PY
)"

for _attempt in {1..300}; do
    if /usr/bin/shlock -f "${lock_file}" -p $$; then
        lock_acquired=1
        break
    fi
    /bin/sleep 0.1
done
if (( ! lock_acquired )); then
    print "another update is still in progress"
    exit 6
fi

destination_version_now="$(bundle_value CFBundleShortVersionString "${destination}")"
destination_build_now="$(bundle_value CFBundleVersion "${destination}")"
if [[ "${destination_version_now}" != "${destination_version_before}" || \
      "${destination_build_now}" != "${destination_build_before}" ]]; then
    print "destination changed while update was waiting; refusing stale replacement"
    exit 6
fi

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

restore_previous() {
    if [[ -e "${destination}" ]]; then
        /bin/rm -rf "${destination}"
    fi
    if [[ -e "${previous}" ]]; then
        /bin/mv "${previous}" "${destination}"
        if [[ -x "${destination}/Contents/Resources/bridge/install.sh" ]]; then
            "${destination}/Contents/Resources/bridge/install.sh" || true
        fi
        if (( runtime_was_running )) && \
           [[ -x "${destination}/Contents/Resources/bridge/control.sh" ]]; then
            "${destination}/Contents/Resources/bridge/control.sh" start || true
        fi
        /usr/bin/open "${destination}" || true
    fi
}

new_installer="${destination}/Contents/Resources/bridge/install.sh"
if [[ ! -x "${new_installer}" ]] || ! "${new_installer}"; then
    restore_previous
    print "new runtime installation failed; previous runtime restored"
    exit 5
fi

health_ok=0
if (( runtime_was_running )); then
    for _attempt in {1..100}; do
        if /usr/bin/python3 - "${runtime_status}" "${runtime_updated_before}" <<'PY'
import json
from pathlib import Path
import sys

try:
    payload = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    previous_update = float(sys.argv[2])
    current_update = float(payload.get("updated_at") or 0)
except (OSError, TypeError, ValueError, UnicodeDecodeError, json.JSONDecodeError):
    raise SystemExit(1)
raise SystemExit(
    0
    if payload.get("active_consumers") == 3 and current_update > previous_update
    else 1
)
PY
        then
            health_ok=1
            break
        fi
        /bin/sleep 0.1
    done
else
    if /usr/bin/python3 \
        "${HOME}/Library/Application Support/Codex Feishu Bridge/bridge.py" \
        --self-test; then
        health_ok=1
    fi
fi
if (( ! health_ok )); then
    restore_previous
    print "new runtime failed health handshake; previous runtime restored"
    exit 5
fi

if ! /usr/bin/open "${destination}"; then
    restore_previous
    print "new app failed to open; previous app and runtime restored"
    exit 5
fi
/bin/sleep 2
if [[ -e "${previous}" ]]; then
    /bin/rm -rf "${previous}"
fi
/bin/rm -rf "${staged_app:h:h}"
print "update completed"
