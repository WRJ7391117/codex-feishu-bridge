#!/bin/zsh

set -euo pipefail

mode="${1:---keep-data}"
label="${CODEX_FEISHU_LAUNCHD_LABEL:-com.deepori.codex-feishu-bridge}"
legacy_label="${CODEX_FEISHU_LEGACY_LAUNCHD_LABEL:-com.openai.codex.feishu-bridge}"
domain="gui/$(/usr/bin/id -u)"
support_dir="${HOME}/Library/Application Support/Codex Feishu Bridge"
state_dir="${HOME}/.codex/feishu-bridge"
log_dir="${HOME}/.codex/log"
plist="${HOME}/Library/LaunchAgents/${label}.plist"
legacy_plist="${HOME}/Library/LaunchAgents/${legacy_label}.plist"
resource_dir="${0:A:h}"

if [[ "${mode}" != "--keep-data" && "${mode}" != "--purge" ]]; then
    print -u2 "用法：${0:t} --keep-data|--purge"
    exit 2
fi

account_home="$(/usr/bin/python3 -B - <<'PY'
import os
import pwd

print(pwd.getpwuid(os.getuid()).pw_dir)
PY
)"
if [[ "${HOME:A}" != "${account_home:A}" && -z "${CODEX_FEISHU_LAUNCHD_LABEL:-}" ]]; then
    print -u2 "refusing LaunchAgent changes with an overridden HOME and production label"
    exit 2
fi

if [[ "${mode}" == "--purge" ]]; then
    print -n "Type PURGE to remove the local Profile, config, state, and logs: "
    IFS= read -r confirmation
    if [[ "${confirmation}" != "PURGE" ]]; then
        print -u2 "purge canceled"
        exit 2
    fi
fi

/usr/bin/python3 -B - "${state_dir}/state.json" <<'PY'
import json
from pathlib import Path
import sys

path = Path(sys.argv[1])
if not path.exists():
    raise SystemExit(0)
try:
    state = json.loads(path.read_text(encoding="utf-8"))
except (OSError, json.JSONDecodeError) as exc:
    raise SystemExit("cannot verify pending bridge work") from exc
if not isinstance(state, dict):
    raise SystemExit("cannot verify pending bridge work")
if (
    state.get("pending_inputs")
    or state.get("pending_replies")
    or state.get("pending_task_creations")
):
    raise SystemExit("bridge has pending Feishu work; wait for queues to clear")
PY

profile=""
if [[ -f "${support_dir}/config.json" ]]; then
    profile="$(/usr/bin/python3 -B - "${support_dir}/config.json" <<'PY'
import json
from pathlib import Path
import sys

try:
    payload = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
except (OSError, json.JSONDecodeError):
    raise SystemExit(0)
value = str(payload.get("lark_profile") or "").strip()
if value and all(character.isalnum() or character in "-_." for character in value):
    print(value)
PY
)"
fi

if [[ -x "${support_dir}/control.sh" ]]; then
    "${support_dir}/control.sh" stop >/dev/null 2>&1 || true
elif /bin/launchctl print "${domain}/${label}" >/dev/null 2>&1; then
    /bin/launchctl bootout "${domain}/${label}" >/dev/null 2>&1 || true
fi

if [[ -n "${profile}" ]]; then
    cli="${support_dir}/lark-cli"
    [[ -x "${cli}" ]] || cli="${resource_dir}/lark-cli"
    if [[ -x "${cli}" ]]; then
        "${cli}" --profile "${profile}" event stop >/dev/null 2>&1 || true
        if [[ "${mode}" == "--purge" ]]; then
            "${cli}" --profile "${profile}" config remove >/dev/null 2>&1 || true
        fi
    fi
fi

/bin/rm -f -- \
    "${plist}" \
    "${legacy_plist}" \
    "${HOME}/.codex/hooks/feishu_bridge_control.sh" \
    "${support_dir}/bridge.py" \
    "${support_dir}/workflow_notifications.py" \
    "${support_dir}/workflow-notify" \
    "${support_dir}/workflow-config" \
    "${support_dir}/control.sh" \
    "${support_dir}/diagnose.sh" \
    "${support_dir}/uninstall.sh" \
    "${support_dir}/lark-cli"

if [[ "${mode}" == "--keep-data" ]]; then
    print "service removed; local data preserved"
    exit 0
fi

/usr/bin/python3 -B - \
    "${support_dir}" \
    "${state_dir}" \
    "${log_dir}/feishu-bridge.log" \
    "${log_dir}/feishu-bridge-launchd.log" \
    "${log_dir}/feishu-bridge-app-update.log" <<'PY'
import os
from pathlib import Path
import shutil
import stat
import sys

for raw in sys.argv[1:3]:
    path = Path(raw)
    if not os.path.lexists(path):
        continue
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
        raise SystemExit("refusing unsafe bridge directory")
    shutil.rmtree(path)

for raw in sys.argv[3:]:
    path = Path(raw)
    if not os.path.lexists(path):
        continue
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid():
        raise SystemExit("refusing unsafe bridge log")
    path.unlink()
PY

print "service and local bridge data removed"
