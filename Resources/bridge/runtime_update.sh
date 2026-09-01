#!/bin/zsh

set -euo pipefail

label="com.deepori.codex-feishu-bridge"
domain="gui/$(/usr/bin/id -u)"
resource_dir="${0:A:h}"
support_dir="${HOME}/Library/Application Support/Codex Feishu Bridge"
plist="${HOME}/Library/LaunchAgents/${label}.plist"
runtime_status="${HOME}/.codex/feishu-bridge/runtime-status.json"
control_hook="${HOME}/.codex/hooks/feishu_bridge_control.sh"
log_dir="${HOME}/.codex/log"
lock_file="${log_dir}/feishu-bridge-app-update.lock"
runtime_files=(bridge.py control.sh diagnose.sh uninstall.sh lark-cli promlight-helper)
backup_dir=""
lock_acquired=0

umask 077
/bin/mkdir -p "${log_dir}"
/bin/chmod 700 "${log_dir}"

cleanup() {
    if [[ -n "${backup_dir}" && -d "${backup_dir}" ]]; then
        /bin/rm -rf "${backup_dir}"
    fi
    if (( lock_acquired )); then
        /bin/rm -f "${lock_file}"
    fi
}
trap cleanup EXIT HUP INT TERM

for _attempt in {1..300}; do
    if /usr/bin/shlock -f "${lock_file}" -p $$; then
        lock_acquired=1
        break
    fi
    /bin/sleep 0.1
done
if (( ! lock_acquired )); then
    print -u2 "another install or update is still in progress"
    exit 6
fi

backup_dir="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/CodexFeishuBridgeRuntime.XXXXXX")"
/bin/chmod 700 "${backup_dir}"
for name in "${runtime_files[@]}"; do
    source="${support_dir}/${name}"
    if [[ -f "${source}" && ! -L "${source}" ]]; then
        /bin/cp -p "${source}" "${backup_dir}/${name}"
    elif [[ -e "${source}" || -L "${source}" ]]; then
        print -u2 "refusing unsafe existing runtime file"
        exit 2
    fi
done
if [[ -f "${plist}" && ! -L "${plist}" ]]; then
    /bin/cp -p "${plist}" "${backup_dir}/launch-agent.plist"
elif [[ -e "${plist}" || -L "${plist}" ]]; then
    print -u2 "refusing unsafe existing LaunchAgent"
    exit 2
fi
if [[ -f "${control_hook}" && ! -L "${control_hook}" ]]; then
    /bin/cp -p "${control_hook}" "${backup_dir}/control-hook.sh"
elif [[ -e "${control_hook}" || -L "${control_hook}" ]]; then
    print -u2 "refusing unsafe existing control hook"
    exit 2
fi

was_running=0
if /bin/launchctl print "${domain}/${label}" >/dev/null 2>&1; then
    was_running=1
fi
previous_updated="$(/usr/bin/python3 - "${runtime_status}" <<'PY'
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

restore_previous_files() {
    for name in "${runtime_files[@]}"; do
        destination="${support_dir}/${name}"
        backup="${backup_dir}/${name}"
        if [[ -f "${backup}" ]]; then
            temporary="${support_dir}/.${name}.restore.$$"
            /bin/cp -p "${backup}" "${temporary}"
            /bin/mv -f "${temporary}" "${destination}"
        else
            /bin/rm -f "${destination}"
        fi
    done
    if [[ -f "${backup_dir}/launch-agent.plist" ]]; then
        /bin/cp -p "${backup_dir}/launch-agent.plist" "${plist}.restore.$$"
        /bin/mv -f "${plist}.restore.$$" "${plist}"
    else
        /bin/rm -f "${plist}"
    fi
    if [[ -f "${backup_dir}/control-hook.sh" ]]; then
        temporary="${control_hook}.restore.$$"
        /bin/cp -p "${backup_dir}/control-hook.sh" "${temporary}"
        /bin/mv -f "${temporary}" "${control_hook}"
    else
        /bin/rm -f "${control_hook}"
    fi
}

restore_previous_runtime() {
    if [[ -x "${support_dir}/control.sh" ]]; then
        "${support_dir}/control.sh" stop || true
    else
        /bin/launchctl bootout "${domain}/${label}" >/dev/null 2>&1 || true
    fi
    restore_previous_files
    if (( was_running )) && [[ -x "${support_dir}/control.sh" ]]; then
        "${support_dir}/control.sh" start || true
    fi
}

if "${resource_dir}/install.sh"; then
    :
else
    install_status=$?
    if (( install_status == 75 )); then
        restore_previous_files
        print -u2 "runtime update deferred because Bridge is no longer idle"
        exit 75
    fi
    restore_previous_runtime
    print -u2 "runtime installation failed; previous runtime restored"
    exit 5
fi

health_ok=0
if (( was_running )); then
    for _attempt in {1..100}; do
        if /usr/bin/python3 - "${runtime_status}" "${previous_updated}" <<'PY'
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
elif /usr/bin/python3 "${support_dir}/bridge.py" --self-test; then
    health_ok=1
fi

if (( health_ok )); then
    print "runtime update completed"
    exit 0
fi

restore_previous_runtime
print -u2 "new runtime failed health verification; previous runtime restored"
exit 5
