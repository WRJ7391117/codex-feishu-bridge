#!/bin/zsh

set -euo pipefail

label="com.deepori.codex-feishu-bridge"
legacy_label="com.openai.codex.feishu-bridge"
domain="gui/$(/usr/bin/id -u)"
support_dir="${HOME}/Library/Application Support/Codex Feishu Bridge"
launch_agents_dir="${HOME}/Library/LaunchAgents"
plist="${launch_agents_dir}/${label}.plist"
legacy_plist="${launch_agents_dir}/${legacy_label}.plist"
resource_dir="${0:A:h}"
was_running=0

if /bin/launchctl print "${domain}/${label}" >/dev/null 2>&1 || \
   /bin/launchctl print "${domain}/${legacy_label}" >/dev/null 2>&1; then
    was_running=1
fi

/bin/mkdir -p "${support_dir}" "${launch_agents_dir}" "${HOME}/.codex/log"
/bin/chmod 700 "${support_dir}"
/usr/bin/install -m 755 "${resource_dir}/feishu_codex_bridge.py" "${support_dir}/bridge.py"
/usr/bin/install -m 755 "${resource_dir}/control.sh" "${support_dir}/control.sh"
/usr/bin/install -m 755 "${resource_dir}/diagnose.sh" "${support_dir}/diagnose.sh"

if [[ ! -f "${support_dir}/config.json" ]]; then
    /usr/bin/python3 - "${support_dir}/config.json" \
        "${resource_dir}/config.example.json" \
        "${HOME}/.codex/hooks/feishu_codex_bridge.py" <<'PY'
import json
from pathlib import Path
import re
import sys

destination = Path(sys.argv[1])
template = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
legacy = Path(sys.argv[3])
if legacy.is_file():
    text = legacy.read_text(encoding="utf-8")
    patterns = {
        "lark_profile": r'^LARK_PROFILE\s*=\s*["\']([^"\']+)',
        "allowed_sender_id": r'^ALLOWED_SENDER_ID\s*=\s*["\']([^"\']+)',
    }
    for key, pattern in patterns.items():
        match = re.search(pattern, text, re.MULTILINE)
        if match:
            template[key] = match.group(1)
    sender = str(template.get("allowed_sender_id") or "").strip()
    if sender:
        template["allowed_users"] = [
            {
                "open_id": sender,
                "name": "现有用户",
                "allowed_projects": ["*"],
            }
        ]
    chat = re.search(r'^ALLOWED_CHAT_ID\s*=\s*["\']([^"\']+)', text, re.MULTILINE)
    if chat:
        template["allowed_chat_ids"] = [chat.group(1)]
destination.write_text(json.dumps(template, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
PY
fi
/bin/chmod 600 "${support_dir}/config.json"

/usr/bin/python3 - "${plist}" "${support_dir}" <<'PY'
import plistlib
from pathlib import Path
import sys

destination = Path(sys.argv[1])
support = Path(sys.argv[2])
payload = {
    "Label": "com.deepori.codex-feishu-bridge",
    "ProgramArguments": ["/usr/bin/python3", str(support / "bridge.py")],
    "WorkingDirectory": str(Path.home()),
    "EnvironmentVariables": {
        "PATH": "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin",
    },
    "RunAtLoad": True,
    "KeepAlive": True,
    "ProcessType": "Background",
    "StandardOutPath": str(Path.home() / ".codex/log/feishu-bridge-launchd.log"),
    "StandardErrorPath": str(Path.home() / ".codex/log/feishu-bridge-launchd.log"),
}
temporary = destination.with_suffix(".tmp")
with temporary.open("wb") as handle:
    plistlib.dump(payload, handle, sort_keys=False)
temporary.replace(destination)
PY
/bin/chmod 600 "${plist}"
/usr/bin/plutil -lint "${plist}" >/dev/null

if /bin/launchctl print "${domain}/${legacy_label}" >/dev/null 2>&1; then
    /bin/launchctl bootout "${domain}/${legacy_label}" || true
fi
if /bin/launchctl print "${domain}/${label}" >/dev/null 2>&1; then
    /bin/launchctl bootout "${domain}/${label}" || true
fi
for _attempt in {1..100}; do
    if ! /bin/launchctl print "${domain}/${legacy_label}" >/dev/null 2>&1 && \
       ! /bin/launchctl print "${domain}/${label}" >/dev/null 2>&1; then
        break
    fi
    /bin/sleep 0.1
done
if [[ -f "${legacy_plist}" && ! -f "${legacy_plist}.migrated-backup" ]]; then
    /bin/mv "${legacy_plist}" "${legacy_plist}.migrated-backup"
fi

if (( was_running )); then
    /bin/launchctl enable "${domain}/${label}"
    if ! /bin/launchctl bootstrap "${domain}" "${plist}"; then
        /bin/sleep 0.5
        /bin/launchctl print "${domain}/${label}" >/dev/null 2>&1 || \
            /bin/launchctl bootstrap "${domain}" "${plist}"
    fi
    /bin/launchctl kickstart -k "${domain}/${label}"
fi

print "installed"
