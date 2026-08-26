#!/bin/zsh

set -u

result=0

label="com.deepori.codex-feishu-bridge"
domain="gui/$(/usr/bin/id -u)"
support_dir="${HOME}/Library/Application Support/Codex Feishu Bridge"
config="${support_dir}/config.json"
bridge="${support_dir}/bridge.py"

print "Codex 飞书桥接诊断"
print "=================="

if [[ -x "${support_dir}/lark-cli" ]]; then
    lark_cli="${support_dir}/lark-cli"
elif [[ -x /opt/homebrew/bin/lark-cli ]]; then
    lark_cli=/opt/homebrew/bin/lark-cli
elif [[ -x /usr/local/bin/lark-cli ]]; then
    lark_cli=/usr/local/bin/lark-cli
else
    lark_cli="$(command -v lark-cli 2>/dev/null || true)"
fi

if [[ -n "${lark_cli}" ]]; then
    print "lark-cli: $(${lark_cli} --version 2>&1 | head -1)"
else
    print "lark-cli: 未安装"
    result=1
fi

if [[ -f "${bridge}" ]]; then
    /usr/bin/python3 "${bridge}" --diagnose-json 2>&1 || result=1
else
    print "bridge.py: 未安装"
    result=1
fi

if /bin/launchctl print "${domain}/${label}" 2>/dev/null | /usr/bin/grep -q $'^\tstate = running$'; then
    print "LaunchAgent: running"
else
    print "LaunchAgent: stopped"
    result=1
fi

if [[ -f "${config}" && -n "${lark_cli}" ]]; then
    profile=$(/usr/bin/python3 - "${config}" <<'PY'
import json, sys
try:
    print(json.load(open(sys.argv[1], encoding="utf-8")).get("lark_profile", "codex-notify"))
except Exception:
    print("codex-notify")
PY
)
    doctor_status=0
    doctor_output="$(
        LARKSUITE_CLI_NO_UPDATE_NOTIFIER=1 LARKSUITE_CLI_NO_SKILLS_NOTIFIER=1 \
            "${lark_cli}" --profile "${profile}" doctor 2>&1
    )" || doctor_status=$?
    if (( doctor_status != 0 )); then
        result=1
    fi
    print -r -- "${doctor_output}" | /usr/bin/python3 -c '
import json
import re
import sys

raw = sys.stdin.read()
try:
    payload = json.loads(raw)
except json.JSONDecodeError:
    sys.stdout.write(raw)
    raise SystemExit(0)
checks = payload.get("checks")
if isinstance(checks, list):
    current = next(
        (
            str(check.get("message") or "")
            for check in checks
            if isinstance(check, dict) and check.get("name") == "cli_version"
        ),
        "",
    )
    match = re.fullmatch(r"(\d+\.\d+\.\d+)-codex-feishu\.\d+", current)
    if match:
        ignored_message = f"{current} → {match.group(1)} available"
        payload["checks"] = [
            check
            for check in checks
            if not (
                isinstance(check, dict)
                and check.get("name") == "cli_update"
                and check.get("message") == ignored_message
            )
        ]
print(json.dumps(payload, ensure_ascii=False, indent=2))
' || result=1
    event_status="$(
        LARKSUITE_CLI_NO_UPDATE_NOTIFIER=1 LARKSUITE_CLI_NO_SKILLS_NOTIFIER=1 \
            "${lark_cli}" --profile "${profile}" event status --current --json 2>&1
    )" || result=1
    print "${event_status}"
    /usr/bin/python3 - "${event_status}" <<'PY' || result=1
import json, sys
try:
    apps = json.loads(sys.argv[1]).get("apps", [])
except Exception:
    raise SystemExit(1)
active = max([int(app.get("active_consumers", 0)) for app in apps] + [0])
raise SystemExit(0 if active == 3 else 1)
PY

    if [[ -x "${support_dir}/workflow-config" ]]; then
        workflow_status="$("${support_dir}/workflow-config" --status 2>&1)" || result=1
        print "workflow config: ${workflow_status}"
    else
        workflow_status="missing"
        print "workflow config: missing client"
        result=1
    fi
    if [[ "${workflow_status}" == "configured" ]]; then
        if [[ -x "${support_dir}/workflow-notify" ]]; then
            "${support_dir}/workflow-notify" --health 2>&1 || result=1
        else
            print "workflow endpoint: missing client"
            result=1
        fi
    fi
fi

exit "${result}"
