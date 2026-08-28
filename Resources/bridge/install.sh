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

umask 077

# Preflight every source and existing destination before changing local state.
/usr/bin/python3 -B - \
    --resources \
    "${resource_dir}/feishu_codex_bridge.py" \
    "${resource_dir}/control.sh" \
    "${resource_dir}/diagnose.sh" \
    "${resource_dir}/uninstall.sh" \
    "${resource_dir}/config.example.json" \
    "${resource_dir}/lark-cli" \
    --executables \
    "${resource_dir}/lark-cli" \
    --directories \
    "${support_dir}" \
    "${launch_agents_dir}" \
    "${HOME}/.codex/log" \
    "${HOME}/.codex/feishu-bridge" \
    "${HOME}/.codex/hooks" \
    --config \
    "${support_dir}/config.json" \
    --files \
    "${HOME}/.codex/feishu-bridge/state.json" \
    "${HOME}/.codex/feishu-bridge/runtime-status.json" \
    "${HOME}/.codex/log/feishu-bridge.log" \
    "${HOME}/.codex/log/feishu-bridge-launchd.log" \
    "${support_dir}/bridge.py" \
    "${support_dir}/control.sh" \
    "${support_dir}/diagnose.sh" \
    "${support_dir}/uninstall.sh" \
    "${support_dir}/lark-cli" \
    "${HOME}/.codex/hooks/feishu_bridge_control.sh" \
    "${plist}" \
    "${legacy_plist}" \
    "${HOME}/.codex/hooks/feishu_codex_bridge.py" <<'PY'
import json
import os
from pathlib import Path
import stat
import sys

sections = {
    "resources": [],
    "executables": [],
    "directories": [],
    "config": [],
    "workflow-state": [],
    "files": [],
}
current = None
for value in sys.argv[1:]:
    if value.startswith("--"):
        current = value.removeprefix("--")
        if current not in sections:
            raise SystemExit("invalid installer preflight")
        continue
    if current is None:
        raise SystemExit("invalid installer preflight")
    sections[current].append(Path(value))

for path in sections["resources"]:
    try:
        path_stat = path.lstat()
    except OSError as exc:
        raise SystemExit("installation package is incomplete") from exc
    if (
        stat.S_ISLNK(path_stat.st_mode)
        or not stat.S_ISREG(path_stat.st_mode)
        or not os.access(path, os.R_OK)
    ):
        raise SystemExit("installation package is unsafe")

for path in sections["executables"]:
    if not os.access(path, os.X_OK):
        raise SystemExit("installation package executable is unavailable")

try:
    template_path = next(
        path for path in sections["resources"] if path.name == "config.example.json"
    )
    json.loads(template_path.read_text(encoding="utf-8"))
except (OSError, UnicodeDecodeError, json.JSONDecodeError, StopIteration) as exc:
    raise SystemExit("installation config template is invalid") from exc

for path in sections["directories"]:
    if not os.path.lexists(path):
        continue
    path_stat = path.lstat()
    if (
        stat.S_ISLNK(path_stat.st_mode)
        or not stat.S_ISDIR(path_stat.st_mode)
        or path_stat.st_uid != os.getuid()
    ):
        raise SystemExit("refusing unsafe existing directory")

for path in sections["files"] + sections["config"] + sections["workflow-state"]:
    if not os.path.lexists(path):
        continue
    path_stat = path.lstat()
    if (
        stat.S_ISLNK(path_stat.st_mode)
        or not stat.S_ISREG(path_stat.st_mode)
        or path_stat.st_uid != os.getuid()
    ):
        raise SystemExit("refusing unsafe existing file")

def read_json(path: Path, error: str):
    if not os.path.lexists(path):
        return None
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise SystemExit(error) from exc
    return payload

def validate_config(payload) -> None:
    if not isinstance(payload, dict):
        raise SystemExit("existing bridge config is invalid")
    string_fields = (
        "lark_profile",
        "allowed_sender_id",
        "current_task_menu_event_key",
        "task_menu_event_key",
        "new_task_menu_event_key",
        "archive_task_menu_event_key",
        "usage_menu_event_key",
        "desktop_sync_menu_event_key",
        "desktop_sync_switch_menu_event_key",
        "task_subscriptions_menu_event_key",
        "lark_cli_path",
        "codex_cli_path",
    )
    for field in string_fields:
        if field in payload and not isinstance(payload[field], str):
            raise SystemExit("existing bridge config is invalid")
    if "allow_access_requests" in payload and not isinstance(
        payload["allow_access_requests"], bool
    ):
        raise SystemExit("existing bridge config is invalid")
    numeric_fields = (
        "max_prompt_chars",
        "max_reply_chars",
        "max_result_images",
        "max_result_files",
        "max_result_file_bytes",
        "max_pending_file_bytes",
        "max_pending_file_spool_bytes",
        "max_input_images",
        "max_input_image_bytes",
        "max_input_files",
        "max_input_file_bytes",
        "max_pending_image_bytes",
        "max_pending_image_spool_bytes",
        "max_pending_inputs",
        "max_pending_inputs_per_task",
        "max_concurrent_runs",
    )
    for field in numeric_fields:
        if field in payload and (
            type(payload[field]) is not int or payload[field] < 0
        ):
            raise SystemExit("existing bridge config is invalid")
    allowed_chat_ids = payload.get("allowed_chat_ids", [])
    if not isinstance(allowed_chat_ids, list) or any(
        not isinstance(value, str) for value in allowed_chat_ids
    ):
        raise SystemExit("existing bridge config is invalid")
    allowed_users = payload.get("allowed_users")
    if allowed_users is not None and (
        not isinstance(allowed_users, list)
        or any(
            not isinstance(user, dict)
            or not isinstance(user.get("open_id"), str)
            or (
                "name" in user
                and not isinstance(user.get("name"), str)
            )
            or not isinstance(user.get("allowed_projects"), list)
            or any(
                not isinstance(project, str)
                for project in user.get("allowed_projects", [])
            )
            for user in allowed_users
        )
    ):
        raise SystemExit("existing bridge config is invalid")
    workflow = payload.get("workflow_notifications")
    if workflow is not None and (
        not isinstance(workflow, dict)
        or not isinstance(workflow.get("enabled"), bool)
        or any(
            not isinstance(workflow.get(field), str)
            for field in (
                "allowed_workflow_id",
                "recipient_open_id",
                "recipient_chat_id",
                "codex_task_id",
            )
        )
    ):
        raise SystemExit("existing bridge config is invalid")

for path in sections["config"]:
    payload = read_json(path, "existing bridge config is invalid")
    if payload is not None:
        validate_config(payload)

state_path = next(
    (path for path in sections["files"] if path.name == "state.json"),
    None,
)
if state_path is not None:
    bridge_state = read_json(state_path, "existing bridge state is invalid")
    if bridge_state is not None:
        if not isinstance(bridge_state, dict):
            raise SystemExit("existing bridge state is invalid")
        pending_inputs = bridge_state.get("pending_inputs", [])
        pending_replies = bridge_state.get("pending_replies", [])
        pending_creations = bridge_state.get("pending_task_creations", {})
        if (
            not isinstance(pending_inputs, list)
            or not isinstance(pending_replies, list)
            or not isinstance(pending_creations, dict)
        ):
            raise SystemExit("existing bridge state is invalid")
        if pending_inputs or pending_replies or pending_creations:
            raise SystemExit(
                "bridge has pending Feishu work; wait for all queues to clear before updating"
            )

runtime_path = next(
    (path for path in sections["files"] if path.name == "runtime-status.json"),
    None,
)
if runtime_path is not None:
    runtime_status = read_json(runtime_path, "existing runtime status is invalid")
    if runtime_status is not None:
        if not isinstance(runtime_status, dict):
            raise SystemExit("existing runtime status is invalid")
        active_runs = runtime_status.get("active_runs", 0)
        if not isinstance(active_runs, int) or isinstance(active_runs, bool):
            raise SystemExit("existing runtime status is invalid")
        if active_runs > 0:
            raise SystemExit(
                "bridge has active Feishu runs; wait for them to finish before updating"
            )
PY

if /bin/launchctl print "${domain}/${label}" >/dev/null 2>&1 || \
   /bin/launchctl print "${domain}/${legacy_label}" >/dev/null 2>&1; then
    was_running=1
fi

/usr/bin/python3 - \
    "${support_dir}" \
    "${launch_agents_dir}" \
    "${HOME}/.codex/log" \
    "${HOME}/.codex/feishu-bridge" \
    "${HOME}/.codex/feishu-bridge/workflow-decision-inbox" \
    "${HOME}/.codex/hooks" <<'PY'
import os
from pathlib import Path
import stat
import sys

for raw_path in sys.argv[1:]:
    path = Path(raw_path)
    path.mkdir(parents=True, mode=0o700, exist_ok=True)
    path_stat = path.lstat()
    if (
        path.is_symlink()
        or not stat.S_ISDIR(path_stat.st_mode)
        or path_stat.st_uid != os.getuid()
    ):
        raise SystemExit(f"refusing unsafe private directory: {path}")
    path.chmod(0o700)
PY

if [[ ! -f "${support_dir}/config.json" ]]; then
    /usr/bin/python3 - "${support_dir}/config.json" \
        "${resource_dir}/config.example.json" \
        "${HOME}/.codex/hooks/feishu_codex_bridge.py" <<'PY'
import json
import os
from pathlib import Path
import re
import secrets
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
temporary = destination.parent / (
    f".{destination.name}.{os.getpid()}.{secrets.token_hex(8)}.tmp"
)
flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
try:
    descriptor = os.open(temporary, flags, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
        json.dump(template, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    if os.path.lexists(destination):
        raise SystemExit("refusing unexpected existing config")
    os.replace(temporary, destination)
    directory = os.open(
        destination.parent,
        os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
    )
    try:
        os.fsync(directory)
    finally:
        os.close(directory)
finally:
    temporary.unlink(missing_ok=True)
PY
fi
/usr/bin/python3 - "${support_dir}/config.json" <<'PY'
import json
import os
from pathlib import Path
import secrets
import stat
import sys

path = Path(sys.argv[1])
defaults = {
    "current_task_menu_event_key": "current_task",
    "task_menu_event_key": "select_task",
    "new_task_menu_event_key": "new_task",
    "archive_task_menu_event_key": "archive_task",
    "usage_menu_event_key": "codex_usage",
    "desktop_sync_menu_event_key": "sync_desktop",
    "desktop_sync_switch_menu_event_key": "sync_desktop_switch",
    "task_subscriptions_menu_event_key": "task_subscriptions",
}
before = path.lstat()
descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
try:
    opened = os.fstat(descriptor)
    if (
        opened.st_dev != before.st_dev
        or opened.st_ino != before.st_ino
        or not stat.S_ISREG(opened.st_mode)
        or opened.st_uid != os.getuid()
    ):
        raise SystemExit("existing bridge config changed during installation")
    with os.fdopen(descriptor, "r", encoding="utf-8") as handle:
        descriptor = -1
        payload = json.load(handle)
finally:
    if descriptor >= 0:
        os.close(descriptor)

changed = False
for key, value in defaults.items():
    if key not in payload:
        payload[key] = value
        changed = True
if not changed:
    raise SystemExit(0)

temporary = path.parent / (
    f".{path.name}.{os.getpid()}.{secrets.token_hex(8)}.tmp"
)
flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
try:
    descriptor = os.open(temporary, flags, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    current = path.lstat()
    if current.st_dev != before.st_dev or current.st_ino != before.st_ino:
        raise SystemExit("existing bridge config changed during installation")
    os.replace(temporary, path)
    directory = os.open(
        path.parent,
        os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
    )
    try:
        os.fsync(directory)
    finally:
        os.close(directory)
finally:
    temporary.unlink(missing_ok=True)
PY
/usr/bin/python3 - \
    "${support_dir}/config.json" \
    "${HOME}/.codex/feishu-bridge/state.json" \
    "${HOME}/.codex/feishu-bridge/runtime-status.json" \
    "${HOME}/.codex/feishu-bridge/workflow-state.json" \
    --logs \
    "${HOME}/.codex/log/feishu-bridge.log" \
    "${HOME}/.codex/log/feishu-bridge-launchd.log" <<'PY'
import os
from pathlib import Path
import stat
import sys

separator = sys.argv.index("--logs")
private_files = [Path(value) for value in sys.argv[1:separator]]
log_files = [Path(value) for value in sys.argv[separator + 1:]]

def secure_file(path: Path, create: bool) -> None:
    flags = os.O_RDWR | getattr(os, "O_NOFOLLOW", 0)
    if create:
        flags |= os.O_CREAT | os.O_APPEND
    try:
        descriptor = os.open(path, flags, 0o600)
    except FileNotFoundError:
        if create:
            raise
        return
    try:
        path_stat = os.fstat(descriptor)
        if not stat.S_ISREG(path_stat.st_mode) or path_stat.st_uid != os.getuid():
            raise SystemExit("refusing unsafe private file")
        os.fchmod(descriptor, 0o600)
        os.fsync(descriptor)
    finally:
        os.close(descriptor)

for path in private_files:
    secure_file(path, create=False)
for path in log_files:
    secure_file(path, create=True)
PY

# Stage every runtime file first, then replace the set transactionally with rollback.
/usr/bin/python3 - \
    "${resource_dir}/feishu_codex_bridge.py" "${support_dir}/bridge.py" 755 \
    "${resource_dir}/control.sh" "${support_dir}/control.sh" 755 \
    "${resource_dir}/diagnose.sh" "${support_dir}/diagnose.sh" 755 \
    "${resource_dir}/uninstall.sh" "${support_dir}/uninstall.sh" 755 \
    "${resource_dir}/lark-cli" "${support_dir}/lark-cli" 755 \
    "${resource_dir}/control.sh" "${HOME}/.codex/hooks/feishu_bridge_control.sh" 755 <<'PY'
import os
from pathlib import Path
import secrets
import stat
import sys

if len(sys.argv[1:]) % 3:
    raise SystemExit("invalid runtime install set")

entries = [
    (Path(sys.argv[index]), Path(sys.argv[index + 1]), int(sys.argv[index + 2], 8))
    for index in range(1, len(sys.argv), 3)
]
staged = []
backups = {}
committed = []
directories = {destination.parent for _source, destination, _mode in entries}

def fsync_directories() -> None:
    for directory in directories:
        descriptor = os.open(
            directory,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)

try:
    for source, destination, mode in entries:
        source_stat = source.lstat()
        source_descriptor = os.open(
            source,
            os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0),
        )
        temporary = destination.parent / (
            f".{destination.name}.{os.getpid()}.{secrets.token_hex(8)}.new"
        )
        destination_descriptor = os.open(
            temporary,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0),
            mode,
        )
        staged.append((temporary, destination))
        try:
            opened_stat = os.fstat(source_descriptor)
            if (
                opened_stat.st_dev != source_stat.st_dev
                or opened_stat.st_ino != source_stat.st_ino
                or not stat.S_ISREG(opened_stat.st_mode)
            ):
                raise SystemExit("runtime source changed during installation")
            while True:
                chunk = os.read(source_descriptor, 1024 * 1024)
                if not chunk:
                    break
                view = memoryview(chunk)
                while view:
                    written = os.write(destination_descriptor, view)
                    view = view[written:]
            os.fchmod(destination_descriptor, mode)
            os.fsync(destination_descriptor)
        finally:
            os.close(source_descriptor)
            os.close(destination_descriptor)

    for temporary, destination in staged:
        backup = None
        if os.path.lexists(destination):
            destination_stat = destination.lstat()
            if (
                stat.S_ISLNK(destination_stat.st_mode)
                or not stat.S_ISREG(destination_stat.st_mode)
                or destination_stat.st_uid != os.getuid()
            ):
                raise SystemExit("runtime destination changed during installation")
            backup = destination.parent / (
                f".{destination.name}.{os.getpid()}.{secrets.token_hex(8)}.old"
            )
            os.link(destination, backup, follow_symlinks=False)
        backups[destination] = backup
        os.replace(temporary, destination)
        committed.append(destination)
    fsync_directories()
except BaseException:
    for destination in reversed(committed):
        backup = backups.get(destination)
        if backup is None:
            destination.unlink(missing_ok=True)
        elif backup.exists():
            os.replace(backup, destination)
    fsync_directories()
    raise
finally:
    for temporary, _destination in staged:
        temporary.unlink(missing_ok=True)
    for backup in backups.values():
        if backup is not None:
            backup.unlink(missing_ok=True)

fsync_directories()
PY

/usr/bin/python3 - "${plist}" "${support_dir}" <<'PY'
import os
import plistlib
from pathlib import Path
import secrets
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
temporary = destination.parent / (
    f".{destination.name}.{os.getpid()}.{secrets.token_hex(8)}.tmp"
)
descriptor = os.open(
    temporary,
    os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0),
    0o600,
)
try:
    with os.fdopen(descriptor, "wb") as handle:
        plistlib.dump(payload, handle, sort_keys=False)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, destination)
    directory = os.open(
        destination.parent,
        os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
    )
    try:
        os.fsync(directory)
    finally:
        os.close(directory)
finally:
    temporary.unlink(missing_ok=True)
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
