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
expected_build="$(bundle_value CFBundleVersion "${staged_app}")"
if [[ -z "${expected_build}" ]]; then
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

if /usr/bin/shlock -f "${lock_file}" -p $$; then
    lock_acquired=1
else
    print "another update is still in progress"
    exit 6
fi

destination_version_now="$(bundle_value CFBundleShortVersionString "${destination}")"
destination_build_now="$(bundle_value CFBundleVersion "${destination}")"
if [[ "${destination_version_now}" != "${destination_version_before}" || \
      "${destination_build_now}" != "${destination_build_before}" ]]; then
    print "destination changed before replacement; refusing stale update"
    exit 6
fi

if ! /usr/bin/python3 - "${running_pid}" <<'PY'
import os
import select
import sys

pid = int(sys.argv[1])
try:
    os.kill(pid, 0)
except ProcessLookupError:
    raise SystemExit(0)
monitor = select.kqueue()
try:
    try:
        monitor.control(
            [
                select.kevent(
                    pid,
                    filter=select.KQ_FILTER_PROC,
                    flags=select.KQ_EV_ADD | select.KQ_EV_ENABLE | select.KQ_EV_ONESHOT,
                    fflags=select.KQ_NOTE_EXIT,
                )
            ],
            0,
            0,
        )
    except ProcessLookupError:
        raise SystemExit(0)
    if not monitor.control(None, 1, 30):
        raise SystemExit(1)
finally:
    monitor.close()
PY
then
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
if [[ ! -x "${new_installer}" ]]; then
    restore_previous
    print "new runtime installation failed; previous runtime restored"
    exit 5
fi
installer_output=""
if ! installer_output="$(
    CODEX_FEISHU_ALLOW_LEGACY_RUNTIME_DEFERRAL=1 "${new_installer}"
)"; then
    restore_previous
    print "new runtime installation failed; previous runtime restored"
    exit 5
fi
print -r -- "${installer_output}"
runtime_sync_deferred=0
if [[ "${installer_output}" == \
      "app installed; legacy runtime sync deferred until a safe stop window" ]]; then
    runtime_sync_deferred=1
fi

health_ok=0
if (( runtime_sync_deferred )); then
    health_ok=1
elif (( runtime_was_running )); then
    if /usr/bin/python3 - "${runtime_status}" "${runtime_updated_before}" <<'PY'
import json
import os
from pathlib import Path
import select
import sys
import time

path = Path(sys.argv[1])
previous_update = float(sys.argv[2])

def healthy():
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        current_update = float(payload.get("updated_at") or 0)
    except (OSError, TypeError, ValueError, UnicodeDecodeError, json.JSONDecodeError):
        return False
    return (
        payload.get("active_consumers") == 3
        and payload.get("update_protocol") == 1
        and current_update > previous_update
    )

descriptor = os.open(
    path.parent,
    os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0),
)
monitor = select.kqueue()
deadline = time.monotonic() + 10
try:
    monitor.control(
        [
            select.kevent(
                descriptor,
                filter=select.KQ_FILTER_VNODE,
                flags=select.KQ_EV_ADD | select.KQ_EV_ENABLE,
                fflags=select.KQ_NOTE_WRITE,
            )
        ],
        0,
        0,
    )
    if healthy():
        raise SystemExit(0)
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0 or not monitor.control(None, 1, remaining):
            raise SystemExit(1)
        if healthy():
            raise SystemExit(0)
finally:
    monitor.close()
    os.close(descriptor)
PY
    then
        health_ok=1
    fi
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

if ! /usr/bin/python3 - \
    "${destination}" \
    "${expected_version}" \
    "${expected_build}" <<'PY'
import json
import os
from pathlib import Path
import secrets
import select
import stat
import subprocess
import sys
import time

application = Path(sys.argv[1])
expected_version = sys.argv[2]
expected_build = sys.argv[3]
nonce = secrets.token_hex(16)
root = Path("/tmp") / f"codex-feishu-bridge-{os.getuid()}"
try:
    root.mkdir(mode=0o700)
except FileExistsError:
    root_stat = root.lstat()
    if (
        not stat.S_ISDIR(root_stat.st_mode)
        or root.is_symlink()
        or root_stat.st_uid != os.getuid()
        or root_stat.st_mode & 0o077
    ):
        raise SystemExit("unsafe app launch acknowledgement directory")
root.chmod(0o700)
directory = root / f"app-launch-ack-{nonce}"
directory.mkdir(mode=0o700)
ack_path = directory / "ready.json"
descriptor = os.open(
    directory,
    os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0),
)
monitor = select.kqueue()
process = None
try:
    opened = os.fstat(descriptor)
    if (
        not stat.S_ISDIR(opened.st_mode)
        or opened.st_uid != os.getuid()
        or opened.st_mode & 0o077
    ):
        raise RuntimeError("unsafe app launch acknowledgement directory")
    monitor.control(
        [
            select.kevent(
                descriptor,
                filter=select.KQ_FILTER_VNODE,
                flags=select.KQ_EV_ADD | select.KQ_EV_ENABLE,
                fflags=select.KQ_NOTE_WRITE,
            )
        ],
        0,
        0,
    )
    process = subprocess.Popen(
        [
            str(application / "Contents/MacOS/CodexFeishuBridge"),
            "--update-launch-ack-path",
            str(ack_path),
            "--update-launch-ack-nonce",
            nonce,
        ],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        start_new_session=True,
    )
    monitor.control(
        [
            select.kevent(
                process.pid,
                filter=select.KQ_FILTER_PROC,
                flags=select.KQ_EV_ADD | select.KQ_EV_ENABLE | select.KQ_EV_ONESHOT,
                fflags=select.KQ_NOTE_EXIT,
            )
        ],
        0,
        0,
    )
    deadline = time.monotonic() + 10
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RuntimeError("new app did not acknowledge launch")
        events = monitor.control(None, 2, remaining)
        if not events:
            raise RuntimeError("new app did not acknowledge launch")
        if any(event.filter == select.KQ_FILTER_PROC for event in events):
            raise RuntimeError("new app exited during launch acknowledgement")
        if not ack_path.is_file():
            continue
        payload = json.loads(ack_path.read_text(encoding="utf-8"))
        if payload != {
            "nonce": nonce,
            "version": expected_version,
            "build": expected_build,
        }:
            raise RuntimeError("new app returned an invalid launch acknowledgement")
        if process.poll() is not None:
            raise RuntimeError("new app exited during launch acknowledgement")
        break
except Exception:
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=2)
    raise
finally:
    monitor.close()
    os.close(descriptor)
    ack_path.unlink(missing_ok=True)
    directory.rmdir()
PY
then
    restore_previous
    print "new app failed launch handshake; previous app and runtime restored"
    exit 5
fi
if [[ -e "${previous}" ]]; then
    /bin/rm -rf "${previous}"
fi
/bin/rm -rf "${staged_app:h:h}"
print "update completed"
