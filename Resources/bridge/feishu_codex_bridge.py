#!/usr/bin/env python3
"""Route authorized Feishu messages to a selected local Codex task."""

from __future__ import annotations

from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import select
import shlex
import shutil
import signal
import socket
import sqlite3
import stat
import struct
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any, Callable
from urllib.parse import unquote, urlsplit
import uuid


BRIDGE_RESOURCE_DIR = Path(__file__).resolve().parent
if str(BRIDGE_RESOURCE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_RESOURCE_DIR))

from workflow_notifications import (  # noqa: E402
    ORI_ONE_WORKFLOW_ID,
    WorkflowNotificationError,
    WorkflowDecisionInbox,
    WorkflowStateError,
    WorkflowStore,
    event_key as workflow_event_key,
    validate_payload as validate_workflow_payload,
)


HOME = Path.home()
APP_SUPPORT = HOME / "Library/Application Support/Codex Feishu Bridge"
CONFIG_PATH = Path(
    os.environ.get("CODEX_FEISHU_BRIDGE_CONFIG", APP_SUPPORT / "config.json")
).expanduser()
STATE_PATH = HOME / ".codex/feishu-bridge/state.json"
LOG_PATH = HOME / ".codex/log/feishu-bridge.log"
DESKTOP_STATE_PATH = HOME / ".codex/.codex-global-state.json"
DESKTOP_CATALOG_DB = HOME / ".codex/sqlite/codex-dev.db"
DESKTOP_IPC_SOCKET = HOME / ".codex/ipc/ipc.sock"
WORKFLOW_STATE_PATH = HOME / ".codex/feishu-bridge/workflow-state.json"
WORKFLOW_DECISION_INBOX_PATH = (
    HOME / ".codex/feishu-bridge/workflow-decision-inbox"
)
WORKFLOW_SOCKET_PATH = HOME / ".codex/feishu-bridge/workflow-notifications.sock"
WORKFLOW_CONTROL_SOCKET_PATH = HOME / ".codex/feishu-bridge/workflow-control.sock"


def load_config() -> dict[str, Any]:
    try:
        payload = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"invalid config: {CONFIG_PATH}: {exc}") from exc
    if not isinstance(payload, dict):
        raise RuntimeError(f"config must be a JSON object: {CONFIG_PATH}")
    return payload


CONFIG = load_config()
WORKFLOW_CONFIG = (
    CONFIG.get("workflow_notifications")
    if isinstance(CONFIG.get("workflow_notifications"), dict)
    else {}
)
_workflow_store = WorkflowStore(WORKFLOW_STATE_PATH)
_workflow_decision_inbox = WorkflowDecisionInbox(WORKFLOW_DECISION_INBOX_PATH)
_workflow_server_socket: socket.socket | None = None
_workflow_control_socket: socket.socket | None = None
_workflow_delivery_lock = threading.Lock()


def find_executable(
    config_key: str,
    names: tuple[str, ...],
    paths: tuple[str, ...],
    prefer_paths: bool = False,
) -> str:
    configured = str(CONFIG.get(config_key) or "").strip()
    if configured:
        candidate = Path(configured).expanduser()
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    if prefer_paths:
        for raw_path in paths:
            candidate = Path(raw_path).expanduser()
            if candidate.is_file() and os.access(candidate, os.X_OK):
                return str(candidate)
    for name in names:
        found = shutil.which(name)
        if found:
            return found
    if not prefer_paths:
        for raw_path in paths:
            candidate = Path(raw_path).expanduser()
            if candidate.is_file() and os.access(candidate, os.X_OK):
                return str(candidate)
    return configured


LARK_CLI = find_executable(
    "lark_cli_path",
    ("lark-cli",),
    (
        str(APP_SUPPORT / "lark-cli"),
        "/opt/homebrew/bin/lark-cli",
        "/usr/local/bin/lark-cli",
    ),
    prefer_paths=True,
)
LARK_PROFILE = str(CONFIG.get("lark_profile") or "codex-notify").strip()
CODEX_CLI = find_executable(
    "codex_cli_path",
    ("codex",),
    (
        "/Applications/ChatGPT.app/Contents/Resources/codex",
        "/Applications/Codex.app/Contents/Resources/codex",
        "~/Applications/ChatGPT.app/Contents/Resources/codex",
        "~/Applications/Codex.app/Contents/Resources/codex",
    ),
    prefer_paths=True,
)


def configured_allowed_users() -> dict[str, set[str]]:
    raw_users = CONFIG.get("allowed_users")
    users: dict[str, set[str]] = {}
    if isinstance(raw_users, list):
        for item in raw_users:
            if not isinstance(item, dict):
                continue
            open_id = str(item.get("open_id") or "").strip()
            raw_projects = item.get("allowed_projects")
            if not open_id or not isinstance(raw_projects, list):
                continue
            projects = {
                str(project).strip()
                for project in raw_projects
                if str(project).strip()
            }
            if projects:
                users[open_id] = projects
        return users

    legacy_sender = str(CONFIG.get("allowed_sender_id") or "").strip()
    return {legacy_sender: {"*"}} if legacy_sender else {}


def allowed_users_config_valid() -> bool:
    raw_users = CONFIG.get("allowed_users")
    if raw_users is None:
        return str(CONFIG.get("allowed_sender_id") or "").strip().startswith("ou_")
    if not isinstance(raw_users, list) or not raw_users:
        return False
    seen: set[str] = set()
    for item in raw_users:
        if not isinstance(item, dict):
            return False
        open_id = str(item.get("open_id") or "").strip()
        projects = item.get("allowed_projects")
        if (
            not open_id.startswith("ou_")
            or open_id in seen
            or not isinstance(projects, list)
            or not projects
            or any(not str(project).strip() for project in projects)
        ):
            return False
        seen.add(open_id)
    return True


ALLOWED_USERS = configured_allowed_users()
PRIMARY_ALLOWED_USER = next(iter(ALLOWED_USERS), "")
ALLOWED_CHAT_IDS = {
    str(value).strip()
    for value in CONFIG.get("allowed_chat_ids", [])
    if str(value).strip()
}
MAX_PROMPT_CHARS = int(CONFIG.get("max_prompt_chars", 12000))
MAX_REPLY_CHARS = int(CONFIG.get("max_reply_chars", 3000))
MAX_RESULT_IMAGES = max(0, int(CONFIG.get("max_result_images", 8)))
MAX_INPUT_IMAGES = max(1, int(CONFIG.get("max_input_images", 4)))
MAX_INPUT_IMAGE_BYTES = max(
    1,
    int(CONFIG.get("max_input_image_bytes", 20 * 1024 * 1024)),
)
MAX_INPUT_FILES = max(1, int(CONFIG.get("max_input_files", 4)))
MAX_INPUT_FILE_BYTES = max(
    1,
    int(CONFIG.get("max_input_file_bytes", 50 * 1024 * 1024)),
)

IMAGE_SUFFIXES = {".gif", ".jpeg", ".jpg", ".png", ".webp"}
DOCUMENT_SUFFIXES = {
    ".c", ".cpp", ".cs", ".csv", ".doc", ".docx", ".go", ".h", ".hpp",
    ".html", ".java", ".js", ".json", ".jsx", ".kt", ".kts", ".md",
    ".pdf", ".ppt", ".pptx", ".py", ".rs", ".rtf", ".sh", ".sql",
    ".swift", ".ts", ".tsx", ".txt", ".xls", ".xlsx", ".xml", ".yaml",
    ".yml", ".zsh",
}
AUDIO_SUFFIXES = {".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav"}
FILE_SUFFIXES = DOCUMENT_SUFFIXES | AUDIO_SUFFIXES
MARKDOWN_IMAGE_PATTERN = re.compile(r"!\[[^\]\n]*\]\(\s*(<[^>\n]+>|[^)\n]+)\s*\)")
IMAGE_KEY_PATTERN = r"img_[A-Za-z0-9_-]{3,512}"
FILE_KEY_PATTERN = r"file_[A-Za-z0-9_-]{3,512}"
INPUT_IMAGE_MARKER_PATTERN = re.compile(
    rf"!\[[^\]\n]*\]\(\s*({IMAGE_KEY_PATTERN})\s*\)"
    rf"|\[Image:\s*({IMAGE_KEY_PATTERN})\]",
    re.IGNORECASE,
)
INPUT_FILE_MARKER_PATTERN = re.compile(FILE_KEY_PATTERN, re.IGNORECASE)

EVENT_KEYS = (
    "im.message.receive_v1",
    "card.action.trigger",
    "application.bot.menu_v6",
)
TASK_MENU_EVENT_KEY = str(CONFIG.get("task_menu_event_key") or "select_task")
NEW_TASK_MENU_EVENT_KEY = str(
    CONFIG.get("new_task_menu_event_key") or "new_task"
)
ARCHIVE_TASK_MENU_EVENT_KEY = str(
    CONFIG.get("archive_task_menu_event_key") or "archive_task"
)
REPLY_RETRY_DELAYS = (1.0, 2.0)
CARD_PATCH_RETRY_DELAYS = (1.0, 2.0)
PENDING_REPLY_DELAYS = (15, 30, 60, 120, 300, 600)
MAX_PENDING_REPLIES = 50
MAX_PENDING_IMAGE_BYTES = max(
    1,
    int(CONFIG.get("max_pending_image_bytes", 20 * 1024 * 1024)),
)
MAX_PENDING_IMAGE_SPOOL_BYTES = max(
    MAX_PENDING_IMAGE_BYTES,
    int(CONFIG.get("max_pending_image_spool_bytes", 100 * 1024 * 1024)),
)
MAX_PENDING_INPUTS = max(1, int(CONFIG.get("max_pending_inputs", 50)))
MAX_PENDING_INPUTS_PER_TASK = max(
    1,
    int(CONFIG.get("max_pending_inputs_per_task", 10)),
)
MAX_CONCURRENT_RUNS = max(1, int(CONFIG.get("max_concurrent_runs", 2)))
ALLOW_ACCESS_REQUESTS = CONFIG.get("allow_access_requests", True) is not False
TASKS_PER_PAGE = max(10, min(50, int(CONFIG.get("tasks_per_page", 50))))

_last_reply_failure_reason = ""
_reply_failure_context = threading.local()

_consumers: list[subprocess.Popen[str]] = []
_state_lock = threading.RLock()
_active_runs_lock = threading.RLock()
_active_runs: dict[str, dict[str, Any]] = {}


def set_reply_failure_reason(reason: str) -> None:
    global _last_reply_failure_reason

    _last_reply_failure_reason = reason
    _reply_failure_context.reason = reason


def current_reply_failure_reason() -> str:
    return str(
        getattr(_reply_failure_context, "reason", "")
        or _last_reply_failure_reason
    )


def authorized_user(open_id: str) -> bool:
    return open_id in ALLOWED_USERS


def workflow_notifications_enabled() -> bool:
    return WORKFLOW_CONFIG.get("enabled") is True


def workflow_configuration_valid() -> bool:
    if not workflow_notifications_enabled():
        return True
    try:
        config_stat = CONFIG_PATH.lstat()
    except OSError:
        return False
    recipient = str(WORKFLOW_CONFIG.get("recipient_open_id") or "").strip()
    chat_id = str(WORKFLOW_CONFIG.get("recipient_chat_id") or "").strip()
    allowed_workflow_id = str(
        WORKFLOW_CONFIG.get("allowed_workflow_id") or ""
    ).strip()
    codex_task_id = str(WORKFLOW_CONFIG.get("codex_task_id") or "").strip()
    try:
        normalized_task_id = str(uuid.UUID(codex_task_id))
    except (ValueError, AttributeError):
        normalized_task_id = ""
    return (
        stat.S_ISREG(config_stat.st_mode)
        and not CONFIG_PATH.is_symlink()
        and config_stat.st_uid == os.getuid()
        and (config_stat.st_mode & 0o777) == 0o600
        and recipient in ALLOWED_USERS
        and (not chat_id or chat_id.startswith("oc_"))
        and allowed_workflow_id == ORI_ONE_WORKFLOW_ID
        and normalized_task_id == codex_task_id.lower()
        and Path(LARK_CLI) == APP_SUPPORT / "lark-cli"
    )


def workflow_recipient() -> tuple[str, str]:
    return (
        str(WORKFLOW_CONFIG.get("recipient_open_id") or "").strip(),
        str(WORKFLOW_CONFIG.get("recipient_chat_id") or "").strip(),
    )


def workflow_allowed_id() -> str:
    return str(WORKFLOW_CONFIG.get("allowed_workflow_id") or "").strip()


def workflow_codex_task_id() -> str:
    return str(WORKFLOW_CONFIG.get("codex_task_id") or "").strip()


def allowed_projects_for(open_id: str) -> set[str]:
    return ALLOWED_USERS.get(open_id, set())


def user_can_access_task(open_id: str, task: dict[str, str]) -> bool:
    projects = allowed_projects_for(open_id)
    return "*" in projects or task["project"] in projects


def state_db_path() -> Path:
    configured = str(CONFIG.get("state_db_path") or "").strip()
    if configured:
        return Path(configured).expanduser()
    preferred = HOME / ".codex/state_5.sqlite"
    if preferred.is_file():
        return preferred
    candidates = sorted(
        (HOME / ".codex").glob("state_*.sqlite"),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    return candidates[0] if candidates else preferred


def log(message: str) -> None:
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    LOG_PATH.parent.chmod(0o700)
    with LOG_PATH.open("a", encoding="utf-8") as handle:
        timestamp = datetime.now().astimezone().isoformat(timespec="seconds")
        handle.write(f"{timestamp} {message.rstrip()}\n")
    LOG_PATH.chmod(0o600)


def load_state() -> dict[str, Any]:
    with _state_lock:
        try:
            state = json.loads(STATE_PATH.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            state = None
        if isinstance(state, dict):
            return state
        return {
            "selected": {},
            "last_lists": {},
            "authorized_chats": {},
            "processed": [],
            "bridge_turns": [],
        }


def save_state(state: dict[str, Any]) -> None:
    with _state_lock:
        STATE_PATH.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
        parent_stat = STATE_PATH.parent.lstat()
        if (
            STATE_PATH.parent.is_symlink()
            or not stat.S_ISDIR(parent_stat.st_mode)
            or parent_stat.st_uid != os.getuid()
        ):
            raise RuntimeError("bridge state directory is unsafe")
        STATE_PATH.parent.chmod(0o700)
        temporary = STATE_PATH.parent / (
            f".{STATE_PATH.name}.{os.getpid()}.{uuid.uuid4().hex}.tmp"
        )
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
        try:
            descriptor = os.open(temporary, flags, 0o600)
            with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
                json.dump(state, handle, ensure_ascii=False, indent=2)
                handle.write("\n")
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temporary, STATE_PATH)
            directory_descriptor = os.open(
                STATE_PATH.parent,
                os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
            )
            try:
                os.fsync(directory_descriptor)
            finally:
                os.close(directory_descriptor)
        finally:
            temporary.unlink(missing_ok=True)


def desktop_project_names() -> dict[str, str]:
    try:
        state = json.loads(DESKTOP_STATE_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    projects = state.get("local-projects", {})
    assignments = state.get("thread-project-assignments", {})
    if not isinstance(projects, dict) or not isinstance(assignments, dict):
        return {}
    names: dict[str, str] = {}
    for thread_id, assignment in assignments.items():
        if (
            not isinstance(assignment, dict)
            or assignment.get("projectKind") != "local"
        ):
            continue
        project = projects.get(assignment.get("projectId"))
        if isinstance(project, dict) and project.get("name"):
            names[str(thread_id)] = str(project["name"])
    return names


def desktop_projects() -> list[dict[str, str]]:
    try:
        state = json.loads(DESKTOP_STATE_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []
    projects = state.get("local-projects", {})
    if not isinstance(projects, dict):
        return []
    result: list[dict[str, str]] = []
    for project in projects.values():
        if not isinstance(project, dict):
            continue
        name = str(project.get("name") or "").strip()
        project_id = str(project.get("id") or "").strip()
        roots = project.get("rootPaths")
        root = next(
            (
                str(Path(str(item)).expanduser().resolve())
                for item in roots
                if str(item).strip() and Path(str(item)).expanduser().is_dir()
            ),
            "",
        ) if isinstance(roots, list) else ""
        if name and project_id and root:
            result.append({"id": project_id, "name": name, "root": root})
    return result


def available_project_names(user_id: str) -> list[str]:
    allowed = allowed_projects_for(user_id)
    return list(
        dict.fromkeys(
            project["name"]
            for project in desktop_projects()
            if "*" in allowed or project["name"] in allowed
        )
    )


def tasks_by_archive_state(
    user_id: str,
    archived: bool,
) -> list[dict[str, str]]:
    state_db = state_db_path()
    connection = sqlite3.connect(
        f"file:{DESKTOP_CATALOG_DB}?mode=ro",
        uri=True,
        timeout=2,
    )
    connection.row_factory = sqlite3.Row
    try:
        connection.execute(
            "ATTACH DATABASE ? AS state",
            (f"file:{state_db}?mode=ro",),
        )
        rows = connection.execute(
            """
            SELECT catalog.thread_id AS id,
                   catalog.display_title AS task_name,
                   catalog.project_id AS project_id,
                   catalog.cwd AS cwd
            FROM local_thread_catalog AS catalog
            JOIN state.threads ON state.threads.id = catalog.thread_id
            WHERE catalog.host_id = 'local'
              AND catalog.missing_candidate = 0
              AND state.threads.archived = ?
              AND state.threads.preview <> ''
            ORDER BY catalog.source_recency_at DESC, catalog.thread_id DESC
            """,
            (int(archived),),
        ).fetchall()
    finally:
        connection.close()
    project_names = desktop_project_names()
    projects = desktop_projects()
    names_by_id = {project["id"]: project["name"] for project in projects}
    names_by_root = {project["root"]: project["name"] for project in projects}
    tasks = [
        {
            "id": str(row["id"]),
            "title": str(row["task_name"]),
            "project": (
                project_names.get(str(row["id"]))
                or names_by_id.get(str(row["project_id"] or ""))
                or (
                    names_by_root.get(str(Path(str(row["cwd"])).resolve()))
                    if row["cwd"]
                    else None
                )
                or "无项目"
            ),
        }
        for row in rows
    ]
    return [task for task in tasks if user_can_access_task(user_id, task)]


def recent_tasks(user_id: str) -> list[dict[str, str]]:
    return tasks_by_archive_state(user_id, archived=False)


def archived_tasks(user_id: str) -> list[dict[str, str]]:
    return tasks_by_archive_state(user_id, archived=True)


def task_by_id(thread_id: str, user_id: str) -> dict[str, str] | None:
    state_db = state_db_path()
    connection = sqlite3.connect(
        f"file:{DESKTOP_CATALOG_DB}?mode=ro",
        uri=True,
        timeout=2,
    )
    connection.row_factory = sqlite3.Row
    try:
        connection.execute(
            "ATTACH DATABASE ? AS state",
            (f"file:{state_db}?mode=ro",),
        )
        row = connection.execute(
            """
            SELECT catalog.thread_id AS id,
                   catalog.display_title AS task_name,
                   catalog.project_id AS project_id,
                   catalog.cwd AS cwd
            FROM local_thread_catalog AS catalog
            JOIN state.threads ON state.threads.id = catalog.thread_id
            WHERE catalog.host_id = 'local'
              AND catalog.thread_id = ?
              AND catalog.missing_candidate = 0
              AND state.threads.archived = 0
            """,
            (thread_id,),
        ).fetchone()
        if row is None:
            row = connection.execute(
                """
                SELECT state.threads.id AS id,
                       COALESCE(
                           NULLIF(state.threads.name, ''),
                           NULLIF(state.threads.title, ''),
                           '未命名 Task'
                       ) AS task_name,
                       state.threads.project_id AS project_id,
                       state.threads.cwd AS cwd
                FROM state.threads
                WHERE state.threads.id = ?
                  AND state.threads.archived = 0
                """,
                (thread_id,),
            ).fetchone()
    finally:
        connection.close()
    if row is None:
        return None
    project_names = desktop_project_names()
    projects = desktop_projects()
    names_by_id = {project["id"]: project["name"] for project in projects}
    names_by_root = {project["root"]: project["name"] for project in projects}
    task = {
        "id": str(row["id"]),
        "title": str(row["task_name"]),
        "project": (
            project_names.get(str(row["id"]))
            or names_by_id.get(str(row["project_id"] or ""))
            or (
                names_by_root.get(str(Path(str(row["cwd"])).resolve()))
                if row["cwd"]
                else None
            )
            or "无项目"
        ),
    }
    return task if user_can_access_task(user_id, task) else None


def rollout_path_for_task(thread_id: str) -> Path | None:
    state_db = state_db_path()
    connection = sqlite3.connect(
        f"file:{state_db}?mode=ro",
        uri=True,
        timeout=2,
    )
    try:
        row = connection.execute(
            "SELECT rollout_path FROM threads WHERE id = ?",
            (thread_id,),
        ).fetchone()
    finally:
        connection.close()
    return Path(str(row[0])) if row and row[0] else None


def task_working_directory(thread_id: str) -> str:
    connection = sqlite3.connect(
        f"file:{state_db_path()}?mode=ro",
        uri=True,
        timeout=2,
    )
    try:
        row = connection.execute(
            "SELECT cwd FROM threads WHERE id = ? AND archived = 0",
            (thread_id,),
        ).fetchone()
    finally:
        connection.close()
    if not row or not row[0]:
        return ""
    path = Path(str(row[0])).expanduser()
    return str(path.resolve()) if path.is_dir() else ""


def version_tuple(version: str) -> tuple[int, int, int] | None:
    match = re.search(r"\b(\d+)\.(\d+)\.(\d+)", version)
    return tuple(map(int, match.groups())) if match else None


def executable_version(executable: str) -> str:
    try:
        result = subprocess.run(
            [executable, "--version"],
            capture_output=True,
            text=True,
            timeout=5,
        )
    except (OSError, subprocess.TimeoutExpired):
        return ""
    return result.stdout.strip().splitlines()[0] if result.returncode == 0 else ""


def rollout_cli_version(rollout_path: Path | None) -> str:
    if rollout_path is None or not rollout_path.is_file():
        return ""
    try:
        with rollout_path.open("r", encoding="utf-8") as handle:
            first_record = json.loads(handle.readline())
    except (OSError, json.JSONDecodeError):
        return ""
    if not isinstance(first_record, dict):
        return ""
    payload = first_record.get("payload")
    if first_record.get("type") != "session_meta" or not isinstance(payload, dict):
        return ""
    return str(payload.get("cli_version") or "").strip()


def cli_resume_preflight(rollout_path: Path | None) -> tuple[bool, str]:
    task_version = rollout_cli_version(rollout_path)
    if not task_version:
        return (
            False,
            "Codex Desktop 当前未连接，且该 task 的本地记录无法由 CLI 安全继续。"
            "请在 Mac 打开 Codex Desktop 后重试；无需删除或重新选择 task。",
        )
    cli_version = executable_version(CODEX_CLI)
    parsed_task_version = version_tuple(task_version)
    parsed_cli_version = version_tuple(cli_version)
    if parsed_task_version is None or parsed_cli_version is None:
        return (
            False,
            "Codex Desktop 当前未连接，且备用 Codex CLI 的兼容版本无法确认。"
            "请在 Mac 打开 Codex Desktop 后重试；无需重新选择 task。",
        )
    if parsed_cli_version < parsed_task_version:
        return (
            False,
            "Codex Desktop 当前未连接，且备用 Codex CLI 版本低于该 task 的记录版本。"
            "请在 Mac 打开 Codex Desktop 后重试。",
        )
    return True, ""


def codex_resume_failure_message(stderr: str) -> str:
    normalized = stderr.lower()
    if any(
        marker in normalized
        for marker in (
            "failed to read thread",
            "thread-store internal error",
            "does not start with session metadata",
        )
    ):
        return (
            "Codex Desktop 当前未连接，且备用 CLI 无法读取这个 task。"
            "请在 Mac 打开 Codex Desktop 后重试；无需重新选择 task。"
        )
    return (
        "没有成功发送到 Codex。请确认 Mac 上的 Codex Desktop 已打开后重试。"
        "详细原因已记录到桥接日志。"
    )


def idempotency_key(message_id: str, kind: str) -> str:
    digest = hashlib.sha256(f"{message_id}:{kind}".encode("utf-8")).hexdigest()[:32]
    return f"codex-bridge-{digest}"


def lark_environment() -> dict[str, str]:
    environment = os.environ.copy()
    environment["LARKSUITE_CLI_NO_UPDATE_NOTIFIER"] = "1"
    environment["LARKSUITE_CLI_NO_SKILLS_NOTIFIER"] = "1"
    return environment


def lark_succeeded(result: subprocess.CompletedProcess[str]) -> bool:
    if result.returncode != 0:
        return False
    try:
        payload = json.loads(result.stdout)
    except json.JSONDecodeError:
        return True
    return not isinstance(payload, dict) or payload.get("ok") is not False


def lark_reply_failure_reason(
    result: subprocess.CompletedProcess[str] | None = None,
    error: BaseException | None = None,
) -> str:
    if isinstance(error, subprocess.TimeoutExpired):
        return "飞书 API 请求超时"
    if isinstance(error, OSError):
        return "本机无法调用 lark-cli"

    raw = ""
    envelope: dict[str, Any] = {}
    if result is not None:
        raw = "\n".join((result.stderr or "", result.stdout or "")).strip()
        for candidate in (result.stderr, result.stdout):
            try:
                payload = json.loads(candidate)
            except (TypeError, json.JSONDecodeError):
                continue
            if isinstance(payload, dict):
                envelope = payload
                break

    details = envelope.get("error") if isinstance(envelope, dict) else None
    if isinstance(details, dict):
        if details.get("missing_scopes") or details.get("subtype") == "missing_scope":
            return "机器人缺少飞书 API 权限"
        error_type = str(details.get("type") or "").strip()
        subtype = str(details.get("subtype") or "").strip()
        if error_type == "authorization":
            return "飞书 API 授权失败"
        if error_type or subtype:
            category = "/".join(value for value in (error_type, subtype) if value)
            return f"飞书 API {category} 错误"

    normalized = raw.lower()
    if any(
        marker in normalized
        for marker in (
            " eof",
            "eof\n",
            "unexpected_eof",
            "connection reset",
            "connection refused",
            "network is unreachable",
            "tls handshake timeout",
            "temporary failure in name resolution",
        )
    ):
        return "飞书 API 网络连接失败"
    if "timeout" in normalized or "timed out" in normalized:
        return "飞书 API 请求超时"
    if result is not None:
        return f"飞书 API 调用失败（退出码 {result.returncode}）"
    return "飞书 API 调用失败"


def lark_reply_failure_metadata(result: subprocess.CompletedProcess[str]) -> str:
    details: dict[str, Any] = {}
    for candidate in (result.stderr, result.stdout):
        try:
            payload = json.loads(candidate)
        except (TypeError, json.JSONDecodeError):
            continue
        error = payload.get("error") if isinstance(payload, dict) else None
        if isinstance(error, dict):
            details = error
            break

    fields = [f"exit_code={result.returncode}"]
    for source, label in (("type", "api_type"), ("subtype", "api_subtype")):
        value = re.sub(r"[^A-Za-z0-9_.-]", "_", str(details.get(source) or ""))[:80]
        if value:
            fields.append(f"{label}={value}")
    api_code = details.get("code")
    if isinstance(api_code, int):
        fields.append(f"api_code={api_code}")
    return " ".join(fields)


def codex_app_server_requests(
    requests: list[
        tuple[
            str,
            dict[str, Any] | Callable[[list[dict[str, Any]]], dict[str, Any]],
        ]
    ],
    timeout: float = 15,
) -> list[dict[str, Any]]:
    process = subprocess.Popen(
        [CODEX_CLI, "app-server", "--listen", "stdio://"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )
    if process.stdin is None or process.stdout is None:
        process.terminate()
        raise RuntimeError("Codex app-server 无法建立输入输出连接。")

    def send(payload: dict[str, Any]) -> None:
        process.stdin.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
        process.stdin.flush()

    def receive(request_id: int) -> dict[str, Any]:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            ready, _, _ = select.select([process.stdout], [], [], 0.5)
            if not ready:
                if process.poll() is not None:
                    break
                continue
            line = process.stdout.readline()
            if not line:
                break
            try:
                payload = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(payload, dict) and payload.get("id") == request_id:
                if payload.get("error"):
                    raise RuntimeError("Codex app-server 拒绝了请求。")
                result = payload.get("result")
                return result if isinstance(result, dict) else {}
        raise RuntimeError("等待 Codex app-server 响应超时。")

    try:
        send(
            {
                "id": 1,
                "method": "initialize",
                "params": {
                    "clientInfo": {
                        "name": "codex-feishu-bridge",
                        "title": "Codex Feishu Bridge",
                        "version": "1",
                    }
                },
            }
        )
        receive(1)
        send({"method": "initialized", "params": {}})
        results: list[dict[str, Any]] = []
        for request_id, (method, params) in enumerate(requests, start=2):
            resolved_params = params(results) if callable(params) else params
            send({"id": request_id, "method": method, "params": resolved_params})
            results.append(receive(request_id))
        return results
    finally:
        process.terminate()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=3)


def create_codex_task(user_id: str, project_name: str, title: str) -> dict[str, str]:
    projects = allowed_projects_for(user_id)
    if "*" not in projects and project_name not in projects:
        raise RuntimeError("你没有在这个项目中新建 Task 的权限。")
    project = next(
        (item for item in desktop_projects() if item["name"] == project_name),
        None,
    )
    if project is None:
        raise RuntimeError("找不到该项目的本地目录，请在 Codex Desktop 中检查项目。")
    clean_title = " ".join(title.split())[:80]
    if not clean_title:
        raise RuntimeError("Task 标题不能为空。")
    def name_params(results: list[dict[str, Any]]) -> dict[str, Any]:
        thread = results[0].get("thread") if results else None
        thread_id = str(thread.get("id") or "") if isinstance(thread, dict) else ""
        try:
            uuid.UUID(thread_id)
        except ValueError as exc:
            raise RuntimeError("Codex 没有返回有效的 Task 标识。") from exc
        return {"threadId": thread_id, "name": clean_title}

    results = codex_app_server_requests(
        [
            (
                "thread/start",
                {
                    "cwd": project["root"],
                    "ephemeral": False,
                },
            ),
            ("thread/name/set", name_params),
        ]
    )
    thread = results[0].get("thread") if results else None
    thread_id = str(thread.get("id") or "") if isinstance(thread, dict) else ""
    try:
        uuid.UUID(thread_id)
    except ValueError as exc:
        raise RuntimeError("Codex 没有返回有效的 Task 标识。") from exc
    return {"id": thread_id, "title": clean_title, "project": project_name}


def archive_codex_task(user_id: str, task: dict[str, str]) -> None:
    if not user_can_access_task(user_id, task):
        raise RuntimeError("你没有归档这个 Task 的权限。")
    codex_app_server_requests(
        [("thread/archive", {"threadId": str(task["id"])})]
    )


def restore_codex_task(user_id: str, task: dict[str, str]) -> None:
    if not user_can_access_task(user_id, task):
        raise RuntimeError("你没有恢复这个 Task 的权限。")
    codex_app_server_requests(
        [("thread/unarchive", {"threadId": str(task["id"])})]
    )


def complete_task_creation(
    message_id: str,
    user_id: str,
    project_name: str,
    title: str,
) -> None:
    try:
        task = create_codex_task(user_id, project_name, title)
    except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
        log(f"task creation failed error={type(exc).__name__}")
        reason = str(exc)
        if reason.startswith(("你没有", "找不到", "Task 标题")):
            message = reason
        elif isinstance(exc, RuntimeError):
            message = (
                "Codex Desktop 没有接受新建 Task 请求。"
                "桥接协议可能不兼容或 Desktop 暂不可用，请更新桥接后重试。"
            )
        else:
            message = "没有成功新建 Task，请在 Mac 上确认 Codex Desktop 正在运行后重试。"
        reply(
            message_id,
            message,
            "new-task-error",
        )
        return
    with _state_lock:
        state = load_state()
        state.setdefault("selected", {})[user_id] = task["id"]
        state.setdefault("last_projects", {})[user_id] = task["project"]
        state.setdefault("task_pages", {})[user_id] = 0
        state.setdefault("task_queries", {}).pop(user_id, None)
        state.setdefault("pending_task_names", {})[user_id] = {
            "task_id": task["id"],
            "title": task["title"],
        }
        save_state(state)
    reply(
        message_id,
        f"{current_task_changed_text(task, '已新建')}\n现在发送的下一条消息会进入这个 Task。",
        "new-task-created",
    )


def restore_pending_task_name(user_id: str, task_id: str) -> bool:
    with _state_lock:
        state = load_state()
        pending = state.setdefault("pending_task_names", {}).get(user_id)
        if (
            not isinstance(pending, dict)
            or str(pending.get("task_id") or "") != task_id
        ):
            return False
        title = str(pending.get("title") or "").strip()
    if not title:
        return False
    try:
        codex_app_server_requests(
            [("thread/name/set", {"threadId": task_id, "name": title})]
        )
    except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
        log(f"task name restore failed error={type(exc).__name__}")
        return False
    with _state_lock:
        state = load_state()
        current = state.setdefault("pending_task_names", {}).get(user_id)
        if isinstance(current, dict) and str(current.get("task_id") or "") == task_id:
            state["pending_task_names"].pop(user_id, None)
            save_state(state)
    return True


def reply(message_id: str, text: str, kind: str) -> bool:
    set_reply_failure_reason("")
    content = text.strip() or "Codex 没有返回文字结果。"
    if len(content) > MAX_REPLY_CHARS:
        content = content[:MAX_REPLY_CHARS].rstrip() + "…"
    reply_key = idempotency_key(message_id, kind)
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-reply",
        "--message-id",
        message_id,
        "--text",
        content,
        "--as",
        "bot",
        "--idempotency-key",
        reply_key,
    ]
    attempts = len(REPLY_RETRY_DELAYS) + 1
    for attempt in range(1, attempts + 1):
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=30,
                env=lark_environment(),
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            set_reply_failure_reason(lark_reply_failure_reason(error=exc))
            log(
                f"reply failed kind={kind} attempt={attempt} "
                f"reason={current_reply_failure_reason()}"
            )
        else:
            if lark_succeeded(result):
                set_reply_failure_reason("")
                return True
            set_reply_failure_reason(lark_reply_failure_reason(result=result))
            log(
                f"reply failed kind={kind} attempt={attempt} "
                f"{lark_reply_failure_metadata(result)} "
                f"reason={current_reply_failure_reason()}"
            )
        if attempt < attempts:
            time.sleep(REPLY_RETRY_DELAYS[attempt - 1])
    return False


def pending_reply_delay(attempts: int) -> int:
    index = min(max(attempts, 0), len(PENDING_REPLY_DELAYS) - 1)
    return PENDING_REPLY_DELAYS[index]


def queue_pending_reply(
    message_id: str,
    text: str,
    kind: str,
    reason: str,
    now: float | None = None,
) -> None:
    with _state_lock:
        state = load_state()
        pending = state.setdefault("pending_replies", [])
        if not isinstance(pending, list):
            pending = []
        timestamp = time.time() if now is None else now
        entry = {
            "message_id": message_id,
            "text": text,
            "kind": kind,
            "reason": reason or "飞书 API 调用失败",
            "attempts": 0,
            "created_at": timestamp,
            "next_attempt_at": timestamp + pending_reply_delay(0),
        }
        pending = [
            item
            for item in pending
            if not (
                isinstance(item, dict)
                and item.get("message_id") == message_id
                and item.get("kind") == kind
            )
        ]
        pending.append(entry)
        state["pending_replies"] = pending[-MAX_PENDING_REPLIES:]
        save_state(state)
    log(f"reply queued kind={kind} reason={entry['reason']}")


def queue_pending_card_patch(
    message_id: str,
    card: dict[str, Any],
    reason: str,
    now: float | None = None,
) -> None:
    with _state_lock:
        state = load_state()
        pending = state.setdefault("pending_replies", [])
        if not isinstance(pending, list):
            pending = []
        timestamp = time.time() if now is None else now
        existing = next(
            (
                item
                for item in pending
                if isinstance(item, dict)
                and item.get("operation") == "card_patch"
                and item.get("message_id") == message_id
            ),
            None,
        )
        entry = {
            "operation": "card_patch",
            "message_id": message_id,
            "card": card,
            "reason": reason or "飞书 API 调用失败",
            "attempts": int(existing.get("attempts") or 0) if existing else 0,
            "created_at": float(existing.get("created_at") or timestamp) if existing else timestamp,
            "next_attempt_at": (
                float(existing.get("next_attempt_at") or timestamp)
                if existing
                else timestamp + pending_reply_delay(0)
            ),
        }
        pending = [item for item in pending if item is not existing]
        pending.append(entry)
        state["pending_replies"] = pending[-MAX_PENDING_REPLIES:]
        save_state(state)
    log(f"card patch queued reason={entry['reason']}")


def clear_pending_card_patch(message_id: str) -> None:
    with _state_lock:
        state = load_state()
        pending = state.get("pending_replies")
        if not isinstance(pending, list):
            return
        kept = [
            item
            for item in pending
            if not (
                isinstance(item, dict)
                and item.get("operation") == "card_patch"
                and item.get("message_id") == message_id
            )
        ]
        if len(kept) != len(pending):
            state["pending_replies"] = kept
            save_state(state)


def queue_pending_queue_card(
    queue_id: str,
    message_id: str,
    reason: str,
    now: float | None = None,
) -> None:
    with _state_lock:
        state = load_state()
        pending = state.setdefault("pending_replies", [])
        if not isinstance(pending, list):
            pending = []
        timestamp = time.time() if now is None else now
        entry = {
            "operation": "queue_card",
            "queue_id": queue_id,
            "message_id": message_id,
            "reason": reason or "飞书 API 调用失败",
            "attempts": 0,
            "created_at": timestamp,
            "next_attempt_at": timestamp + pending_reply_delay(0),
        }
        pending = [
            item
            for item in pending
            if not (
                isinstance(item, dict)
                and item.get("operation") == "queue_card"
                and item.get("queue_id") == queue_id
            )
        ]
        pending.append(entry)
        state["pending_replies"] = pending[-MAX_PENDING_REPLIES:]
        save_state(state)
    log(f"queue card queued reason={entry['reason']}")


def pending_image_spool_directory() -> Path:
    return STATE_PATH.parent / "reply-images"


def remove_pending_image_file(item: dict[str, Any]) -> None:
    if item.get("operation") != "image_reply" or item.get("remote") is True:
        return
    raw_path = str(item.get("image") or "")
    if not raw_path:
        return
    path = Path(raw_path)
    try:
        path.resolve().relative_to(pending_image_spool_directory().resolve())
        path.unlink(missing_ok=True)
    except (OSError, ValueError):
        return


def queue_pending_image(
    message_id: str,
    image: str,
    index: int,
    reason: str,
    now: float | None = None,
) -> bool:
    parsed = urlsplit(image)
    remote = parsed.scheme.lower() in {"http", "https"}
    stored_image = image
    if not remote:
        source = Path(image).resolve()
        try:
            size = source.stat().st_size
        except OSError:
            return False
        if (
            source.suffix.lower() not in IMAGE_SUFFIXES
            or size <= 0
            or size > MAX_PENDING_IMAGE_BYTES
        ):
            return False
        spool = pending_image_spool_directory()
        spool.mkdir(parents=True, exist_ok=True)
        spool.chmod(0o700)
        digest = hashlib.sha256(
            f"{message_id}:{index}".encode("utf-8")
        ).hexdigest()[:32]
        target = spool / f"{digest}{source.suffix.lower()}"
        temporary = target.with_suffix(target.suffix + ".tmp")
        try:
            shutil.copy2(source, temporary)
            temporary.replace(target)
            target.chmod(0o600)
        except OSError:
            temporary.unlink(missing_ok=True)
            return False
        stored_image = str(target)

    with _state_lock:
        state = load_state()
        pending = state.setdefault("pending_replies", [])
        if not isinstance(pending, list):
            pending = []
        timestamp = time.time() if now is None else now
        replaced = [
            item
            for item in pending
            if isinstance(item, dict)
            and item.get("operation") == "image_reply"
            and item.get("message_id") == message_id
            and int(item.get("index") or 0) == index
        ]
        for item in replaced:
            if str(item.get("image") or "") != stored_image:
                remove_pending_image_file(item)
        pending = [item for item in pending if item not in replaced]
        pending.append(
            {
                "operation": "image_reply",
                "message_id": message_id,
                "image": stored_image,
                "remote": remote,
                "index": index,
                "reason": reason or "飞书 API 调用失败",
                "attempts": 0,
                "created_at": timestamp,
                "next_attempt_at": timestamp + pending_reply_delay(0),
            }
        )
        if not remote:
            local_items = [
                item
                for item in pending
                if isinstance(item, dict)
                and item.get("operation") == "image_reply"
                and item.get("remote") is not True
            ]
            total = sum(
                Path(str(item.get("image") or "")).stat().st_size
                if Path(str(item.get("image") or "")).is_file()
                else 0
                for item in local_items
            )
            while total > MAX_PENDING_IMAGE_SPOOL_BYTES and local_items:
                oldest = local_items.pop(0)
                try:
                    total -= Path(str(oldest.get("image") or "")).stat().st_size
                except OSError:
                    pass
                remove_pending_image_file(oldest)
                pending.remove(oldest)
        while len(pending) > MAX_PENDING_REPLIES:
            removed = pending.pop(0)
            if isinstance(removed, dict):
                remove_pending_image_file(removed)
        state["pending_replies"] = pending
        save_state(state)
    log(f"image reply queued index={index} reason={reason or '飞书 API 调用失败'}")
    return True


def reply_or_queue(message_id: str, text: str, kind: str) -> bool:
    delivered = reply(message_id, text, kind)
    if not delivered and kind == "final":
        queue_pending_reply(
            message_id,
            text,
            kind,
            current_reply_failure_reason() or "飞书 API 调用失败",
        )
    return delivered


def retry_pending_replies(now: float | None = None) -> bool:
    global _last_reply_failure_reason

    timestamp = time.time() if now is None else now
    with _state_lock:
        state = load_state()
        pending = state.get("pending_replies")
        if not isinstance(pending, list):
            return False
        for index, item in enumerate(pending):
            if not isinstance(item, dict):
                continue
            try:
                next_attempt_at = float(item.get("next_attempt_at") or 0)
            except (TypeError, ValueError):
                next_attempt_at = 0
            if next_attempt_at > timestamp:
                continue
            operation = str(item.get("operation") or "text_reply")
            message_id = str(item.get("message_id") or "")
            if operation == "card_patch":
                card = item.get("card")
                if not message_id or not isinstance(card, dict):
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    return True
                if patch_card(message_id, card, persist=False):
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    log("pending card patch delivered")
                    return True
            elif operation == "queue_card":
                queue_id = str(item.get("queue_id") or "")
                queued_entry = next(
                    (
                        entry
                        for entry in pending_inputs(state)
                        if isinstance(entry, dict)
                        and str(entry.get("queue_id") or "") == queue_id
                    ),
                    None,
                )
                if not message_id or queued_entry is None:
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    return True
                task_id = str(queued_entry.get("task", {}).get("id") or "")
                position = queued_position(pending_inputs(state), task_id, queue_id)
                try:
                    delivered, progress_message_id = reply_card_message(
                        message_id,
                        build_queued_card(queued_entry, position),
                        f"queue-{queue_id}",
                    )
                except (OSError, subprocess.TimeoutExpired):
                    delivered, progress_message_id = False, None
                if delivered and progress_message_id:
                    queued_entry["progress_message_id"] = progress_message_id
                    queued_entry["ready"] = True
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    log("pending queue card delivered")
                    return True
            elif operation == "image_reply":
                image = str(item.get("image") or "")
                try:
                    image_index = int(item.get("index") or 0)
                except (TypeError, ValueError):
                    image_index = 0
                if (
                    not message_id
                    or not image
                    or image_index <= 0
                    or (
                        item.get("remote") is not True
                        and not Path(image).is_file()
                    )
                ):
                    remove_pending_image_file(item)
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    return True
                if reply_image(message_id, image, image_index):
                    reason = str(item.get("reason") or "飞书 API 调用失败")
                    remove_pending_image_file(item)
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    log(
                        f"pending image delivered index={image_index} "
                        f"previous_reason={reason}"
                    )
                    return True
            elif operation != "text_reply":
                pending.pop(index)
                state["pending_replies"] = pending
                save_state(state)
                return True

            text = str(item.get("text") or "")
            kind = str(item.get("kind") or "final")
            if operation == "text_reply" and (
                not message_id
                or not text
                or kind not in {"final", "workflow-choice"}
            ):
                pending.pop(index)
                state["pending_replies"] = pending
                save_state(state)
                return True
            if operation == "text_reply" and reply(message_id, text, kind):
                reason = str(item.get("reason") or "飞书 API 调用失败")
                pending.pop(index)
                state["pending_replies"] = pending
                save_state(state)
                log(f"pending reply delivered kind={kind} previous_reason={reason}")
                if kind == "final":
                    reply(
                        message_id,
                        f"上一条结果曾因{reason}未能及时送达，连接恢复后已自动补发。",
                        f"{kind}-recovered",
                    )
                return True
            try:
                attempts = int(item.get("attempts") or 0) + 1
            except (TypeError, ValueError):
                attempts = 1
            item["attempts"] = attempts
            if operation in {"text_reply", "image_reply"}:
                item["reason"] = _last_reply_failure_reason or item.get("reason")
            item["next_attempt_at"] = timestamp + pending_reply_delay(attempts)
            state["pending_replies"] = pending
            save_state(state)
            log(
                f"pending reply retry failed kind={kind} attempts={attempts} "
                f"reason={item['reason']}"
            )
            return True
    return False


def input_image_keys(content: str) -> list[str]:
    keys: list[str] = []
    try:
        payload = json.loads(content)
    except json.JSONDecodeError:
        payload = None
    if isinstance(payload, dict):
        key = str(payload.get("image_key") or "")
        if re.fullmatch(IMAGE_KEY_PATTERN, key):
            keys.append(key)
    for match in INPUT_IMAGE_MARKER_PATTERN.finditer(content):
        key = next((group for group in match.groups() if group), "")
        if key and key not in keys:
            keys.append(key)
    return keys


def input_file_keys(content: str) -> list[str]:
    return list(dict.fromkeys(INPUT_FILE_MARKER_PATTERN.findall(content)))


def input_file_label(content: str, file_key: str, index: int) -> str:
    try:
        payload = json.loads(content)
    except json.JSONDecodeError:
        payload = None
    if isinstance(payload, dict):
        for key in ("file_name", "name", "filename"):
            value = payload.get(key)
            if isinstance(value, str):
                label = Path(value.strip()).name
                if label and label not in {".", ".."}:
                    return label
    patterns = (
        rf"File:\s*([^\n()]{{1,200}})\s*\(\s*{re.escape(file_key)}\s*\)",
        rf"\[File:\s*([^\]\n]{{1,200}})\][^\n]*{re.escape(file_key)}",
        rf"([^\s/\\]{{1,200}}\.[A-Za-z0-9]{{1,12}})[^\n]*{re.escape(file_key)}",
    )
    for pattern in patterns:
        match = re.search(pattern, content, re.IGNORECASE)
        if match:
            label = Path(match.group(1).strip()).name
            if label and label not in {".", ".."}:
                return label
    return f"附件-{index}"


def input_prompt(
    content: str,
    image_keys: list[str],
    file_keys: list[str] | None = None,
    message_type: str = "",
) -> str:
    file_keys = file_keys or []
    try:
        payload = json.loads(content)
    except json.JSONDecodeError:
        payload = None
    is_resource_payload = isinstance(payload, dict) and (
        payload.get("image_key") or payload.get("file_key")
    )
    text = "" if is_resource_payload or message_type in {"file", "audio", "media"} else content
    text = INPUT_IMAGE_MARKER_PATTERN.sub("", text)
    text = INPUT_FILE_MARKER_PATTERN.sub("", text)
    normalized = normalized_content(text)
    if normalized:
        return normalized
    if image_keys and file_keys:
        return "用户从飞书发送了以下图片和文件。"
    if image_keys:
        return "用户从飞书发送了以下图片。"
    if file_keys:
        return "用户从飞书发送了以下音频。" if message_type == "audio" else "用户从飞书发送了以下文件。"
    return ""


def detected_image_suffix(path: Path) -> str:
    try:
        with path.open("rb") as handle:
            header = handle.read(12)
    except OSError:
        return ""
    if header.startswith(b"\x89PNG\r\n\x1a\n"):
        return ".png"
    if header.startswith(b"\xff\xd8\xff"):
        return ".jpg"
    if header.startswith((b"GIF87a", b"GIF89a")):
        return ".gif"
    if header.startswith(b"RIFF") and header[8:12] == b"WEBP":
        return ".webp"
    return ""


def downloaded_image_path(
    stdout: str,
    directory: Path,
    index: int,
) -> tuple[Path | None, str]:
    try:
        payload = json.loads(stdout)
    except json.JSONDecodeError:
        return None, "飞书没有返回有效的图片下载结果。"
    if not isinstance(payload, dict) or payload.get("ok") is not True:
        return None, "飞书没有确认图片下载成功。"
    data = payload.get("data")
    saved_path = str(data.get("saved_path") or "") if isinstance(data, dict) else ""
    if not saved_path:
        return None, "飞书没有返回图片保存路径。"
    path = Path(saved_path)
    if not path.is_absolute():
        path = directory / path
    try:
        path = path.resolve()
        path.relative_to(directory.resolve())
        size = path.stat().st_size
    except (OSError, ValueError):
        return None, "图片下载位置不安全或文件不存在。"
    if size == 0:
        return None, "收到的图片文件为空。"
    if size > MAX_INPUT_IMAGE_BYTES:
        return None, f"图片超过 {MAX_INPUT_IMAGE_BYTES // (1024 * 1024)} MB 限制。"
    suffix = detected_image_suffix(path)
    if suffix not in IMAGE_SUFFIXES:
        return None, "当前仅支持 PNG、JPEG、GIF 和 WebP 图片。"
    normalized = directory / f"input-{index}{suffix}"
    if path != normalized:
        try:
            path.replace(normalized)
        except OSError:
            return None, "无法准备下载后的图片。"
    return normalized.resolve(), ""


def image_download_failure(stderr: str) -> tuple[bool, str]:
    try:
        payload = json.loads(stderr)
    except json.JSONDecodeError:
        return True, "无法从飞书读取这张图片，请重新发送。"
    error = payload.get("error") if isinstance(payload, dict) else None
    if not isinstance(error, dict):
        return False, "无法从飞书读取这张图片，请重新发送。"
    if error.get("missing_scopes"):
        return (
            False,
            "机器人缺少读取图片资源的权限，请联系这台 Mac 的管理员处理。",
        )
    return (
        error.get("retryable") is True,
        "这张飞书图片当前无法下载，请重新发送。",
    )


def download_input_image(
    message_id: str,
    image_key: str,
    directory: Path,
    index: int,
) -> tuple[Path | None, str]:
    if not re.fullmatch(IMAGE_KEY_PATTERN, image_key):
        return None, "图片资源标识无效。"
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-resources-download",
        "--message-id",
        message_id,
        "--file-key",
        image_key,
        "--type",
        "image",
        "--output",
        f"./download-{index}",
        "--as",
        "bot",
        "--json",
    ]
    for attempt in range(1, 3):
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=60,
                env=lark_environment(),
                cwd=directory,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            log(
                f"image download failed index={index} attempt={attempt} "
                f"error={type(exc).__name__}"
            )
            continue
        if result.returncode == 0:
            path, error = downloaded_image_path(result.stdout, directory, index)
            if path is not None:
                return path, ""
            log(f"image download invalid index={index} attempt={attempt}")
            return None, error
        log(
            f"image download failed index={index} attempt={attempt} "
            f"code={result.returncode}"
        )
        retryable, message = image_download_failure(result.stderr)
        if not retryable:
            return None, message
    return (
        None,
        "无法从飞书读取这张图片。请确认机器人具有消息读取权限后重新发送。",
    )


def downloaded_file_path(
    stdout: str,
    directory: Path,
    index: int,
    label: str,
    message_type: str,
) -> tuple[dict[str, str] | None, str]:
    try:
        payload = json.loads(stdout)
    except json.JSONDecodeError:
        return None, "飞书没有返回有效的文件下载结果。"
    data = payload.get("data") if isinstance(payload, dict) else None
    saved_path = str(data.get("saved_path") or "") if isinstance(data, dict) else ""
    if not isinstance(payload, dict) or payload.get("ok") is not True or not saved_path:
        return None, "飞书没有确认文件下载成功。"
    path = Path(saved_path)
    if not path.is_absolute():
        path = directory / path
    try:
        path = path.resolve()
        path.relative_to(directory.resolve())
        size = path.stat().st_size
    except (OSError, ValueError):
        return None, "文件下载位置不安全或文件不存在。"
    if size == 0:
        return None, "收到的文件为空。"
    if size > MAX_INPUT_FILE_BYTES:
        return None, f"文件超过 {MAX_INPUT_FILE_BYTES // (1024 * 1024)} MB 限制。"
    suffix = path.suffix.lower() or Path(label).suffix.lower()
    if not suffix and message_type == "audio":
        suffix = ".opus"
    if suffix not in FILE_SUFFIXES:
        return None, "当前不支持该文件类型；暂不自动处理压缩包或可执行文件。"
    safe_stem = re.sub(r"[^A-Za-z0-9._-]", "_", Path(label).stem).strip("._")
    normalized = directory / f"input-{index}-{safe_stem or 'file'}{suffix}"
    if path != normalized:
        try:
            path.replace(normalized)
        except OSError:
            return None, "无法准备下载后的文件。"
    return {
        "path": str(normalized.resolve()),
        "label": Path(label).name if Path(label).suffix else normalized.name,
        "kind": "audio" if message_type == "audio" or suffix in AUDIO_SUFFIXES else "file",
    }, ""


def download_input_file(
    message_id: str,
    file_key: str,
    directory: Path,
    index: int,
    label: str,
    message_type: str,
) -> tuple[dict[str, str] | None, str]:
    if not re.fullmatch(FILE_KEY_PATTERN, file_key):
        return None, "文件资源标识无效。"
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-resources-download",
        "--message-id",
        message_id,
        "--file-key",
        file_key,
        "--type",
        "file",
        "--as",
        "bot",
        "--json",
    ]
    for attempt in range(1, 3):
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=120,
                env=lark_environment(),
                cwd=directory,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            log(
                f"file download failed index={index} attempt={attempt} "
                f"error={type(exc).__name__}"
            )
            continue
        if result.returncode == 0:
            attachment, error = downloaded_file_path(
                result.stdout,
                directory,
                index,
                label,
                message_type,
            )
            return attachment, error
        retryable, message = image_download_failure(result.stderr)
        log(f"file download failed index={index} attempt={attempt} code={result.returncode}")
        if not retryable:
            return None, message.replace("图片", "文件")
    return None, "无法从飞书读取这个文件，请重新发送。"


def codex_turn_input(
    prompt: str,
    input_images: list[str],
    input_files: list[dict[str, str]] | None = None,
) -> list[dict[str, Any]]:
    items: list[dict[str, Any]] = [
        {"type": "text", "text": prompt, "text_elements": []}
    ]
    items.extend(
        {"type": "localImage", "path": str(Path(image).resolve())}
        for image in input_images
    )
    for attachment in input_files or []:
        if attachment.get("kind") == "audio":
            items.append(
                {"type": "localAudio", "path": str(Path(attachment["path"]).resolve())}
            )
        else:
            items.append(
                {
                    "type": "mention",
                    "name": attachment["label"],
                    "path": str(Path(attachment["path"]).resolve()),
                }
            )
    return items


def codex_attachments(input_files: list[dict[str, str]]) -> list[dict[str, str]]:
    return [
        {
            "label": attachment["label"],
            "path": str(Path(attachment["path"]).resolve()),
            "fsPath": str(Path(attachment["path"]).resolve()),
        }
        for attachment in input_files
    ]


def normalized_image_reference(reference: str) -> str | None:
    value = reference.strip()
    if value.startswith("<") and value.endswith(">"):
        value = value[1:-1].strip()
    else:
        value = re.split(r"\s+(?=[\"'])", value, maxsplit=1)[0].strip()

    parsed = urlsplit(value)
    if parsed.scheme.lower() in {"http", "https"}:
        return value if Path(parsed.path).suffix.lower() in IMAGE_SUFFIXES else None
    if parsed.scheme.lower() == "file":
        value = unquote(parsed.path)
    elif parsed.scheme:
        return None

    path = Path(value).expanduser()
    if (
        not path.is_absolute()
        or path.suffix.lower() not in IMAGE_SUFFIXES
        or not path.is_file()
    ):
        return None
    return str(path.resolve())


def extract_result_images(text: str) -> tuple[str, list[str]]:
    images: list[str] = []

    def replace(match: re.Match[str]) -> str:
        image = normalized_image_reference(match.group(1))
        if image is None:
            return "图片不可用"
        if image not in images:
            images.append(image)
        return "图片见下方"

    return MARKDOWN_IMAGE_PATTERN.sub(replace, text).strip(), images


def prepare_result_images(
    text: str,
    rollout_images: list[str],
) -> tuple[str, list[str]]:
    clean_text, linked_images = extract_result_images(text)
    if MAX_RESULT_IMAGES == 0:
        return clean_text, []
    images: list[str] = []
    for reference in rollout_images + linked_images:
        image = normalized_image_reference(reference)
        if image is not None and image not in images:
            images.append(image)
        if len(images) >= MAX_RESULT_IMAGES:
            break
    if images and "图片见下方" not in clean_text:
        clean_text = clean_text.rstrip() + "\n\n图片见下方。"
    return clean_text, images


def reply_image(message_id: str, image: str, index: int) -> bool:
    set_reply_failure_reason("")
    parsed = urlsplit(image)
    cwd: Path | None = None
    image_argument = image
    if parsed.scheme.lower() not in {"http", "https"}:
        path = Path(image).resolve()
        cwd = path.parent
        image_argument = f"./{path.name}"
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-reply",
        "--message-id",
        message_id,
        "--image",
        image_argument,
        "--as",
        "bot",
        "--idempotency-key",
        idempotency_key(message_id, f"image-{index}"),
    ]
    for attempt in range(1, 3):
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=60,
                env=lark_environment(),
                cwd=cwd,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            set_reply_failure_reason(lark_reply_failure_reason(error=exc))
            log(
                f"image reply failed index={index} attempt={attempt} "
                f"reason={current_reply_failure_reason()}"
            )
            continue
        if lark_succeeded(result):
            set_reply_failure_reason("")
            return True
        set_reply_failure_reason(lark_reply_failure_reason(result=result))
        log(
            f"image reply failed index={index} attempt={attempt} "
            f"{lark_reply_failure_metadata(result)} "
            f"reason={current_reply_failure_reason()}"
        )
    return False


def sent_message_id(stdout: str) -> str | None:
    try:
        payload = json.loads(stdout)
    except json.JSONDecodeError:
        return None
    data = payload.get("data") if isinstance(payload, dict) else None
    if not isinstance(data, dict):
        return None
    message = data.get("message")
    candidates = [
        data.get("message_id"),
        message.get("message_id") if isinstance(message, dict) else None,
        message.get("id") if isinstance(message, dict) else None,
    ]
    return next(
        (
            str(candidate)
            for candidate in candidates
            if isinstance(candidate, str) and candidate.startswith("om_")
        ),
        None,
    )


def reply_card_message(
    message_id: str,
    card: dict[str, Any],
    kind: str,
) -> tuple[bool, str | None]:
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-reply",
        "--message-id",
        message_id,
        "--msg-type",
        "interactive",
        "--content",
        json.dumps(card, ensure_ascii=False, separators=(",", ":")),
        "--as",
        "bot",
        "--idempotency-key",
        idempotency_key(message_id, kind),
    ]
    result = subprocess.run(
        command,
        capture_output=True,
        text=True,
        timeout=30,
        env=lark_environment(),
    )
    if not lark_succeeded(result):
        log(f"card reply failed kind={kind} code={result.returncode}")
        return False, None
    return True, sent_message_id(result.stdout)


def reply_card(message_id: str, card: dict[str, Any], kind: str) -> bool:
    return reply_card_message(message_id, card, kind)[0]


def patch_card(
    message_id: str,
    card: dict[str, Any],
    persist: bool = True,
) -> bool:
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "messages",
        "patch",
        "--message-id",
        message_id,
        "--data",
        json.dumps(
            {
                "content": json.dumps(
                    card,
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
            },
            ensure_ascii=False,
            separators=(",", ":"),
        ),
        "--as",
        "bot",
    ]
    attempts = len(CARD_PATCH_RETRY_DELAYS) + 1
    failure_reason = "飞书 API 调用失败"
    for attempt in range(1, attempts + 1):
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=30,
                env=lark_environment(),
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            failure_reason = lark_reply_failure_reason(error=exc)
            log(
                f"card patch failed attempt={attempt} "
                f"reason={failure_reason}"
            )
        else:
            if lark_succeeded(result):
                if persist:
                    clear_pending_card_patch(message_id)
                return True
            failure_reason = lark_reply_failure_reason(result)
            log(
                f"card patch failed attempt={attempt} "
                f"{lark_reply_failure_metadata(result)} "
                f"reason={failure_reason}"
            )
        if attempt < attempts:
            time.sleep(CARD_PATCH_RETRY_DELAYS[attempt - 1])
    if persist:
        queue_pending_card_patch(message_id, card, failure_reason)
    return False


def sent_chat_id(stdout: str) -> str | None:
    try:
        payload = json.loads(stdout)
    except json.JSONDecodeError:
        return None
    data = payload.get("data") if isinstance(payload, dict) else None
    if not isinstance(data, dict):
        return None
    message = data.get("message")
    candidates = [
        data.get("chat_id"),
        message.get("chat_id") if isinstance(message, dict) else None,
    ]
    return next(
        (
            str(candidate)
            for candidate in candidates
            if isinstance(candidate, str) and candidate.startswith("oc_")
        ),
        None,
    )


def send_card(
    user_id: str,
    card: dict[str, Any],
    kind: str,
) -> tuple[bool, str | None, str | None]:
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-send",
        "--user-id",
        user_id,
        "--msg-type",
        "interactive",
        "--content",
        json.dumps(card, ensure_ascii=False, separators=(",", ":")),
        "--as",
        "bot",
        "--idempotency-key",
        idempotency_key(kind, "task-card"),
    ]
    result = subprocess.run(
        command,
        capture_output=True,
        text=True,
        timeout=30,
        env=lark_environment(),
    )
    if not lark_succeeded(result):
        log(f"card send failed kind={kind} code={result.returncode}")
        return False, None, None
    return True, sent_chat_id(result.stdout), sent_message_id(result.stdout)


def update_card(token: str, card: dict[str, Any]) -> bool:
    payload = json.dumps(
        {"token": token, "card": card},
        ensure_ascii=False,
        separators=(",", ":"),
    )
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "api",
        "POST",
        "/open-apis/interactive/v1/card/update",
        "--as",
        "bot",
        "--data",
        payload,
    ]
    result = subprocess.run(
        command,
        capture_output=True,
        text=True,
        timeout=30,
        env=lark_environment(),
    )
    if not lark_succeeded(result):
        log(f"card update failed code={result.returncode}")
        return False
    return True


def build_workflow_card(
    record: dict[str, Any],
    reminder: bool = False,
    completed: bool = False,
) -> dict[str, Any]:
    requires_action = record.get("status") == "user_action_required"
    selected = record.get("selected_action")
    title = str(record.get("task_id") or "Ori One Mind")[:128]
    summary = card_markdown_escape(str(record.get("summary") or "")[:2000])
    elements: list[dict[str, Any]] = [
        {"tag": "markdown", "content": f"**摘要**\n{summary}"},
        {
            "tag": "button",
            "text": {"tag": "plain_text", "content": "打开执行工作台"},
            "type": "default",
            "behaviors": [
                {
                    "type": "open_url",
                    "default_url": str(record.get("workbench_url") or ""),
                }
            ],
        },
    ]
    if requires_action and not completed:
        elements.insert(
            1,
            {
                "tag": "markdown",
                "content": (
                    "请选择一个方案。推荐项已标出；每个请求只能处理一次。"
                    if not reminder
                    else "这是 24 小时后的唯一一次提醒。请选择一个方案。"
                ),
            },
        )
        callback_base = {
            "action": "workflow_decision",
            "workflow_id": str(record.get("workflow_id") or ""),
            "event_id": str(record.get("event_id") or ""),
            "decision_token": str(record.get("decision_token") or ""),
        }
        for action in record.get("actions", []):
            if not isinstance(action, dict):
                continue
            recommended = action.get("recommended") is True
            label = str(action.get("label") or "")
            description = card_markdown_escape(str(action.get("description") or "")[:500])
            elements.append(
                {
                    "tag": "markdown",
                    "content": (
                        f"**{card_markdown_escape(label)}"
                        f"{'（推荐）' if recommended else ''}**\n{description}"
                    ),
                }
            )
            elements.append(
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": label[:80]},
                    "type": "primary_filled" if recommended else "default",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                **callback_base,
                                "action_id": str(action.get("id") or ""),
                            },
                        }
                    ],
                }
            )
    elif requires_action and completed:
        action_label = (
            str(selected.get("label") or "")
            if isinstance(selected, dict)
            else "已处理"
        )
        elements.insert(
            1,
            {
                "tag": "markdown",
                "content": f"本次请求已处理：**{card_markdown_escape(action_label)}**。",
            },
        )

    if completed:
        template, tag_text, tag_color = "green", "已处理", "green"
    elif requires_action:
        template, tag_text, tag_color = "yellow", "需要你处理", "yellow"
    else:
        template, tag_text, tag_color = "green", "里程碑完成", "green"
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": f"Ori One Mind · {tag_text}"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": title},
            "subtitle": {
                "tag": "plain_text",
                "content": "Ori One Mind 自动研发" + (" · 24 小时提醒" if reminder else ""),
            },
            "template": template,
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": tag_text},
                    "color": tag_color,
                }
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def send_workflow_card(
    key: str,
    record: dict[str, Any],
    reminder: bool = False,
) -> tuple[bool, str, str, str]:
    user_id, configured_chat_id = workflow_recipient()
    kind = "reminder" if reminder else "initial"
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-send",
        "--user-id",
        user_id,
        "--msg-type",
        "interactive",
        "--content",
        json.dumps(
            build_workflow_card(record, reminder=reminder),
            ensure_ascii=False,
            separators=(",", ":"),
        ),
        "--as",
        "bot",
        "--idempotency-key",
        idempotency_key(key, f"workflow-{kind}"),
    ]
    try:
        result = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=30,
            env=lark_environment(),
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return False, "", "", lark_reply_failure_reason(error=exc)
    if not lark_succeeded(result):
        return False, "", "", lark_reply_failure_reason(result=result)
    message_id = sent_message_id(result.stdout) or ""
    chat_id = sent_chat_id(result.stdout) or configured_chat_id
    if not message_id or not chat_id:
        return False, "", "", "飞书 API 返回缺少消息标识"
    return True, message_id, chat_id, ""


def retry_workflow_notifications(now: float | None = None) -> bool:
    if not workflow_notifications_enabled():
        return False
    if not _workflow_delivery_lock.acquire(blocking=False):
        return False
    try:
        due = _workflow_store.due_delivery(now)
        if due is None:
            return False
        kind, key, record = due
        delivered, message_id, chat_id, reason = send_workflow_card(
            key,
            record,
            reminder=kind == "reminder",
        )
        if delivered:
            _workflow_store.delivery_succeeded(
                key,
                kind,
                message_id,
                chat_id,
                24 * 60 * 60,
                now,
            )
            log(f"workflow notification delivered kind={kind}")
        else:
            _workflow_store.delivery_failed(key, kind, reason, now)
            log(f"workflow notification deferred kind={kind} reason={reason}")
        return True
    finally:
        _workflow_delivery_lock.release()


def workflow_recovery_prompt(recovery: dict[str, Any]) -> str:
    request_id = str(recovery.get("attention_request_id") or "")
    action_id = str(recovery.get("selected_action_id") or "")
    action_label = str(recovery.get("selected_action_label") or "")
    resolution = str(recovery.get("resolution") or "")
    summary = str(recovery.get("summary") or "")
    if recovery.get("task_id") == "TEST-ROUNDTRIP":
        return (
            "这是 Ori One Mind 飞书桥 TEST-ROUNDTRIP 往返测试。\n"
            f"{workflow_recovery_signature(recovery)}\n"
            f"选择：{action_label}\n"
            f"测试事项：{summary}\n\n"
            "只回报这次测试回执：专用 Codex Task 已收到一次飞书选择。"
            "不得调用 Neon、编排器或 resolve-attention；不得读取或修改仓库文件；"
            "不得租用、推进或改变任何 ONE 研发任务。"
        )
    command = " ".join(
        [
            "node",
            "bin/orchestrator.mjs",
            "resolve-attention",
            "--request-id",
            shlex.quote(request_id),
            "--action-id",
            shlex.quote(action_id),
            "--action-label",
            shlex.quote(action_label),
            "--summary",
            shlex.quote(summary),
            "--resolution",
            shlex.quote(resolution),
        ]
    )
    return (
        "用户已通过飞书处理 Ori One Mind 自动研发人工门。\n"
        f"{workflow_recovery_signature(recovery)}\n"
        f"任务：{recovery.get('task_id')}\n"
        f"选择：{action_label}\n"
        f"事项：{summary}\n"
        f"工作台：{recovery.get('workbench_url')}\n\n"
        "这是一次已完成身份、会话关联和单次消费检查的响应。"
        "第一步必须执行：\n"
        f"{command}\n\n"
        "只有 resolve-attention 成功后，才能按返回的检查点处理。"
        "resolution 为 pause 或 stop 时不得继续自动研发；"
        "不得重复消费、重复提交或绕过仍然存在的人工门。"
    )


def workflow_recovery_signature(recovery: dict[str, Any]) -> str:
    if recovery.get("task_id") == "TEST-ROUNDTRIP":
        return (
            "Ori One Mind 飞书桥往返测试响应\n"
            f"roundtrip_event_id: {recovery.get('event_id')}\n"
            f"selected_action_id: {recovery.get('selected_action_id')}\n"
            f"resolution: {recovery.get('resolution')}"
        )
    return (
        "Ori One Mind 飞书人工门响应\n"
        f"attention_request_id: {recovery.get('attention_request_id')}\n"
        f"selected_action_id: {recovery.get('selected_action_id')}\n"
        f"resolution: {recovery.get('resolution')}"
    )


def workflow_recovery_in_rollout(
    thread_id: str,
    recovery: dict[str, Any],
) -> bool:
    rollout_path = rollout_path_for_task(thread_id)
    signature = workflow_recovery_signature(recovery)
    if rollout_path is None or not rollout_path.is_file():
        return False
    try:
        with rollout_path.open(encoding="utf-8", errors="ignore") as handle:
            for line in handle:
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(event, dict):
                    continue
                payload = event.get("payload")
                if (
                    event.get("type") != "response_item"
                    or not isinstance(payload, dict)
                    or payload.get("type") != "message"
                    or payload.get("role") != "user"
                ):
                    continue
                content = payload.get("content")
                if not isinstance(content, list):
                    continue
                text = "\n".join(
                    str(item.get("text") or "")
                    for item in content
                    if isinstance(item, dict)
                    and item.get("type") in {"input_text", "text"}
                )
                if signature in text:
                    return True
    except OSError:
        return False
    return False


def submit_workflow_recovery(
    recovery: dict[str, Any],
) -> tuple[str, str]:
    thread_id = workflow_codex_task_id()
    if workflow_recovery_in_rollout(thread_id, recovery):
        return "accepted", "reconciled"
    if active_run_for_task(thread_id) is not None:
        return "retry", "Codex Task 当前正忙"
    recipient, _chat_id = workflow_recipient()
    if task_by_id(thread_id, recipient) is None:
        return "retry", "固定 Codex Task 当前不可用"
    rollout_path = rollout_path_for_task(thread_id)
    if not DESKTOP_IPC_SOCKET.exists() or rollout_path is None:
        return "retry", "Codex Desktop 当前不可用"

    request_sent = False
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(30)
            connection.connect(str(DESKTOP_IPC_SOCKET))
            client_id = initialize_desktop_connection(connection)
            request_id = str(uuid.uuid4())
            request_sent = True
            send_ipc_message(
                connection,
                {
                    "type": "request",
                    "requestId": request_id,
                    "sourceClientId": client_id,
                    "version": 2,
                    "method": "thread-follower-start-turn",
                    "params": {
                        "conversationId": thread_id,
                        "turnStart": {
                            "request": {
                                "threadId": thread_id,
                                "input": codex_turn_input(
                                    workflow_recovery_prompt(recovery),
                                    [],
                                    [],
                                ),
                            },
                            "context": {
                                "inheritThreadSettings": True,
                                "attachments": [],
                            },
                        },
                    },
                    "timeoutMs": 30000,
                },
            )
            response = wait_for_ipc_response(connection, request_id)
    except (ConnectionError, FileNotFoundError, OSError, socket.timeout):
        return (
            ("unknown", "Codex Desktop 提交确认中断")
            if request_sent
            else ("retry", "Codex Desktop 当前不可用")
        )
    if response.get("resultType") != "success":
        error = " ".join(str(response.get("error") or "").split()).lower()
        if any(marker_text in error for marker_text in ("active turn", "already running", "busy")):
            return "retry", "Codex Task 当前正忙"
        return "retry", "Codex Desktop 未接受恢复请求"
    turn_id = str(
        response.get("result", {}).get("result", {}).get("turn", {}).get("id") or ""
    )
    if not turn_id:
        return "unknown", "Codex Desktop 已接受但未返回 turn id"
    remember_bridge_turn(turn_id)
    return "accepted", turn_id


def retry_workflow_recoveries(now: float | None = None) -> bool:
    if not workflow_notifications_enabled():
        return False
    if not _workflow_delivery_lock.acquire(blocking=False):
        return False
    try:
        thread_id = workflow_codex_task_id()
        for key, recovery in _workflow_store.unknown_recoveries():
            if workflow_recovery_in_rollout(thread_id, recovery):
                _workflow_store.recovery_succeeded(key, "reconciled", now)
                log("workflow decision delivery reconciled from dedicated Codex Task")
                return True
        due = _workflow_store.due_recovery(now)
        if due is None:
            return False
        key, recovery = due
        status, detail = submit_workflow_recovery(recovery)
        if status == "accepted":
            _workflow_store.recovery_succeeded(key, detail, now)
            log("workflow decision delivered to dedicated Codex Task")
        else:
            _workflow_store.recovery_failed(
                key,
                detail,
                retryable=status == "retry",
                now=now,
            )
            log(f"workflow decision delivery status={status} reason={detail}")
        return True
    finally:
        _workflow_delivery_lock.release()


def workflow_event_authorized(event: dict[str, Any], record: dict[str, Any]) -> bool:
    recipient, configured_chat_id = workflow_recipient()
    user_id = str(event.get("operator_id") or event.get("sender_id") or "")
    chat_id = str(event.get("chat_id") or "")
    expected_chat = configured_chat_id or str(record.get("chat_id") or "")
    return user_id == recipient and bool(chat_id) and chat_id == expected_chat


def workflow_completed_card(record: dict[str, Any]) -> dict[str, Any]:
    return build_workflow_card(record, completed=True)


def patch_workflow_completed_cards(record: dict[str, Any]) -> None:
    card = workflow_completed_card(record)
    message_ids = dict.fromkeys(
        str(record.get(key) or "")
        for key in ("message_id", "reminder_message_id")
    )
    for message_id in message_ids:
        if message_id.startswith("om_"):
            patch_card(message_id, card)


def workflow_event_processed(key: str) -> bool:
    with _state_lock:
        processed = load_state().get("processed", [])
        return isinstance(processed, list) and key in processed


def handle_workflow_card_action(
    event: dict[str, Any],
    payload: dict[str, Any],
) -> bool:
    if payload.get("action") != "workflow_decision":
        return False
    workflow_id = str(payload.get("workflow_id") or "")
    event_id = str(payload.get("event_id") or "")
    try:
        record = _workflow_store.record_for_event(workflow_id, event_id)
    except WorkflowStateError:
        log("workflow decision ignored reason=state-unavailable")
        raise
    if record is None or not workflow_event_authorized(event, record):
        log("workflow decision ignored reason=authorization")
        return True
    bridge_event_id = str(event.get("event_id") or "")
    if not bridge_event_id:
        return True
    outcome, _recovery = _workflow_store.consume_token_decision(
        workflow_id,
        event_id,
        str(payload.get("decision_token") or ""),
        str(payload.get("action_id") or ""),
        source_id=bridge_event_id,
    )
    if outcome == "already_consumed":
        log("workflow decision status=already_consumed")
        return True
    if outcome not in {"consumed", "consumed_retry"}:
        log(f"workflow decision ignored reason={outcome}")
        return True
    processed_key = f"workflow-card:{bridge_event_id}"
    if workflow_event_processed(processed_key):
        return True
    updated = _workflow_store.record_for_event(workflow_id, event_id) or record
    patch_workflow_completed_cards(updated)
    mark_processed(load_state(), processed_key)
    log(f"workflow decision status={outcome}")
    return True


def workflow_parent_message_id(event: dict[str, Any]) -> str:
    for key in ("parent_id", "parent_message_id", "root_id", "root_message_id"):
        value = str(event.get(key) or "")
        if value.startswith("om_"):
            return value
    return ""


def handle_workflow_text_reply(
    event: dict[str, Any],
    content: str,
) -> bool:
    parent_id = workflow_parent_message_id(event)
    if not parent_id:
        return False
    try:
        record = _workflow_store.record_for_message(parent_id)
    except WorkflowStateError:
        log("workflow reply ignored reason=state-unavailable")
        raise
    if record is None:
        return False
    if not workflow_event_authorized(event, record):
        log("workflow reply ignored reason=authorization")
        return True
    message_id = str(event.get("message_id") or "")
    if not message_id:
        return True
    outcome, _recovery, current = _workflow_store.consume_reply_decision(
        parent_id,
        content,
        source_id=message_id,
    )
    processed_key = f"workflow-reply:{message_id}"
    if outcome == "unknown_action":
        labels = [
            str(action.get("label") or "")
            for action in (current or record).get("actions", [])
            if isinstance(action, dict)
        ]
        reply(
            message_id,
            "请回复选项序号或完整名称：" + " / ".join(labels),
            "workflow-choice-help",
        )
        mark_processed(load_state(), processed_key)
        return True
    if outcome == "already_consumed":
        if workflow_event_processed(processed_key):
            return True
        reply(
            message_id,
            "这个请求已经处理过，不会重复恢复工作流。",
            "workflow-choice-duplicate",
        )
        mark_processed(load_state(), processed_key)
        return True
    if outcome not in {"consumed", "consumed_retry"}:
        return True
    if workflow_event_processed(processed_key):
        return True
    updated = _workflow_store.record_for_event(
        str(record.get("workflow_id") or ""),
        str(record.get("event_id") or ""),
    ) or record
    patch_workflow_completed_cards(updated)
    if not reply(
        message_id,
        "已记录你的选择，自动研发工作流将从原检查点继续。",
        "workflow-choice",
    ):
        queue_pending_reply(
            message_id,
            "已记录你的选择，自动研发工作流将从原检查点继续。",
            "workflow-choice",
            current_reply_failure_reason() or "飞书 API 调用失败",
        )
    mark_processed(load_state(), processed_key)
    log("workflow reply consumed")
    return True


def handle_workflow_socket_connection(connection: socket.socket) -> None:
    try:
        chunks: list[bytes] = []
        total = 0
        while total <= 64 * 1024:
            chunk = connection.recv(min(4096, 64 * 1024 + 1 - total))
            if not chunk:
                break
            chunks.append(chunk)
            total += len(chunk)
            if b"\n" in chunk:
                break
        raw = b"".join(chunks).split(b"\n", 1)[0]
        if not raw or len(raw) > 64 * 1024:
            raise WorkflowNotificationError("invalid request size")
        payload = validate_workflow_payload(json.loads(raw))
        result = _workflow_store.enqueue(payload, workflow_allowed_id())
        response = {"ok": True, "result": result}
    except WorkflowStateError:
        response = {"ok": False, "error": "workflow_state_unavailable"}
        log("workflow notification rejected reason=state-unavailable")
    except (
        OSError,
        UnicodeDecodeError,
        json.JSONDecodeError,
        WorkflowNotificationError,
    ):
        response = {"ok": False, "error": "invalid_request"}
        log("workflow notification rejected reason=invalid-request")
    try:
        connection.sendall(
            json.dumps(response, separators=(",", ":")).encode("utf-8") + b"\n"
        )
    except OSError:
        pass


def workflow_socket_loop(server: socket.socket) -> None:
    while True:
        try:
            connection, _address = server.accept()
        except (OSError, socket.timeout):
            if server.fileno() < 0:
                return
            continue
        with connection:
            handle_workflow_socket_connection(connection)


def handle_workflow_control_connection(connection: socket.socket) -> None:
    try:
        raw = connection.recv(4097)
        if not raw or len(raw) > 4096:
            raise ValueError
        request = json.loads(raw.split(b"\n", 1)[0])
        if not isinstance(request, dict) or set(request) != {"command"}:
            raise ValueError
        command = request.get("command")
        if command == "health":
            ready = workflow_configuration_valid()
            if ready:
                _workflow_store.load()
            response = {
                "ok": ready,
                "result": "ready" if ready else "invalid_configuration",
            }
        elif command == "status":
            response = {"ok": True, "result": _workflow_store.safe_status()}
        elif command == "retry-outbox":
            now = time.time()
            response = {
                "ok": True,
                "result": {
                    "notification_attempted": retry_workflow_notifications(now),
                    "recovery_attempted": retry_workflow_recoveries(now),
                },
            }
        else:
            raise ValueError
    except WorkflowStateError:
        response = {"ok": False, "error": "workflow_state_unavailable"}
        log("workflow control rejected reason=state-unavailable")
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, ValueError):
        response = {"ok": False, "error": "invalid_control_request"}
    try:
        connection.sendall(
            json.dumps(response, separators=(",", ":")).encode("utf-8") + b"\n"
        )
    except OSError:
        pass


def workflow_control_socket_loop(server: socket.socket) -> None:
    while True:
        try:
            connection, _address = server.accept()
        except (OSError, socket.timeout):
            if server.fileno() < 0:
                return
            continue
        with connection:
            handle_workflow_control_connection(connection)


def bind_workflow_socket(path: Path) -> socket.socket | None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.parent.chmod(0o700)
    if path.exists() or path.is_symlink():
        try:
            existing_mode = path.lstat().st_mode
        except OSError:
            return None
        if not stat.S_ISSOCK(existing_mode):
            log("workflow socket refused reason=unsafe-existing-path")
            return None
        path.unlink()
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        server.bind(str(path))
        path.chmod(0o600)
        server.listen(8)
        server.settimeout(1)
    except OSError:
        server.close()
        return None
    return server


def start_workflow_socket_server() -> bool:
    global _workflow_control_socket, _workflow_server_socket

    if not workflow_notifications_enabled():
        return False
    try:
        _workflow_store.load()
    except WorkflowStateError:
        log("workflow socket refused reason=state-unavailable")
        return False
    server = bind_workflow_socket(WORKFLOW_SOCKET_PATH)
    control = bind_workflow_socket(WORKFLOW_CONTROL_SOCKET_PATH)
    if server is None or control is None:
        if server is not None:
            server.close()
        if control is not None:
            control.close()
        log("workflow socket failed to start")
        return False
    _workflow_server_socket = server
    _workflow_control_socket = control
    threading.Thread(
        target=workflow_socket_loop,
        args=(server,),
        daemon=True,
        name="codex-feishu-workflow-socket",
    ).start()
    threading.Thread(
        target=workflow_control_socket_loop,
        args=(control,),
        daemon=True,
        name="codex-feishu-workflow-control",
    ).start()
    log("workflow notification endpoint ready")
    return True


def stop_workflow_socket_server() -> None:
    global _workflow_control_socket, _workflow_server_socket

    if _workflow_server_socket is not None:
        _workflow_server_socket.close()
        _workflow_server_socket = None
    if _workflow_control_socket is not None:
        _workflow_control_socket.close()
        _workflow_control_socket = None
    for path in (WORKFLOW_SOCKET_PATH, WORKFLOW_CONTROL_SOCKET_PATH):
        try:
            if path.exists() and stat.S_ISSOCK(path.lstat().st_mode):
                path.unlink()
        except OSError:
            pass


def elapsed_text(started_at: float) -> str:
    seconds = max(0, int(time.time() - started_at))
    minutes, seconds = divmod(seconds, 60)
    return f"{minutes} 分 {seconds} 秒" if minutes else f"{seconds} 秒"


def card_markdown_escape(value: str) -> str:
    replacements = {
        "&": "&#38;",
        "<": "&#60;",
        ">": "&#62;",
        "*": "&#42;",
        "_": "&#95;",
        "~": "&#126;",
        "[": "&#91;",
        "]": "&#93;",
        "(": "&#40;",
        ")": "&#41;",
        "#": "&#35;",
        "`": "&#96;",
    }
    return "".join(replacements.get(character, character) for character in value)


def task_title_text(task: dict[str, str]) -> str:
    return f"Task：{task['title']}"


def task_project_text(task: dict[str, str]) -> str:
    return f"项目：{task['project']}"


def current_task_text(task: dict[str, str]) -> str:
    return f"🟢 当前 Task\n{task_project_text(task)}\n{task_title_text(task)}"


def current_task_changed_text(task: dict[str, str], action: str = "已切换") -> str:
    return f"✅ 当前 Task {action}\n{task_project_text(task)}\n{task_title_text(task)}"


def task_status_prefix(task: dict[str, str], status: str) -> str:
    return f"{current_task_text(task)}\n状态：{status}\n\n"


def current_task_tag() -> dict[str, Any]:
    return {
        "tag": "text_tag",
        "text": {"tag": "plain_text", "content": "当前 Task"},
        "color": "green",
    }


def build_run_card(run: dict[str, Any]) -> dict[str, Any]:
    status = str(run.get("status") or "正在准备")
    outcome = str(run.get("outcome") or "running")
    templates = {
        "running": ("blue", "运行中", "blue"),
        "approval": ("yellow", "等待授权", "yellow"),
        "completed": ("green", "已完成", "green"),
        "stopped": ("grey", "已停止", "neutral"),
        "failed": ("red", "未完成", "red"),
    }
    template, tag_text, tag_color = templates.get(outcome, templates["running"])
    attachment_count = int(run.get("attachment_count") or 0)
    details = [
        f"**当前阶段**\n{status}",
        f"<font color='grey'>运行时间：{elapsed_text(float(run['started_at']))}</font>",
    ]
    if attachment_count:
        details.append(f"<font color='grey'>本轮附件：{attachment_count} 个</font>")
    elements: list[dict[str, Any]] = [
        {
            "tag": "markdown",
            "content": "\n\n".join(details),
        }
    ]
    if outcome in {"running", "approval"}:
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "停止运行…"},
                "type": "default",
                "behaviors": [
                    {
                        "type": "callback",
                        "value": {
                            "action": "stop_run",
                            "run_id": str(run["run_id"]),
                        },
                    }
                ],
                "confirm": {
                    "title": {"tag": "plain_text", "content": "确认停止当前运行？"},
                    "text": {
                        "tag": "plain_text",
                        "content": "只有确认后才会停止。取消可继续等待，已完成的工作会保留。",
                    },
                },
            }
        )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": f"Codex {tag_text}"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": task_title_text(run["task"])},
            "subtitle": {
                "tag": "plain_text",
                "content": task_project_text(run["task"]),
            },
            "template": template,
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                current_task_tag(),
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": tag_text},
                    "color": tag_color,
                }
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def build_queued_card(
    entry: dict[str, Any],
    position: int,
    status: str = "等待当前运行完成后自动执行",
    canceled: bool = False,
) -> dict[str, Any]:
    task = entry["task"]
    attachment_count = len(entry.get("image_keys") or []) + len(
        entry.get("file_keys") or []
    )
    details = [
        f"**当前状态**\n{'已取消排队' if canceled else status}",
    ]
    if not canceled:
        details.append(f"<font color='grey'>队列位置：第 {position} 条</font>")
    if attachment_count:
        details.append(f"<font color='grey'>本轮附件：{attachment_count} 个</font>")
    elements: list[dict[str, Any]] = [
        {"tag": "markdown", "content": "\n\n".join(details)}
    ]
    if not canceled:
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "取消排队…"},
                "type": "default",
                "behaviors": [
                    {
                        "type": "callback",
                        "value": {
                            "action": "cancel_queued_input",
                            "queue_id": str(entry["queue_id"]),
                        },
                    }
                ],
                "confirm": {
                    "title": {"tag": "plain_text", "content": "确认取消这条消息？"},
                    "text": {
                        "tag": "plain_text",
                        "content": "取消后不会发送到 Codex，其他排队消息不受影响。",
                    },
                },
            }
        )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": "Codex 消息已排队"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": task_title_text(task)},
            "subtitle": {"tag": "plain_text", "content": task_project_text(task)},
            "template": "grey" if canceled else "blue",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                current_task_tag(),
                {
                    "tag": "text_tag",
                    "text": {
                        "tag": "plain_text",
                        "content": "已取消" if canceled else "已排队",
                    },
                    "color": "neutral" if canceled else "blue",
                }
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def build_approval_card(run: dict[str, Any], approval: dict[str, Any]) -> dict[str, Any]:
    request_type = str(approval.get("type") or "permission")
    titles = {
        "command": "Codex 请求运行命令",
        "file": "Codex 请求修改文件",
        "permission": "Codex 请求临时权限",
    }
    detail = card_markdown_escape(
        str(approval.get("detail") or "请在允许前确认桌面版中的操作内容。")[:1200]
    )
    callback_base = {
        "run_id": str(run["run_id"]),
        "request_id": str(approval["request_id"]),
        "approval_type": request_type,
    }
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": "Codex 等待授权"},
        },
        "header": {
            "title": {
                "tag": "plain_text",
                "content": task_title_text(run["task"]),
            },
            "subtitle": {
                "tag": "plain_text",
                "content": task_project_text(run["task"]),
            },
            "template": "yellow",
            "icon": {"tag": "standard_icon", "token": "approval_colorful"},
            "text_tag_list": [
                current_task_tag(),
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": "待处理"},
                    "color": "yellow",
                }
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": f"**授权请求**\n{titles.get(request_type, titles['permission'])}",
                },
                {"tag": "markdown", "content": f"**请求说明**\n{detail}"},
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "允许一次"},
                    "type": "primary_filled",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {**callback_base, "action": "approve_once"},
                        }
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "拒绝"},
                    "type": "danger",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {**callback_base, "action": "decline"},
                        }
                    ],
                    "confirm": {
                        "title": {"tag": "plain_text", "content": "拒绝这次请求？"},
                        "text": {"tag": "plain_text", "content": "Codex 可能无法继续当前步骤。"},
                    },
                },
            ],
        },
    }


def completed_approval_card(
    run: dict[str, Any],
    approval: dict[str, Any],
    approved: bool,
) -> dict[str, Any]:
    card = build_approval_card(run, approval)
    card["header"]["template"] = "green" if approved else "grey"
    card["header"]["text_tag_list"] = [
        current_task_tag(),
        {
            "tag": "text_tag",
            "text": {"tag": "plain_text", "content": "已允许" if approved else "已拒绝"},
            "color": "green" if approved else "neutral",
        }
    ]
    card["body"]["elements"] = [
        {
            "tag": "markdown",
            "content": "本次请求已允许。" if approved else "本次请求已拒绝。",
        }
    ]
    return card


def normalized_content(content: str) -> str:
    return re.sub(r"^@\S+\s*", "", content.strip()).strip()


def help_text() -> str:
    return (
        "可用命令：\n"
        "机器人菜单“选择 Task” —— 选择当前 Task 或恢复已归档 Task\n"
        "机器人菜单“新建 Task” —— 选择项目并新建 Task\n"
        "机器人菜单“归档 Task” —— 可取消或二次确认归档当前 Task\n"
        "对话 —— 用文字打开 Task 选择卡片（备用）\n"
        "选择 N —— 文字选择 task（备用）\n"
        "搜索 关键词 —— 按标题搜索当前项目的 Task\n"
        "当前 —— 查看当前 task\n"
        "帮助 —— 显示本说明\n\n"
        "选择后，文字、图片、文件和音频会发送到该 Codex task。"
        "Task 运行中继续发送的消息会自动排队。"
        f"单次最多 {MAX_INPUT_IMAGES} 张图片、{MAX_INPUT_FILES} 个文件。"
    )


def option_text(task: dict[str, str]) -> str:
    return f"{task['project']} · {task['title']}"


def build_task_card(
    tasks: list[dict[str, str]],
    selected_id: str | None,
    project_filter: str | None = None,
    page: int = 0,
    search_query: str = "",
    archived: bool = False,
    selection_changed: bool = False,
) -> dict[str, Any]:
    selected = next((task for task in tasks if task["id"] == selected_id), None)
    projects = list(dict.fromkeys(task["project"] for task in tasks))
    active_project = (
        project_filter
        if project_filter in projects
        else selected["project"] if selected else projects[0] if projects else None
    )
    project_tasks = [
        task for task in tasks if task["project"] == active_project
    ]
    query = search_query.strip().casefold()
    if query:
        project_tasks = [
            task
            for task in project_tasks
            if query in task["title"].casefold()
            or query in option_text(task).casefold()
        ]
    page_count = max(1, (len(project_tasks) + TASKS_PER_PAGE - 1) // TASKS_PER_PAGE)
    active_page = min(max(page, 0), page_count - 1)
    start = active_page * TASKS_PER_PAGE
    visible_tasks = project_tasks[start : start + TASKS_PER_PAGE]
    card_title = "恢复已归档 Task" if archived else "选择 Codex task"
    header: dict[str, Any] = {
        "title": {
            "tag": "plain_text",
            "content": (
                f"待恢复：{selected['title']}"
                if archived and selected
                else task_title_text(selected)
                if selected
                else card_title
            ),
        },
        "subtitle": {
            "tag": "plain_text",
            "content": (
                task_project_text(selected)
                if selected
                else "选择后确认恢复该 Task"
                if archived
                else "选择后，后续文字会发送到该 Task"
            ),
        },
        "template": "yellow" if archived and selected else "green" if selected else "blue",
        "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
    }
    if selected:
        header["text_tag_list"] = [
            {
                "tag": "text_tag",
                "text": {
                    "tag": "plain_text",
                    "content": "待恢复" if archived else "当前 Task",
                },
                "color": "yellow" if archived else "green",
            }
        ]
    elements: list[dict[str, Any]] = [
        {
            "tag": "markdown",
            "content": (
                (
                    "**恢复已归档 Task**\n先选项目，再选 Task，最后点击“恢复这个 Task”。"
                    if archived
                    else (
                        "✅ **当前 Task 已切换**\n后续消息会持续发送到这个 Task。"
                        if selection_changed
                        else "**选择 Codex Task**\n先选项目，再选 Task；当前选择会持续保留。"
                    )
                )
                + (f"\n当前搜索：`{card_markdown_escape(search_query[:80])}`" if query else "")
                if tasks
                else (
                    "当前没有你有权恢复的已归档 Task。"
                    if archived
                    else "当前没有你有权访问的 Codex task。请联系这台 Mac 的管理员。"
                )
            ),
        }
    ]
    project_selector: dict[str, Any] = {
        "tag": "select_static",
        "name": "archived_project_selector" if archived else "project_selector",
        "placeholder": {"tag": "plain_text", "content": "筛选项目"},
        "options": [
            {
                "text": {"tag": "plain_text", "content": project},
                "value": project,
            }
            for project in projects
        ],
        "initial_option": active_project,
        "width": "fill",
    }
    selector: dict[str, Any] = {
        "tag": "select_static",
        "name": "archived_task_selector" if archived else "task_selector",
        "placeholder": {
            "tag": "plain_text",
            "content": "选择一个已归档 Task" if archived else "选择一个 Task",
        },
        "options": [
            {
                "text": {"tag": "plain_text", "content": option_text(task)},
                "value": task["id"],
            }
            for task in visible_tasks
        ],
        "width": "fill",
    }
    if selected and selected in visible_tasks:
        selector["initial_option"] = selected["id"]
    if tasks:
        elements.append(project_selector)
        if visible_tasks:
            elements.append(selector)
        else:
            elements.append(
                {
                    "tag": "markdown",
                    "content": "当前项目没有匹配的 Task。请清除搜索或切换项目。",
                }
            )
        page_label = f"第 {active_page + 1}/{page_count} 页 · {len(project_tasks)} 个 Task"
        elements.append({"tag": "markdown", "content": f"<font color='grey'>{page_label}</font>"})
        if active_page > 0:
            elements.append(
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "上一页"},
                    "type": "default",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "archived_task_page" if archived else "task_page",
                                "page": active_page - 1,
                            },
                        }
                    ],
                }
            )
        if active_page + 1 < page_count:
            elements.append(
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "下一页"},
                    "type": "default",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "archived_task_page" if archived else "task_page",
                                "page": active_page + 1,
                            },
                        }
                    ],
                }
            )
        if query and not archived:
            elements.append(
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "清除搜索"},
                    "type": "default",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "clear_task_search"}}
                    ],
                }
            )
    if archived:
        if selected:
            elements.append(
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "恢复这个 Task"},
                    "type": "primary_filled",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "restore_task",
                                "task_id": str(selected["id"]),
                            },
                        }
                    ],
                    "confirm": {
                        "title": {"tag": "plain_text", "content": "确认恢复这个 Task？"},
                        "text": {
                            "tag": "plain_text",
                            "content": option_text(selected),
                        },
                    },
                }
            )
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "返回当前 Task"},
                "type": "default",
                "width": "fill",
                "behaviors": [
                    {"type": "callback", "value": {"action": "show_task_selector"}}
                ],
            }
        )
    else:
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "查看已归档 Task"},
                "type": "default",
                "width": "fill",
                "behaviors": [
                    {"type": "callback", "value": {"action": "show_archived_tasks"}}
                ],
            }
        )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": card_title},
        },
        "header": header,
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def build_new_task_card(
    projects: list[str],
    selected_project: str | None = None,
    canceled: bool = False,
) -> dict[str, Any]:
    active_project = (
        selected_project
        if selected_project in projects
        else projects[0] if projects else None
    )
    elements: list[dict[str, Any]] = [
        {
            "tag": "markdown",
            "content": (
                "已取消新建流程，不会再等待 Task 标题。已经创建的 Task 不受影响。"
                if canceled
                else
                "**选择新 Task 所属项目**\n确认项目后，Bot 会等待你发送 Task 标题。"
                if projects
                else "当前没有可用于新建 Task 的本地项目，请联系这台 Mac 的管理员。"
            ),
        }
    ]
    if active_project and not canceled:
        elements.extend(
            [
                {
                    "tag": "select_static",
                    "name": "new_task_project_selector",
                    "placeholder": {"tag": "plain_text", "content": "选择项目"},
                    "options": [
                        {
                            "text": {"tag": "plain_text", "content": project},
                            "value": project,
                        }
                        for project in projects
                    ],
                    "initial_option": active_project,
                    "width": "fill",
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "在此项目新建"},
                    "type": "primary_filled",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "new_task",
                                "project": active_project,
                            },
                        }
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "取消新建"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "cancel_new_task"}}
                    ],
                },
            ]
        )
    elif canceled:
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "返回 Task 选择"},
                "type": "default",
                "width": "fill",
                "behaviors": [
                    {"type": "callback", "value": {"action": "show_task_selector"}}
                ],
            }
        )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": "新建 Codex Task"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": "新建 Codex Task"},
            "subtitle": {
                "tag": "plain_text",
                "content": (
                    "已取消"
                    if canceled
                    else f"当前项目：{active_project}"
                    if active_project
                    else "没有可用项目"
                ),
            },
            "template": "grey" if canceled else "blue",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": (
                [
                    {
                        "tag": "text_tag",
                        "text": {"tag": "plain_text", "content": "已取消"},
                        "color": "neutral",
                    }
                ]
                if canceled
                else []
            ),
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def build_archive_task_card(
    task: dict[str, str] | None,
    busy: bool = False,
    archived: bool = False,
    canceled: bool = False,
    restored: bool = False,
) -> dict[str, Any]:
    if task is None:
        status = "尚未选择 Task。请先点击机器人菜单中的“选择 Task”。"
    elif archived:
        status = f"已归档：**{card_markdown_escape(option_text(task))}**\n\n可在下方立即撤销归档。"
    elif canceled:
        status = (
            f"已取消归档：**{card_markdown_escape(option_text(task))}**\n\n"
            "当前 Task 保持选中，没有执行归档。"
        )
    elif restored:
        status = (
            f"已恢复并选择：**{card_markdown_escape(option_text(task))}**\n\n"
            "后续消息会继续发送到这个 Task。"
        )
    elif busy:
        status = (
            f"当前 Task：**{card_markdown_escape(option_text(task))}**\n\n"
            "这个 Task 正在运行，完成或停止后才能归档。"
        )
    else:
        status = (
            f"当前 Task：**{card_markdown_escape(option_text(task))}**\n\n"
            "归档后会从未归档列表移除，可在飞书或 Codex Desktop 中恢复。"
        )
    elements: list[dict[str, Any]] = [{"tag": "markdown", "content": status}]
    if task is not None and not busy and not archived and not canceled:
        elements.extend(
            [
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "归档这个 Task…"},
                    "type": "danger",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "archive_task",
                                "task_id": str(task["id"]),
                            },
                        }
                    ],
                    "confirm": {
                        "title": {
                            "tag": "plain_text",
                            "content": "确认归档这个 Task？",
                        },
                        "text": {
                            "tag": "plain_text",
                            "content": option_text(task),
                        },
                    },
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "取消，不归档"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "cancel_archive",
                                "task_id": str(task["id"]),
                            },
                        }
                    ],
                },
            ]
        )
    if task is not None and archived:
        elements.extend(
            [
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "撤销归档"},
                    "type": "primary_filled",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "restore_task",
                                "task_id": str(task["id"]),
                            },
                        }
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "选择其他 Task"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "show_task_selector"}}
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "新建 Task"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "show_new_task"}}
                    ],
                },
            ]
        )
    elif task is not None and restored:
        elements.extend(
            [
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "选择其他 Task"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "show_task_selector"}}
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "新建 Task"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "show_new_task"}}
                    ],
                },
            ]
        )
    header_tags: list[dict[str, Any]] = []
    if task is not None and not archived:
        header_tags.append(current_task_tag())
    if archived or canceled or restored or busy:
        header_tags.append(
            {
                "tag": "text_tag",
                "text": {
                    "tag": "plain_text",
                    "content": (
                        "已恢复"
                        if restored
                        else "已归档"
                        if archived
                        else "已取消"
                        if canceled
                        else "运行中"
                    ),
                },
                "color": (
                    "green"
                    if restored
                    else "neutral"
                    if archived or canceled
                    else "yellow"
                ),
            }
        )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": "归档 Codex Task"},
        },
        "header": {
            "title": {
                "tag": "plain_text",
                "content": task_title_text(task) if task else "归档 Codex Task",
            },
            "subtitle": {
                "tag": "plain_text",
                "content": task_project_text(task) if task else "没有当前 Task",
            },
            "template": (
                "green"
                if restored
                else "grey"
                if archived or canceled
                else "red"
                if task and not busy
                else "yellow"
            ),
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": header_tags,
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def updated_task_card(
    card_content: str,
    selected: dict[str, str],
    tasks: list[dict[str, str]] | None = None,
) -> dict[str, Any] | None:
    try:
        card = json.loads(card_content)
    except (TypeError, json.JSONDecodeError):
        return None
    if not isinstance(card, dict) or card.get("schema") != "2.0":
        return None
    if tasks is not None:
        return build_task_card(
            tasks,
            selected["id"],
            selected["project"],
            selection_changed=True,
        )
    header = card.get("header")
    body = card.get("body")
    elements = body.get("elements") if isinstance(body, dict) else None
    if not isinstance(header, dict) or not isinstance(elements, list):
        return None
    selector = next(
        (
            element
            for element in elements
            if isinstance(element, dict)
            and element.get("tag") == "select_static"
            and element.get("name") == "task_selector"
        ),
        None,
    )
    if selector is None:
        return None
    options = selector.get("options")
    if not isinstance(options, list) or selected["id"] not in {
        option.get("value")
        for option in options
        if isinstance(option, dict)
    }:
        return None
    header["title"] = {
        "tag": "plain_text",
        "content": task_title_text(selected),
    }
    header["subtitle"] = {
        "tag": "plain_text",
        "content": task_project_text(selected),
    }
    header["template"] = "green"
    header["text_tag_list"] = [
        {
            "tag": "text_tag",
            "text": {"tag": "plain_text", "content": "当前 Task"},
            "color": "green",
        }
    ]
    selector["initial_option"] = selected["id"]
    return card


def reply_task_card(
    message_id: str,
    state_key: str,
    state: dict[str, Any],
) -> bool:
    return reply_card(
        message_id,
        task_card_for_state(state_key, state),
        "task-card",
    )


def send_task_card(
    user_id: str,
    state: dict[str, Any],
    event_id: str,
) -> bool:
    return send_menu_card(
        user_id,
        state,
        task_card_for_state(user_id, state),
        f"select-task-{event_id}",
    )


def card_context_type(card: dict[str, Any]) -> str:
    elements = card.get("body", {}).get("elements", [])
    names = {
        str(element.get("name") or "")
        for element in elements
        if isinstance(element, dict) and element.get("tag") == "select_static"
    }
    if "new_task_project_selector" in names:
        return "new_task"
    if names & {"archived_project_selector", "archived_task_selector"}:
        return "archived_tasks"
    if names & {"project_selector", "task_selector"}:
        return "tasks"
    title = str(card.get("header", {}).get("title", {}).get("content") or "")
    return "archive_task" if title == "归档 Codex Task" else ""


def remember_card_context(
    state: dict[str, Any],
    user_id: str,
    message_id: str,
    card: dict[str, Any],
) -> None:
    context_type = card_context_type(card)
    if not message_id.startswith("om_") or not context_type:
        return
    contexts = state.setdefault("card_contexts", {})
    if not isinstance(contexts, dict):
        contexts = {}
    contexts.pop(message_id, None)
    contexts[message_id] = {
        "user_id": user_id,
        "type": context_type,
    }
    state["card_contexts"] = dict(list(contexts.items())[-100:])
    save_state(state)


def card_context_for_event(
    state: dict[str, Any],
    user_id: str,
    message_id: str,
) -> str:
    context = state.get("card_contexts", {}).get(message_id)
    if not isinstance(context, dict) or context.get("user_id") != user_id:
        return ""
    return str(context.get("type") or "")


def send_menu_card(
    user_id: str,
    state: dict[str, Any],
    card: dict[str, Any],
    kind: str,
) -> bool:
    with _state_lock:
        success, chat_id, message_id = send_card(
            user_id,
            card,
            kind,
        )
        if success and chat_id:
            authorize_chat(state, user_id, chat_id)
        if success and message_id:
            remember_card_context(state, user_id, message_id, card)
        return success


def authorize_chat(state: dict[str, Any], user_id: str, chat_id: str) -> None:
    with _state_lock:
        if not chat_id.startswith("oc_"):
            return
        chats = state.setdefault("authorized_chats", {}).setdefault(user_id, [])
        if chat_id not in chats:
            chats.append(chat_id)
            state["authorized_chats"][user_id] = chats[-5:]
            save_state(state)


def is_authorized_chat(state: dict[str, Any], user_id: str, chat_id: str) -> bool:
    return chat_id in ALLOWED_CHAT_IDS or chat_id in state.get(
        "authorized_chats",
        {},
    ).get(user_id, [])


def build_access_request_card(already_pending: bool = False) -> dict[str, Any]:
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": "Codex 访问申请待审批"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": "Codex 访问申请"},
            "subtitle": {"tag": "plain_text", "content": "Codex 飞书桥接"},
            "template": "yellow",
            "icon": {"tag": "standard_icon", "token": "approval_colorful"},
            "text_tag_list": [
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": "待机主审批"},
                    "color": "yellow",
                }
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": (
                        "你的申请仍在等待这台 Mac 的机主审批。审批后，机主会为你指定可访问的项目。"
                        if already_pending
                        else "申请已提交给这台 Mac 的机主。机主必须为你指定可访问的项目后，权限才会生效。"
                    ),
                }
            ],
        },
    }


def record_access_request(user_id: str, display_name: str = "") -> bool:
    with _state_lock:
        state = load_state()
        requests = state.setdefault("access_requests", [])
        if not isinstance(requests, list):
            requests = []
        now = time.time()
        existing = next(
            (
                item
                for item in requests
                if isinstance(item, dict) and item.get("open_id") == user_id
            ),
            None,
        )
        if existing is not None:
            existing["last_requested_at"] = now
            if display_name and not existing.get("name"):
                existing["name"] = display_name[:100]
            already_pending = True
        else:
            requests.append(
                {
                    "open_id": user_id,
                    "name": display_name[:100],
                    "requested_at": now,
                    "last_requested_at": now,
                }
            )
            already_pending = False
        state["access_requests"] = requests[-100:]
        save_state(state)
        return already_pending


def selected_task(user_id: str, state: dict[str, Any]) -> dict[str, str] | None:
    with _state_lock:
        selected = state.setdefault("selected", {})
        thread_id = selected.get(user_id)
        if not thread_id and user_id == PRIMARY_ALLOWED_USER:
            for chat_id in ALLOWED_CHAT_IDS:
                thread_id = selected.get(chat_id)
                if thread_id:
                    selected[user_id] = thread_id
                    save_state(state)
                    break
        task = task_by_id(thread_id, user_id) if thread_id else None
        if thread_id and not task:
            selected.pop(user_id, None)
            save_state(state)
        return task


def task_card_for_state(user_id: str, state: dict[str, Any]) -> dict[str, Any]:
    with _state_lock:
        selected = selected_task(user_id, state)
        tasks = recent_tasks(user_id)
        state.setdefault("last_lists", {})[user_id] = [task["id"] for task in tasks]
        project_filter = state.setdefault("last_projects", {}).get(user_id)
        page = int(state.setdefault("task_pages", {}).get(user_id) or 0)
        query = str(state.setdefault("task_queries", {}).get(user_id) or "")
        save_state(state)
        return build_task_card(
            tasks,
            selected["id"] if selected else None,
            str(project_filter) if project_filter else None,
            page,
            query,
        )


def archived_task_card_for_state(
    user_id: str,
    state: dict[str, Any],
    selected_id: str | None = None,
) -> dict[str, Any]:
    with _state_lock:
        tasks = archived_tasks(user_id)
        project_filter = state.setdefault("archived_last_projects", {}).get(user_id)
        page = int(state.setdefault("archived_task_pages", {}).get(user_id) or 0)
        save_state(state)
        return build_task_card(
            tasks,
            selected_id,
            str(project_filter) if project_filter else None,
            page,
            archived=True,
        )


def show_tasks(user_id: str, state: dict[str, Any]) -> str:
    tasks = recent_tasks(user_id)
    state.setdefault("last_lists", {})[user_id] = [task["id"] for task in tasks]
    save_state(state)
    selected = state.get("selected", {}).get(user_id)
    if not tasks:
        return "当前没有你有权访问的 Codex task。请联系这台 Mac 的管理员。"
    lines = ["Codex tasks："]
    for index, task in enumerate(tasks, 1):
        marker = " ← 当前" if task["id"] == selected else ""
        lines.append(f"{index}. {option_text(task)}{marker}")
    lines.append("\n发送“选择 N”，例如：选择 2")
    return "\n".join(lines)


def select_task(user_id: str, choice: str, state: dict[str, Any]) -> str:
    with _state_lock:
        tasks = recent_tasks(user_id)
        last_ids = state.get("last_lists", {}).get(user_id, [])
        selected: dict[str, str] | None = None

        if choice.isdigit():
            index = int(choice) - 1
            if 0 <= index < len(last_ids):
                selected = task_by_id(last_ids[index], user_id)
            elif 0 <= index < len(tasks):
                selected = tasks[index]
        else:
            id_matches = [task for task in tasks if task["id"].startswith(choice)]
            title_matches = [task for task in tasks if choice.lower() in task["title"].lower()]
            matches = id_matches or title_matches
            if len(matches) == 1:
                selected = matches[0]
            elif len(matches) > 1:
                return "匹配到多个 tasks，请先发送“对话”，再用序号选择。"

        if not selected:
            return "没有找到该 task。请发送“对话”刷新列表。"
        state.setdefault("selected", {})[user_id] = selected["id"]
        save_state(state)
        return current_task_changed_text(selected)


def current_task(user_id: str, state: dict[str, Any]) -> str:
    task = selected_task(user_id, state)
    if not task:
        return "尚未选择 Codex task。请点击机器人菜单中的“选择 Task”。"
    return current_task_text(task)


def send_ipc_message(connection: socket.socket, message: dict[str, Any]) -> None:
    data = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode()
    connection.sendall(struct.pack("<I", len(data)) + data)


def receive_ipc_message(connection: socket.socket) -> dict[str, Any]:
    header = bytearray()
    while len(header) < 4:
        chunk = connection.recv(4 - len(header))
        if not chunk:
            raise ConnectionError("desktop IPC closed while reading a frame")
        header.extend(chunk)
    size = struct.unpack("<I", header)[0]
    body = bytearray()
    while len(body) < size:
        chunk = connection.recv(size - len(body))
        if not chunk:
            raise ConnectionError("desktop IPC closed while reading a message")
        body.extend(chunk)
    payload = json.loads(body)
    if not isinstance(payload, dict):
        raise ValueError("desktop IPC returned a non-object response")
    return payload


def wait_for_ipc_response(
    connection: socket.socket,
    request_id: str,
) -> dict[str, Any]:
    while True:
        response = receive_ipc_message(connection)
        if response.get("requestId") == request_id:
            return response


def initialize_desktop_connection(connection: socket.socket) -> str:
    initialize_id = str(uuid.uuid4())
    send_ipc_message(
        connection,
        {
            "type": "request",
            "requestId": initialize_id,
            "sourceClientId": "initializing-client",
            "version": 0,
            "method": "initialize",
            "params": {"clientType": "feishu-bridge"},
        },
    )
    initialize = wait_for_ipc_response(connection, initialize_id)
    if initialize.get("resultType") != "success":
        raise RuntimeError(str(initialize.get("error") or "initialize failed"))
    client_id = str(initialize.get("result", {}).get("clientId") or "")
    if not client_id:
        raise RuntimeError("desktop IPC did not return a client id")
    return client_id


def begin_desktop_following(
    connection: socket.socket,
    client_id: str,
    thread_id: str,
) -> None:
    send_ipc_message(
        connection,
        {
            "type": "broadcast",
            "sourceClientId": client_id,
            "version": 1,
            "method": "thread-stream-following-changed",
            "params": {
                "conversationId": thread_id,
                "hostId": "local",
                "following": True,
            },
        },
    )
    send_ipc_message(
        connection,
        {
            "type": "request",
            "requestId": str(uuid.uuid4()),
            "sourceClientId": client_id,
            "version": 1,
            "method": "thread-follower-load-complete-history",
            "params": {"conversationId": thread_id},
            "timeoutMs": 30000,
        },
    )


def set_run_ipc(
    run: dict[str, Any],
    connection: socket.socket,
    client_id: str,
) -> None:
    with _active_runs_lock:
        run["ipc_connection"] = connection
        run["ipc_client_id"] = client_id
    request_run_interrupt(run)


def complete_run_ipc_response(run: dict[str, Any], response: dict[str, Any]) -> bool:
    request_id = str(response.get("requestId") or "")
    if not request_id:
        return False
    pending_lock = run["ipc_pending_lock"]
    with pending_lock:
        pending = run["ipc_pending"].get(request_id)
        if not isinstance(pending, dict):
            return False
        pending["response"] = response
        pending["event"].set()
        return True


def send_run_ipc_request(
    run: dict[str, Any],
    method: str,
    version: int,
    params: dict[str, Any],
    timeout_ms: int = 30000,
) -> dict[str, Any]:
    with _active_runs_lock:
        connection = run.get("ipc_connection")
        client_id = str(run.get("ipc_client_id") or "")
    if not isinstance(connection, socket.socket) or not client_id:
        raise ConnectionError("desktop follower connection is unavailable")
    request_id = str(uuid.uuid4())
    completed = threading.Event()
    pending: dict[str, Any] = {"event": completed}
    pending_lock = run["ipc_pending_lock"]
    with pending_lock:
        run["ipc_pending"][request_id] = pending
    try:
        with run["ipc_send_lock"]:
            send_ipc_message(
                connection,
                {
                    "type": "request",
                    "requestId": request_id,
                    "sourceClientId": client_id,
                    "version": version,
                    "method": method,
                    "params": params,
                    "timeoutMs": timeout_ms,
                },
            )
        if not completed.wait(max(1, timeout_ms // 1000)):
            raise socket.timeout(f"desktop request timed out: {method}")
        response = pending.get("response")
        if not isinstance(response, dict):
            raise ConnectionError("desktop request returned no response")
        return response
    finally:
        with pending_lock:
            run["ipc_pending"].pop(request_id, None)


def interrupt_desktop_turn(run: dict[str, Any]) -> bool:
    try:
        response = send_run_ipc_request(
            run,
            "thread-follower-interrupt-turn",
            4,
            {
                "conversationId": str(run["task"]["id"]),
                "mode": "user-stop",
                "expectedTurnId": str(run.get("turn_id") or ""),
            },
        )
    except (ConnectionError, OSError, RuntimeError, ValueError, socket.timeout) as exc:
        log(f"desktop interrupt failed error={type(exc).__name__}")
        return False
    if response.get("resultType") != "success":
        log("desktop interrupt failed result=error")
        return False
    return True


def interrupt_run(run: dict[str, Any]) -> None:
    confirmed = interrupt_desktop_turn(run)
    if confirmed:
        cancel_confirmed = run.get("cancel_confirmed")
        if isinstance(cancel_confirmed, threading.Event):
            cancel_confirmed.set()
    else:
        set_run_progress(run, "停止请求未确认，请在 Codex Desktop 中查看", "failed", force=True)


def request_run_interrupt(run: dict[str, Any]) -> bool:
    with _active_runs_lock:
        cancel_event = run.get("cancel_event")
        if (
            not isinstance(cancel_event, threading.Event)
            or not cancel_event.is_set()
            or run.get("interrupt_started")
            or not run.get("turn_id")
            or not run.get("ipc_connection")
        ):
            return False
        run["interrupt_started"] = True
    threading.Thread(
        target=interrupt_run,
        args=(run,),
        daemon=True,
        name=f"codex-feishu-stop-{str(run['run_id'])[:8]}",
    ).start()
    return True


def respond_desktop_approval(
    run: dict[str, Any],
    approval: dict[str, Any],
    approved: bool,
) -> bool:
    approval_type = str(approval.get("type") or "")
    request_id = str(approval.get("request_id") or "")
    if not request_id:
        return False
    if approval_type == "command":
        method = "thread-follower-command-approval-decision"
        params: dict[str, Any] = {
            "conversationId": str(run["task"]["id"]),
            "requestId": request_id,
            "decision": "accept" if approved else "decline",
        }
    elif approval_type == "file":
        method = "thread-follower-file-approval-decision"
        params = {
            "conversationId": str(run["task"]["id"]),
            "requestId": request_id,
            "decision": "accept" if approved else "decline",
        }
    elif approval_type == "permission":
        method = "thread-follower-permissions-request-approval-response"
        requested = approval.get("params", {}).get("permissions")
        params = {
            "conversationId": str(run["task"]["id"]),
            "requestId": request_id,
            "response": {
                "permissions": requested if approved and isinstance(requested, dict) else {},
                "scope": "turn",
            },
        }
    else:
        return False
    try:
        response = send_run_ipc_request(run, method, 1, params)
    except (ConnectionError, OSError, RuntimeError, ValueError, socket.timeout) as exc:
        log(f"desktop approval response failed error={type(exc).__name__}")
        return False
    return response.get("resultType") == "success"


def action_payload(event: dict[str, Any]) -> dict[str, Any]:
    try:
        payload = json.loads(str(event.get("action_value") or ""))
    except json.JSONDecodeError:
        return {}
    return payload if isinstance(payload, dict) else {}


def pending_inputs(state: dict[str, Any]) -> list[dict[str, Any]]:
    pending = state.setdefault("pending_inputs", [])
    if not isinstance(pending, list):
        pending = []
        state["pending_inputs"] = pending
    return pending


def queued_position(
    entries: list[dict[str, Any]],
    task_id: str,
    queue_id: str,
) -> int:
    same_task = [
        entry
        for entry in entries
        if isinstance(entry, dict) and str(entry.get("task", {}).get("id") or "") == task_id
    ]
    return next(
        (
            index
            for index, entry in enumerate(same_task, start=1)
            if str(entry.get("queue_id") or "") == queue_id
        ),
        len(same_task) + 1,
    )


def enqueue_pending_input(entry: dict[str, Any]) -> tuple[bool, int, str]:
    with _state_lock:
        state = load_state()
        entries = pending_inputs(state)
        task_id = str(entry["task"]["id"])
        task_entries = [
            item
            for item in entries
            if isinstance(item, dict)
            and str(item.get("task", {}).get("id") or "") == task_id
        ]
        if len(entries) >= MAX_PENDING_INPUTS:
            return False, 0, "桥接消息队列已满，请稍后重试。"
        if len(task_entries) >= MAX_PENDING_INPUTS_PER_TASK:
            return (
                False,
                0,
                f"该 Task 已有 {MAX_PENDING_INPUTS_PER_TASK} 条排队消息，请等待后再试。",
            )
        entries.append(entry)
        save_state(state)
        return True, len(task_entries) + 1, ""


def update_pending_input(queue_id: str, **changes: Any) -> dict[str, Any] | None:
    with _state_lock:
        state = load_state()
        for entry in pending_inputs(state):
            if isinstance(entry, dict) and entry.get("queue_id") == queue_id:
                entry.update(changes)
                save_state(state)
                return dict(entry)
    return None


def cancel_pending_input(
    queue_id: str,
    user_id: str,
    chat_id: str,
) -> dict[str, Any] | None:
    with _state_lock:
        state = load_state()
        entries = pending_inputs(state)
        for index, entry in enumerate(entries):
            if (
                isinstance(entry, dict)
                and entry.get("queue_id") == queue_id
                and entry.get("user_id") == user_id
                and (not chat_id or entry.get("chat_id") == chat_id)
            ):
                removed = entries.pop(index)
                save_state(state)
                return removed
    return None


def queued_cards_for_task(task_id: str) -> list[tuple[str, dict[str, Any], int]]:
    with _state_lock:
        entries = [
            dict(entry)
            for entry in pending_inputs(load_state())
            if isinstance(entry, dict)
            and str(entry.get("task", {}).get("id") or "") == task_id
        ]
    return [
        (str(entry.get("progress_message_id") or ""), entry, index)
        for index, entry in enumerate(entries, start=1)
        if entry.get("progress_message_id")
    ]


def refresh_queued_cards(task_id: str) -> None:
    for message_id, entry, position in queued_cards_for_task(task_id):
        patch_card(message_id, build_queued_card(entry, position))


def register_active_run(run: dict[str, Any]) -> None:
    with _active_runs_lock:
        _active_runs[str(run["run_id"])] = run


def claim_active_run(run: dict[str, Any]) -> bool:
    with _active_runs_lock:
        thread_id = str(run["task"]["id"])
        running = [
            item
            for item in _active_runs.values()
            if item.get("outcome") in {"running", "approval"}
        ]
        if len(running) >= MAX_CONCURRENT_RUNS:
            return False
        if any(
            str(item["task"]["id"]) == thread_id
            and item.get("outcome") in {"running", "approval"}
            for item in running
        ):
            return False
        _active_runs[str(run["run_id"])] = run
        return True


def remove_active_run(run_id: str) -> None:
    with _active_runs_lock:
        _active_runs.pop(run_id, None)


def active_run(run_id: str) -> dict[str, Any] | None:
    with _active_runs_lock:
        return _active_runs.get(run_id)


def active_run_for_task(thread_id: str) -> dict[str, Any] | None:
    with _active_runs_lock:
        return next(
            (
                run
                for run in _active_runs.values()
                if run["task"]["id"] == thread_id
                and run.get("outcome") in {"running", "approval"}
            ),
            None,
        )


def new_run(
    user_id: str,
    chat_id: str,
    message_id: str,
    task: dict[str, str],
    image_keys: list[str],
    file_keys: list[str],
    progress_message_id: str = "",
    queue_entry: dict[str, Any] | None = None,
) -> dict[str, Any]:
    run = {
        "run_id": str(uuid.uuid4()),
        "user_id": user_id,
        "chat_id": chat_id,
        "source_message_id": message_id,
        "task": task,
        "status": "正在准备",
        "outcome": "running",
        "started_at": time.time(),
        "attachment_count": len(image_keys) + len(file_keys),
        "cancel_event": threading.Event(),
        "cancel_confirmed": threading.Event(),
        "ipc_send_lock": threading.Lock(),
        "ipc_pending_lock": threading.RLock(),
        "ipc_pending": {},
        "approvals": {},
    }
    if progress_message_id:
        run["progress_message_id"] = progress_message_id
    if queue_entry is not None:
        run["queue_entry"] = queue_entry
    return run


def start_claimed_run(
    run: dict[str, Any],
    content: str,
    image_keys: list[str],
    file_keys: list[str],
    raw_content: str,
    message_type: str,
) -> None:
    message_id = str(run["source_message_id"])
    existing_progress_id = str(run.get("progress_message_id") or "")
    if existing_progress_id:
        if not patch_card(existing_progress_id, build_run_card(run)):
            reply(
                message_id,
                task_status_prefix(run["task"], "正在准备")
                + "排队消息已开始执行，完成后会自动回复结果。",
                f"queued-running-{run['run_id']}",
            )
    else:
        try:
            card_sent, progress_message_id = reply_card_message(
                message_id,
                build_run_card(run),
                f"run-{run['run_id']}",
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            log(f"run card reply failed error={type(exc).__name__}")
            card_sent, progress_message_id = False, None
        if card_sent and progress_message_id:
            run["progress_message_id"] = progress_message_id
        else:
            reply(
                message_id,
                task_status_prefix(run["task"], "正在准备")
                + "完成后会自动回复结果。",
                "running",
            )
    worker = threading.Thread(
        target=process_message_run,
        args=(run, content, image_keys, file_keys, raw_content, message_type),
        daemon=True,
        name=f"codex-feishu-run-{str(run['run_id'])[:8]}",
    )
    run["worker"] = worker
    worker.start()


def start_next_queued_input(task_id: str, now: float | None = None) -> bool:
    timestamp = time.time() if now is None else now
    with _active_runs_lock:
        if sum(
            item.get("outcome") in {"running", "approval"}
            for item in _active_runs.values()
        ) >= MAX_CONCURRENT_RUNS:
            return False
        if active_run_for_task(task_id) is not None:
            return False
        with _state_lock:
            state = load_state()
            entries = pending_inputs(state)
            selected_index = next(
                (
                    index
                    for index, entry in enumerate(entries)
                    if isinstance(entry, dict)
                    and str(entry.get("task", {}).get("id") or "") == task_id
                    and entry.get("ready") is not False
                    and float(entry.get("available_at") or 0) <= timestamp
                ),
                None,
            )
            if selected_index is None:
                return False
            entry = entries.pop(selected_index)
            save_state(state)
        task = entry["task"]
        run = new_run(
            str(entry["user_id"]),
            str(entry["chat_id"]),
            str(entry["source_message_id"]),
            task,
            list(entry.get("image_keys") or []),
            list(entry.get("file_keys") or []),
            str(entry.get("progress_message_id") or ""),
            entry,
        )
        _active_runs[str(run["run_id"])] = run
    start_claimed_run(
        run,
        str(entry.get("content") or ""),
        list(entry.get("image_keys") or []),
        list(entry.get("file_keys") or []),
        str(entry.get("raw_content") or ""),
        str(entry.get("message_type") or "text"),
    )
    refresh_queued_cards(task_id)
    log("queued input started")
    return True


def start_pending_inputs(now: float | None = None) -> None:
    with _state_lock:
        task_ids = list(
            dict.fromkeys(
                str(entry.get("task", {}).get("id") or "")
                for entry in pending_inputs(load_state())
                if isinstance(entry, dict)
            )
        )
    for task_id in task_ids:
        if task_id:
            start_next_queued_input(task_id, now)


def set_run_progress(
    run: dict[str, Any],
    status: str,
    outcome: str | None = None,
    force: bool = False,
) -> None:
    now = time.time()
    with _active_runs_lock:
        run["status"] = status
        if outcome is not None:
            run["outcome"] = outcome
        message_id = str(run.get("progress_message_id") or "")
        last_patch_at = float(run.get("last_patch_at") or 0)
        if not message_id or (not force and now - last_patch_at < 2):
            return
        run["last_patch_at"] = now
        card = build_run_card(run)
    patch_card(message_id, card)


def approval_from_request(request: dict[str, Any]) -> dict[str, Any] | None:
    request_id = str(request.get("id") or request.get("requestId") or "")
    method = str(request.get("method") or "")
    params = request.get("params") if isinstance(request.get("params"), dict) else {}
    if not request_id:
        return None
    if method == "item/commandExecution/requestApproval":
        request_type = "command"
        detail = str(params.get("reason") or params.get("command") or "Codex 请求运行一条命令。")
    elif method == "item/fileChange/requestApproval":
        request_type = "file"
        detail = str(params.get("reason") or "Codex 请求修改本地文件。")
    elif method == "item/permissions/requestApproval":
        request_type = "permission"
        detail = str(params.get("reason") or "Codex 请求仅在本轮使用额外权限。")
    else:
        return None
    return {
        "request_id": request_id,
        "type": request_type,
        "detail": " ".join(detail.split())[:1200],
        "params": params,
    }


def publish_approval(run: dict[str, Any], approval: dict[str, Any]) -> None:
    request_id = str(approval["request_id"])
    with _active_runs_lock:
        approvals = run.setdefault("approvals", {})
        if request_id in approvals:
            return
        approvals[request_id] = approval
    set_run_progress(run, "等待你在飞书确认授权", "approval", force=True)
    try:
        success, message_id = reply_card_message(
            str(run["source_message_id"]),
            build_approval_card(run, approval),
            f"approval-{request_id}",
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        log(f"approval card reply failed error={type(exc).__name__}")
        success, message_id = False, None
    if success and message_id:
        with _active_runs_lock:
            approval["message_id"] = message_id
    elif not success:
        reply(
            str(run["source_message_id"]),
            "Codex 正在等待授权，但授权卡片未能送达。请在 Codex Desktop 中处理。",
            f"approval-fallback-{request_id}",
        )


def handle_approval_action(
    run: dict[str, Any],
    approval: dict[str, Any],
    approved: bool,
    event: dict[str, Any],
) -> None:
    success = respond_desktop_approval(run, approval, approved)
    message_id = str(event.get("message_id") or approval.get("message_id") or "")
    if not success:
        set_run_progress(run, "授权响应失败，请在 Codex Desktop 中处理", "approval", force=True)
        if message_id:
            reply(
                message_id,
                "没有成功把授权决定送到 Codex Desktop，请在桌面版中处理。",
                f"approval-error-{approval['request_id']}",
            )
        return
    token = str(event.get("token") or "")
    card = completed_approval_card(run, approval, approved)
    if token:
        update_card(token, card)
    elif message_id:
        patch_card(message_id, card)
    with _active_runs_lock:
        approval["resolved"] = True
        has_pending = any(
            not item.get("resolved")
            for item in run.get("approvals", {}).values()
            if isinstance(item, dict)
        )
    set_run_progress(
        run,
        "仍有授权请求等待处理"
        if has_pending
        else "已允许一次，Codex 继续运行"
        if approved
        else "已拒绝请求，等待 Codex 处理",
        "approval" if has_pending else "running",
        force=True,
    )


def remember_bridge_turn(turn_id: str) -> None:
    with _state_lock:
        state = load_state()
        turns = [str(item) for item in state.get("bridge_turns", [])]
        if turn_id not in turns:
            turns.append(turn_id)
        state["bridge_turns"] = turns[-200:]
        save_state(state)


def wait_for_desktop_turn(
    rollout_path: Path,
    start_offset: int,
    turn_id: str,
    on_progress: Callable[[str], None] | None = None,
    on_approval: Callable[[dict[str, Any]], None] | None = None,
    cancel_event: threading.Event | None = None,
    cancel_confirmed: threading.Event | None = None,
    ipc_connection: socket.socket | None = None,
    client_id: str = "",
    on_ipc_response: Callable[[dict[str, Any]], bool] | None = None,
) -> tuple[bool, str, list[str]]:
    deadline = time.monotonic() + 3600
    cancelled_at: float | None = None
    last_elapsed_update = 0.0
    last_snapshot_request = 0.0
    images: list[str] = []
    with rollout_path.open("r", encoding="utf-8") as handle:
        handle.seek(start_offset)
        while time.monotonic() < deadline:
            now = time.monotonic()
            if cancel_event is not None and cancel_event.is_set():
                cancelled_at = cancelled_at or now
                if now - cancelled_at >= 10:
                    return (
                        False,
                        "已按你的要求停止运行。"
                        if cancel_confirmed is not None and cancel_confirmed.is_set()
                        else "已发出停止请求，但 Codex Desktop 未确认。请在桌面版查看当前状态。",
                        [],
                    )
            if on_progress is not None and now - last_elapsed_update >= 10:
                on_progress("正在等待 Codex 完成")
                last_elapsed_update = now

            if ipc_connection is not None:
                try:
                    readable, _, _ = select.select([ipc_connection], [], [], 0)
                except (OSError, ValueError):
                    readable = []
                if readable:
                    try:
                        frame = receive_ipc_message(ipc_connection)
                    except (ConnectionError, OSError, ValueError, json.JSONDecodeError):
                        ipc_connection = None
                    else:
                        if on_ipc_response is not None and on_ipc_response(frame):
                            continue
                        if (
                            frame.get("type") == "broadcast"
                            and frame.get("method") == "thread-stream-state-changed"
                        ):
                            params = frame.get("params")
                            change = params.get("change") if isinstance(params, dict) else None
                            if isinstance(change, dict) and change.get("type") == "snapshot":
                                conversation = change.get("conversationState")
                                requests = conversation.get("requests") if isinstance(conversation, dict) else None
                                if isinstance(requests, list) and on_approval is not None:
                                    for request in requests:
                                        if isinstance(request, dict):
                                            approval = approval_from_request(request)
                                            if approval is not None:
                                                on_approval(approval)
                            elif (
                                isinstance(change, dict)
                                and change.get("type") == "patches"
                                and client_id
                                and now - last_snapshot_request >= 1
                            ):
                                snapshot_id = str(uuid.uuid4())
                                send_ipc_message(
                                    ipc_connection,
                                    {
                                        "type": "request",
                                        "requestId": snapshot_id,
                                        "sourceClientId": client_id,
                                        "version": 1,
                                        "method": "thread-follower-load-complete-history",
                                        "params": {"conversationId": str(params.get("conversationId") or "")},
                                        "timeoutMs": 30000,
                                    },
                                )
                                last_snapshot_request = now
            line = handle.readline()
            if not line:
                time.sleep(0.25)
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                continue
            payload = record.get("payload")
            if not isinstance(payload, dict):
                continue
            event_type = payload.get("type")
            if on_progress is not None:
                outer_type = record.get("type")
                if outer_type == "response_item" and event_type in {
                    "function_call",
                    "custom_tool_call",
                    "computer_initialize_state",
                }:
                    on_progress("正在使用工具")
                elif outer_type == "event_msg" and event_type in {
                    "agent_message_delta",
                    "agent_message_content_delta",
                }:
                    on_progress("正在整理回复")
            if event_type == "image_generation_end":
                image = normalized_image_reference(str(payload.get("saved_path") or ""))
                if image is not None and image not in images:
                    images.append(image)
                continue
            if payload.get("turn_id") != turn_id:
                continue
            if event_type == "task_complete":
                message = str(payload.get("last_agent_message") or "").strip()
                return (
                    True,
                    message or "Codex 已完成，但没有返回文字结果。",
                    images,
                )
            if event_type in {"task_failed", "turn_aborted"}:
                return (
                    False,
                    "已按你的要求停止运行。"
                    if cancel_event is not None and cancel_event.is_set()
                    else "Codex 没有完成这条消息，请在桌面版中查看具体原因。",
                    [],
                )
    return (
        False,
        "等待超过 60 分钟，尚未确认 task 完成；"
        "task 可能仍在 Codex Desktop 中运行，请在桌面版中查看。",
        [],
    )


def rollout_images_since(rollout_path: Path | None, start_offset: int) -> list[str]:
    if rollout_path is None or not rollout_path.is_file():
        return []
    images: list[str] = []
    with rollout_path.open("r", encoding="utf-8") as handle:
        handle.seek(start_offset)
        for line in handle:
            try:
                payload = json.loads(line).get("payload")
            except (AttributeError, json.JSONDecodeError):
                continue
            if not isinstance(payload, dict) or payload.get("type") != "image_generation_end":
                continue
            image = normalized_image_reference(str(payload.get("saved_path") or ""))
            if image is not None and image not in images:
                images.append(image)
    return images


def run_codex_via_desktop(
    thread_id: str,
    prompt: str,
    on_started: Callable[[], None] | None = None,
    input_images: list[str] | None = None,
    input_files: list[dict[str, str]] | None = None,
    on_turn_started: Callable[[str], None] | None = None,
    on_progress: Callable[[str], None] | None = None,
    on_approval: Callable[[dict[str, Any]], None] | None = None,
    cancel_event: threading.Event | None = None,
    cancel_confirmed: threading.Event | None = None,
    on_ipc_ready: Callable[[socket.socket, str], None] | None = None,
    on_ipc_response: Callable[[dict[str, Any]], bool] | None = None,
) -> tuple[str, str, list[str]]:
    rollout_path = rollout_path_for_task(thread_id)
    if not DESKTOP_IPC_SOCKET.exists() or not rollout_path or not rollout_path.exists():
        return "unavailable", "", []
    start_offset = rollout_path.stat().st_size
    turn_request_attempted = False
    turn_confirmed = False
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(30)
            connection.connect(str(DESKTOP_IPC_SOCKET))
            client_id = initialize_desktop_connection(connection)

            request_id = str(uuid.uuid4())
            turn_request = {
                "threadId": thread_id,
                "input": codex_turn_input(
                    prompt,
                    input_images or [],
                    input_files or [],
                ),
            }
            working_directory = task_working_directory(thread_id)
            if working_directory:
                turn_request["cwd"] = working_directory
            turn_request_attempted = True
            send_ipc_message(
                connection,
                {
                    "type": "request",
                    "requestId": request_id,
                    "sourceClientId": client_id,
                    "version": 2,
                    "method": "thread-follower-start-turn",
                    "params": {
                        "conversationId": thread_id,
                        "turnStart": {
                            "request": turn_request,
                            "context": {
                                "inheritThreadSettings": True,
                                "attachments": codex_attachments(input_files or []),
                            },
                        },
                    },
                    "timeoutMs": 30000,
                },
            )
            response = wait_for_ipc_response(connection, request_id)
            if response.get("resultType") != "success":
                error = " ".join(str(response.get("error") or "unknown error").split())[-2000:]
                if error == "no-client-found":
                    return "unavailable", "", []
                log(f"desktop turn failed error={error}")
                if any(marker in error.lower() for marker in ("active turn", "already running", "busy")):
                    return (
                        "failed",
                        "当前 task 正在运行，请稍后重试。",
                        [],
                    )
                return (
                    "failed",
                    "没有成功发送到 Codex Desktop。详细原因已记录到 Mac 的桥接日志。",
                    [],
                )

            turn_id = str(
                response.get("result", {}).get("result", {}).get("turn", {}).get("id") or ""
            )
            if not turn_id:
                log("desktop turn failed error=missing turn id")
                return "failed", "Codex Desktop 已接受消息，但没有返回 turn id。", []
            turn_confirmed = True
            if on_started is not None:
                on_started()
            if on_ipc_ready is not None:
                on_ipc_ready(connection, client_id)
            if on_turn_started is not None:
                on_turn_started(turn_id)
            try:
                remember_bridge_turn(turn_id)
            except OSError as exc:
                log(f"remember bridge turn failed: {type(exc).__name__}: {exc}")

            begin_desktop_following(connection, client_id, thread_id)
            connection.settimeout(1)
            success, result, images = wait_for_desktop_turn(
                rollout_path,
                start_offset,
                turn_id,
                on_progress,
                on_approval,
                cancel_event,
                cancel_confirmed,
                connection,
                client_id,
                on_ipc_response,
            )
            return ("completed" if success else "failed"), result, images
    except (ConnectionError, FileNotFoundError, socket.timeout) as exc:
        if turn_confirmed:
            log(f"desktop turn monitoring failed: {type(exc).__name__}")
            return (
                "failed",
                "Codex Desktop 已开始运行，但桥接与桌面版的状态连接中断。"
                "Task 可能仍在运行，请在桌面版中查看。",
                [],
            )
        if turn_request_attempted:
            log(f"desktop turn confirmation failed: {type(exc).__name__}")
            return (
                "failed",
                "已向 Codex Desktop 发出启动请求，但没有收到确认；"
                "为避免重复执行，本条不会再次提交。请在桌面版查看该 task。",
                [],
            )
        return "unavailable", "", []
    except (OSError, RuntimeError, ValueError, KeyError, json.JSONDecodeError) as exc:
        log(f"desktop IPC failed: {type(exc).__name__}: {exc}")
        if turn_confirmed:
            return (
                "failed",
                "Codex Desktop 已开始运行，但桥接未能继续读取运行状态。"
                "Task 可能仍在运行，请在桌面版中查看。",
                [],
            )
        if turn_request_attempted:
            return (
                "failed",
                "已向 Codex Desktop 发出启动请求，但没有收到确认；"
                "为避免重复执行，本条不会再次提交。请在桌面版查看该 task。",
                [],
            )
        return (
            "failed",
            "没有成功发送到 Codex Desktop。详细原因已记录到 Mac 的桥接日志。",
            [],
        )

def run_codex(
    thread_id: str,
    prompt: str,
    on_started: Callable[[str], None] | None = None,
    input_images: list[str] | None = None,
    input_files: list[dict[str, str]] | None = None,
    on_turn_started: Callable[[str], None] | None = None,
    on_progress: Callable[[str], None] | None = None,
    on_approval: Callable[[dict[str, Any]], None] | None = None,
    cancel_event: threading.Event | None = None,
    cancel_confirmed: threading.Event | None = None,
    on_ipc_ready: Callable[[socket.socket, str], None] | None = None,
    on_ipc_response: Callable[[dict[str, Any]], bool] | None = None,
) -> tuple[bool, str, list[str]]:
    if cancel_event is not None and cancel_event.is_set():
        if cancel_confirmed is not None:
            cancel_confirmed.set()
        return False, "已按你的要求停止运行。", []
    started_notified = False

    def notify_started(status: str) -> None:
        nonlocal started_notified
        if started_notified or on_started is None:
            return
        started_notified = True
        try:
            on_started(status)
        except Exception as exc:
            log(f"running status reply failed: {type(exc).__name__}: {exc}")

    desktop_status, desktop_result, desktop_images = run_codex_via_desktop(
        thread_id,
        prompt,
        on_started=lambda: notify_started("Codex Desktop 已接收，正在运行"),
        input_images=input_images or [],
        input_files=input_files or [],
        on_turn_started=on_turn_started,
        on_progress=on_progress,
        on_approval=on_approval,
        cancel_event=cancel_event,
        cancel_confirmed=cancel_confirmed,
        on_ipc_ready=on_ipc_ready,
        on_ipc_response=on_ipc_response,
    )
    if desktop_status == "completed":
        return True, desktop_result, desktop_images
    if desktop_status == "failed":
        return False, desktop_result, []

    environment = os.environ.copy()
    environment["CODEX_FEISHU_BRIDGE"] = "1"
    rollout_path = rollout_path_for_task(thread_id)
    compatible, compatibility_message = cli_resume_preflight(rollout_path)
    if not compatible:
        log(f"codex resume preflight blocked cli={CODEX_CLI}")
        return False, compatibility_message, []
    rollout_offset = (
        rollout_path.stat().st_size
        if rollout_path and rollout_path.is_file()
        else 0
    )
    with tempfile.TemporaryDirectory(prefix="codex-feishu-") as directory:
        output_path = Path(directory) / "last-message.txt"
        command = [
            CODEX_CLI,
            "exec",
            "resume",
            "--skip-git-repo-check",
            "--output-last-message",
            str(output_path),
        ]
        for image in input_images or []:
            command.extend(["--image", str(Path(image).resolve())])
        command.extend([thread_id, "-"])
        cli_prompt = prompt
        if input_files:
            attachment_lines = [
                f"- {attachment['label']}: {Path(attachment['path']).resolve()}"
                for attachment in input_files
            ]
            cli_prompt = (
                f"{prompt.rstrip()}\n\n"
                "本轮消息包含以下已下载到本机临时目录的飞书附件，请直接读取：\n"
                + "\n".join(attachment_lines)
            ).strip()
        notify_started("正在通过备用 Codex CLI 启动")
        try:
            process = subprocess.Popen(
                command,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                env=environment,
            )
        except OSError as exc:
            log(f"codex resume start failed error={type(exc).__name__}")
            return False, "无法启动本机 Codex CLI，详细原因已记录到桥接日志。", []
        deadline = time.monotonic() + 3600
        last_progress = 0.0
        stdout = ""
        stderr = ""
        first_communicate = True
        while True:
            if cancel_event is not None and cancel_event.is_set():
                process.terminate()
                try:
                    stdout, stderr = process.communicate(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    stdout, stderr = process.communicate()
                if cancel_confirmed is not None:
                    cancel_confirmed.set()
                return False, "已按你的要求停止运行。", []
            now = time.monotonic()
            if now >= deadline:
                process.terminate()
                try:
                    process.communicate(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.communicate()
                return (
                    False,
                    "等待超过 60 分钟，已停止备用 Codex CLI。请在桌面版中查看 task。",
                    [],
                )
            if on_progress is not None and now - last_progress >= 10:
                on_progress("正在通过备用 Codex CLI 运行")
                last_progress = now
            try:
                stdout, stderr = process.communicate(
                    input=cli_prompt if first_communicate else None,
                    timeout=0.5,
                )
                break
            except subprocess.TimeoutExpired:
                first_communicate = False
                continue
        if process.returncode != 0:
            error = " ".join(stderr.strip().split())[-2000:] or "(empty)"
            log(f"codex resume failed code={process.returncode} stderr={error}")
            return (
                False,
                codex_resume_failure_message(stderr),
                [],
            )
        try:
            final_message = output_path.read_text(encoding="utf-8").strip()
        except OSError:
            final_message = ""
        return (
            True,
            final_message or "Codex 已完成，但没有返回文字结果。",
            rollout_images_since(rollout_path, rollout_offset),
        )


def mark_processed(state: dict[str, Any], key: str) -> bool:
    with _state_lock:
        processed = state.setdefault("processed", [])
        if key in processed:
            return False
        processed.append(key)
        state["processed"] = processed[-200:]
        save_state(state)
        return True


def set_run_turn_id(run: dict[str, Any], turn_id: str) -> None:
    with _active_runs_lock:
        run["turn_id"] = turn_id
    request_run_interrupt(run)


def queue_busy_run(
    run: dict[str, Any],
    content: str,
    image_keys: list[str],
    file_keys: list[str],
    raw_content: str,
    message_type: str,
) -> bool:
    original = run.get("queue_entry")
    entry = (
        dict(original)
        if isinstance(original, dict)
        else {
            "queue_id": str(uuid.uuid4()),
            "user_id": str(run["user_id"]),
            "chat_id": str(run["chat_id"]),
            "source_message_id": str(run["source_message_id"]),
            "task": run["task"],
            "content": content,
            "image_keys": image_keys,
            "file_keys": file_keys,
            "raw_content": raw_content,
            "message_type": message_type,
            "created_at": time.time(),
            "progress_message_id": str(run.get("progress_message_id") or ""),
        }
    )
    entry["available_at"] = time.time() + 15
    entry["ready"] = True
    with _state_lock:
        state = load_state()
        entries = pending_inputs(state)
        if not any(
            isinstance(item, dict) and item.get("queue_id") == entry.get("queue_id")
            for item in entries
        ):
            if len(entries) >= MAX_PENDING_INPUTS:
                return False
            same_task_count = sum(
                isinstance(item, dict)
                and str(item.get("task", {}).get("id") or "")
                == str(entry["task"]["id"])
                for item in entries
            )
            if same_task_count >= MAX_PENDING_INPUTS_PER_TASK:
                return False
            entries.insert(0, entry)
            save_state(state)
        position = queued_position(
            entries,
            str(entry["task"]["id"]),
            str(entry["queue_id"]),
        )
    message_id = str(entry.get("progress_message_id") or "")
    if message_id:
        patch_card(
            message_id,
            build_queued_card(
                entry,
                position,
                "Codex Desktop 中的 Task 仍在运行，15 秒后自动重试",
            ),
        )
    log("input queued reason=desktop-task-busy")
    return True


def process_message_run(
    run: dict[str, Any],
    prompt: str,
    image_keys: list[str],
    file_keys: list[str],
    raw_content: str,
    message_type: str,
) -> None:
    message_id = str(run["source_message_id"])
    task = run["task"]
    cancel_event: threading.Event = run["cancel_event"]
    try:
        if run.get("queue_entry") is not None:
            current_task = task_by_id(str(task["id"]), str(run["user_id"]))
            if current_task is None:
                raise RuntimeError(
                    "这条排队消息未执行：Task 已归档、删除或当前用户已无权访问。"
                )
            task = current_task
            run["task"] = current_task
        with tempfile.TemporaryDirectory(prefix="codex-feishu-input-") as directory:
            input_directory = Path(directory)
            input_images: list[str] = []
            input_files: list[dict[str, str]] = []
            if image_keys or file_keys:
                set_run_progress(run, "正在从飞书安全读取附件", force=True)
            for index, image_key in enumerate(image_keys, start=1):
                if cancel_event.is_set():
                    raise InterruptedError
                image, error = download_input_image(
                    message_id,
                    image_key,
                    input_directory,
                    index,
                )
                if image is None:
                    raise RuntimeError(error)
                input_images.append(str(image))
            for index, file_key in enumerate(file_keys, start=1):
                if cancel_event.is_set():
                    raise InterruptedError
                attachment, error = download_input_file(
                    message_id,
                    file_key,
                    input_directory,
                    index,
                    input_file_label(raw_content, file_key, index),
                    message_type,
                )
                if attachment is None:
                    raise RuntimeError(error)
                input_files.append(attachment)

            if cancel_event.is_set():
                raise InterruptedError
            success, result, rollout_images = run_codex(
                str(task["id"]),
                prompt,
                on_started=lambda status: set_run_progress(run, status, force=True),
                input_images=input_images,
                input_files=input_files,
                on_turn_started=lambda turn_id: set_run_turn_id(run, turn_id),
                on_progress=lambda status: set_run_progress(run, status),
                on_approval=lambda approval: publish_approval(run, approval),
                cancel_event=cancel_event,
                cancel_confirmed=run["cancel_confirmed"],
                on_ipc_ready=lambda connection, client_id: set_run_ipc(
                    run,
                    connection,
                    client_id,
                ),
                on_ipc_response=lambda response: complete_run_ipc_response(run, response),
            )
            if success:
                restore_pending_task_name(
                    str(run["user_id"]),
                    str(task["id"]),
                )
    except InterruptedError:
        run["cancel_confirmed"].set()
        success = False
        result = "已按你的要求停止运行。"
        rollout_images = []
    except RuntimeError as exc:
        success = False
        result = str(exc) or "附件处理失败。"
        rollout_images = []
    except Exception as exc:
        log(f"Codex bridge run failed: {type(exc).__name__}: {exc}")
        success = False
        result = "桥接运行异常，详细原因已记录到 Mac 的桥接日志。"
        rollout_images = []

    try:
        if (
            not success
            and "当前 task 正在运行" in result
            and queue_busy_run(
                run,
                prompt,
                image_keys,
                file_keys,
                raw_content,
                message_type,
            )
        ):
            return
        stop_requested = cancel_event.is_set()
        stopped = stop_requested and run["cancel_confirmed"].is_set()
        outcome = "stopped" if stopped else "completed" if success else "failed"
        status = (
            "已停止"
            if stopped
            else "停止未确认"
            if stop_requested
            else "正在发送结果"
            if success
            else "运行未完成"
        )
        set_run_progress(run, status, outcome, force=True)
        label = "已停止" if stopped else "已完成" if success else "未完成"
        prefix = task_status_prefix(task, label)
        clean_result, images = prepare_result_images(result, rollout_images)
        delivered = reply_or_queue(message_id, prefix + clean_result, "final")
        failed_images = 0
        queued_images = 0
        for index, image in enumerate(images, start=1):
            if reply_image(message_id, image, index):
                continue
            failed_images += 1
            if queue_pending_image(
                message_id,
                image,
                index,
                current_reply_failure_reason() or "飞书 API 调用失败",
            ):
                queued_images += 1
        if failed_images:
            reply(
                message_id,
                (
                    f"有 {queued_images} 张图片暂未送达，连接恢复后会自动补发。"
                    if queued_images == failed_images
                    else f"有 {queued_images} 张图片等待自动补发，另有 "
                    f"{failed_images - queued_images} 张无法保存，请在 Codex Desktop 中查看。"
                ),
                "image-error",
            )
        if success:
            delivery_status = "已完成，结果已返回飞书" if delivered else "已完成，结果等待自动补发"
            set_run_progress(run, delivery_status, "completed", force=True)
    finally:
        task_id = str(run["task"]["id"])
        remove_active_run(str(run["run_id"]))
        start_next_queued_input(task_id)


def handle_message_event(event: dict[str, Any]) -> None:
    chat_id = str(event.get("chat_id") or "")
    user_id = str(event.get("sender_id") or "")
    message_type = str(event.get("message_type") or "")
    if (
        event.get("sender_type") != "user"
        or message_type not in {"text", "post", "image", "file", "audio", "media"}
    ):
        return
    message_id = str(event.get("message_id") or "")
    if not authorized_user(user_id):
        if (
            ALLOW_ACCESS_REQUESTS
            and event.get("chat_type") == "p2p"
            and user_id.startswith("ou_")
            and message_id
        ):
            already_pending = record_access_request(
                user_id,
                str(event.get("sender_name") or event.get("operator_name") or "").strip(),
            )
            reply_card(
                message_id,
                build_access_request_card(already_pending),
                "access-request-status",
            )
            log("access request recorded")
        return
    raw_content = str(event.get("content") or "")
    image_keys = input_image_keys(raw_content) if message_type in {"post", "image", "media"} else []
    file_keys = input_file_keys(raw_content) if message_type in {"post", "file", "audio", "media"} else []
    content = input_prompt(raw_content, image_keys, file_keys, message_type)
    if not message_id or (not content and not image_keys and not file_keys):
        return
    if (
        workflow_notifications_enabled()
        and not image_keys
        and not file_keys
        and handle_workflow_text_reply(event, content)
    ):
        return

    with _state_lock:
        state = load_state()
        if event.get("chat_type") == "p2p":
            authorize_chat(state, user_id, chat_id)
        elif not is_authorized_chat(state, user_id, chat_id):
            return
        if not mark_processed(state, message_id):
            return

    pending_project = str(
        state.get("pending_task_creations", {}).get(user_id) or ""
    )
    if pending_project and not image_keys and not file_keys:
        if content in {"取消新建", "取消", "/cancel"}:
            with _state_lock:
                state = load_state()
                state.setdefault("pending_task_creations", {}).pop(user_id, None)
                save_state(state)
            reply(message_id, "已取消新建 Task。", "new-task-canceled")
            return
        with _state_lock:
            state = load_state()
            state.setdefault("pending_task_creations", {}).pop(user_id, None)
            save_state(state)
        reply(
            message_id,
            f"正在项目“{pending_project}”中新建 Task…",
            "new-task-creating",
        )
        threading.Thread(
            target=complete_task_creation,
            args=(message_id, user_id, pending_project, content),
            daemon=True,
            name="codex-feishu-create-task",
        ).start()
        return

    if not image_keys and not file_keys and content in {"帮助", "/help", "help"}:
        reply(message_id, help_text(), "help")
        return
    if not image_keys and not file_keys and content in {"对话", "任务", "/list", "list"}:
        if not reply_task_card(message_id, user_id, state):
            reply(message_id, show_tasks(user_id, state), "list-fallback")
        return
    if not image_keys and not file_keys and content in {"当前", "/current", "current"}:
        reply(message_id, current_task(user_id, state), "current")
        return
    search_match = re.fullmatch(r"(?:/)?(?:搜索|search)\s+(.+)", content, re.IGNORECASE)
    if not image_keys and not file_keys and search_match:
        query = " ".join(search_match.group(1).split())[:80]
        with _state_lock:
            state = load_state()
            state.setdefault("task_queries", {})[user_id] = query
            state.setdefault("task_pages", {})[user_id] = 0
            save_state(state)
        if not reply_task_card(message_id, user_id, state):
            reply(message_id, "Task 搜索卡片发送失败，请稍后重试。", "search-fallback")
        return
    match = re.fullmatch(r"(?:/)?(?:选择|使用|use)\s+(.+)", content, re.IGNORECASE)
    if not image_keys and not file_keys and match:
        reply(
            message_id,
            select_task(user_id, match.group(1).strip(), state),
            "select",
        )
        return
    if len(content) > MAX_PROMPT_CHARS:
        reply(message_id, f"消息超过 {MAX_PROMPT_CHARS} 字，请缩短后重试。", "too-long")
        return
    if len(image_keys) > MAX_INPUT_IMAGES:
        reply(
            message_id,
            f"一次最多发送 {MAX_INPUT_IMAGES} 张图片，请分开发送。",
            "too-many-images",
        )
        return
    if len(file_keys) > MAX_INPUT_FILES:
        reply(
            message_id,
            f"一次最多发送 {MAX_INPUT_FILES} 个文件，请分开发送。",
            "too-many-files",
        )
        return

    with _state_lock:
        state = load_state()
        task = selected_task(user_id, state)
    if not task:
        reply(
            message_id,
            "尚未选择 Codex task。请点击机器人菜单中的“选择 Task”。",
            "no-selection",
        )
        return

    run = new_run(
        user_id,
        chat_id,
        message_id,
        task,
        image_keys,
        file_keys,
    )
    if not claim_active_run(run):
        entry = {
            "queue_id": str(uuid.uuid4()),
            "user_id": user_id,
            "chat_id": chat_id,
            "source_message_id": message_id,
            "task": task,
            "content": content,
            "image_keys": image_keys,
            "file_keys": file_keys,
            "raw_content": raw_content,
            "message_type": message_type,
            "created_at": time.time(),
            "available_at": 0,
            "ready": False,
        }
        queued, position, error = enqueue_pending_input(entry)
        if not queued:
            reply(message_id, error, "queue-full")
            return
        try:
            card_sent, progress_message_id = reply_card_message(
                message_id,
                build_queued_card(entry, position),
                f"queue-{entry['queue_id']}",
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            log(f"queue card reply failed error={type(exc).__name__}")
            card_sent, progress_message_id = False, None
        if card_sent and progress_message_id:
            update_pending_input(
                str(entry["queue_id"]),
                progress_message_id=progress_message_id,
                ready=True,
            )
        else:
            update_pending_input(str(entry["queue_id"]), ready=True)
            queue_pending_queue_card(
                str(entry["queue_id"]),
                message_id,
                current_reply_failure_reason() or "飞书 API 调用失败",
            )
            reply(
                message_id,
                task_status_prefix(task, f"已排队（第 {position} 条）")
                + "当前运行完成后会自动执行。",
                "task-queued",
            )
        log(f"input queued position={position} attachments={len(image_keys) + len(file_keys)}")
        start_next_queued_input(str(task["id"]))
        return
    start_claimed_run(run, content, image_keys, file_keys, raw_content, message_type)


def handle_card_event(event: dict[str, Any]) -> None:
    chat_id = str(event.get("chat_id") or "")
    user_id = str(event.get("operator_id") or "")
    action_name = str(event.get("action_name") or "")
    action_tag = str(event.get("action_tag") or "")
    if not authorized_user(user_id):
        log("card ignored reason=unauthorized-user")
        return
    event_id = str(event.get("event_id") or "")
    message_id = str(event.get("message_id") or "")
    if not event_id:
        return

    if action_tag == "button":
        payload = action_payload(event)
        action = str(payload.get("action") or "")
        if workflow_notifications_enabled() and handle_workflow_card_action(event, payload):
            return
        if action == "cancel_queued_input":
            with _state_lock:
                state = load_state()
                if not mark_processed(state, f"card:{event_id}"):
                    return
            entry = cancel_pending_input(
                str(payload.get("queue_id") or ""),
                user_id,
                chat_id,
            )
            if entry is None:
                return
            progress_message_id = str(
                message_id or entry.get("progress_message_id") or ""
            )
            if progress_message_id:
                patch_card(
                    progress_message_id,
                    build_queued_card(entry, 0, canceled=True),
                )
            task_id = str(entry["task"]["id"])
            refresh_queued_cards(task_id)
            log("queued input canceled")
            return
        if action in {
            "task_page",
            "archived_task_page",
            "clear_task_search",
            "new_task",
            "cancel_new_task",
            "archive_task",
            "cancel_archive",
            "restore_task",
            "show_task_selector",
            "show_archived_tasks",
            "show_new_task",
        }:
            with _state_lock:
                state = load_state()
                if not mark_processed(state, f"card:{event_id}"):
                    return
                if action == "task_page":
                    try:
                        page = max(0, int(payload.get("page") or 0))
                    except (TypeError, ValueError):
                        page = 0
                    state.setdefault("task_pages", {})[user_id] = page
                    save_state(state)
                    card = task_card_for_state(user_id, state)
                elif action == "archived_task_page":
                    try:
                        page = max(0, int(payload.get("page") or 0))
                    except (TypeError, ValueError):
                        page = 0
                    state.setdefault("archived_task_pages", {})[user_id] = page
                    save_state(state)
                    card = archived_task_card_for_state(user_id, state)
                elif action == "clear_task_search":
                    state.setdefault("task_queries", {}).pop(user_id, None)
                    state.setdefault("task_pages", {})[user_id] = 0
                    save_state(state)
                    card = task_card_for_state(user_id, state)
                elif action == "show_task_selector":
                    save_state(state)
                    card = task_card_for_state(user_id, state)
                elif action == "show_archived_tasks":
                    state.setdefault("archived_task_pages", {})[user_id] = 0
                    save_state(state)
                    card = archived_task_card_for_state(user_id, state)
                elif action == "show_new_task":
                    projects = available_project_names(user_id)
                    selected_project = str(
                        state.setdefault("last_projects", {}).get(user_id) or ""
                    )
                    save_state(state)
                    card = build_new_task_card(projects, selected_project)
                elif action == "cancel_new_task":
                    state.setdefault("pending_task_creations", {}).pop(user_id, None)
                    save_state(state)
                    card = build_new_task_card([], canceled=True)
                    log("new task creation canceled")
                elif action == "new_task":
                    requested_project = str(payload.get("project") or "")
                    projects = set(available_project_names(user_id))
                    latest_project = str(
                        state.setdefault("last_projects", {}).get(user_id) or ""
                    )
                    project = (
                        latest_project
                        if latest_project in projects
                        else requested_project
                    )
                    if project not in projects:
                        return
                    state.setdefault("pending_task_creations", {})[user_id] = project
                    save_state(state)
                    reply(
                        message_id,
                        f"准备在项目“{project}”中新建 Task。请发送 Task 标题；发送“取消新建”可退出。",
                        f"new-task-ready-{event_id}",
                    )
                    return
                elif action == "cancel_archive":
                    task = selected_task(user_id, state)
                    save_state(state)
                    card = build_archive_task_card(task, canceled=True)
                    log("task archive canceled")
                elif action == "restore_task":
                    requested_task_id = str(payload.get("task_id") or "")
                    task = next(
                        (
                            candidate
                            for candidate in archived_tasks(user_id)
                            if str(candidate["id"]) == requested_task_id
                        ),
                        None,
                    )
                    if task is None:
                        reply(
                            message_id,
                            "该 Task 已经恢复、删除或当前用户已无权访问。",
                            f"restore-stale-{event_id}",
                        )
                        return
                    try:
                        restore_codex_task(user_id, task)
                    except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
                        log(f"task restore failed error={type(exc).__name__}")
                        reply(
                            message_id,
                            "没有成功恢复 Task，请在 Codex Desktop 中重试。",
                            f"restore-error-{event_id}",
                        )
                        return
                    state = load_state()
                    state.setdefault("selected", {})[user_id] = task["id"]
                    state.setdefault("last_projects", {})[user_id] = task["project"]
                    state.setdefault("task_pages", {})[user_id] = 0
                    save_state(state)
                    card = build_archive_task_card(task, restored=True)
                    reply(
                        message_id,
                        current_task_changed_text(task, "已恢复"),
                        f"restored-{event_id}",
                    )
                else:
                    task = selected_task(user_id, state)
                    if task is None:
                        reply(
                            message_id,
                            "尚未选择 Task，请先点击机器人菜单中的“选择 Task”。",
                            f"archive-no-task-{event_id}",
                        )
                        return
                    requested_task_id = str(payload.get("task_id") or "")
                    if requested_task_id and requested_task_id != str(task["id"]):
                        reply(
                            message_id,
                            "当前 Task 已经变化，请重新点击机器人菜单中的“归档 Task”。",
                            f"archive-stale-{event_id}",
                        )
                        return
                    if active_run_for_task(str(task["id"])) is not None:
                        reply(
                            message_id,
                            "当前 Task 正在运行，完成或停止后才能归档。",
                            f"archive-busy-{event_id}",
                        )
                        return
                    try:
                        archive_codex_task(user_id, task)
                    except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
                        log(f"task archive failed error={type(exc).__name__}")
                        reply(
                            message_id,
                            "没有成功归档 Task，请在 Codex Desktop 中重试。",
                            f"archive-error-{event_id}",
                        )
                        return
                    state = load_state()
                    state.setdefault("selected", {}).pop(user_id, None)
                    save_state(state)
                    card = build_archive_task_card(task, archived=True)
                    reply(
                        message_id,
                        f"已归档：{option_text(task)}",
                        f"archived-{event_id}",
                    )
            token = str(event.get("token") or "")
            if message_id:
                with _state_lock:
                    current_state = load_state()
                    remember_card_context(current_state, user_id, message_id, card)
            if token and update_card(token, card):
                return
            if message_id:
                patch_card(message_id, card)
            return
        run = active_run(str(payload.get("run_id") or ""))
        if (
            run is None
            or run.get("user_id") != user_id
            or (chat_id and run.get("chat_id") != chat_id)
        ):
            return
        with _state_lock:
            state = load_state()
            if not mark_processed(state, f"card:{event_id}"):
                return
        if action == "stop_run":
            cancel_event = run.get("cancel_event")
            if isinstance(cancel_event, threading.Event):
                cancel_event.set()
            set_run_progress(run, "正在停止当前运行", force=True)
            request_run_interrupt(run)
            return
        if action in {"approve_once", "decline"}:
            request_id = str(payload.get("request_id") or "")
            with _active_runs_lock:
                approval = run.get("approvals", {}).get(request_id)
            if not isinstance(approval, dict) or approval.get("resolved"):
                return
            threading.Thread(
                target=handle_approval_action,
                args=(run, approval, action == "approve_once", event),
                daemon=True,
                name=f"codex-feishu-approval-{request_id[:8]}",
            ).start()
        return

    selected_value = str(event.get("option") or "")
    try:
        original_card = json.loads(str(event.get("card_content") or ""))
    except json.JSONDecodeError:
        original_card = None
    with _state_lock:
        context_type = card_context_for_event(load_state(), user_id, message_id)
    elements = (
        original_card.get("body", {}).get("elements", [])
        if isinstance(original_card, dict)
        else []
    )
    recognized_new_task_card = any(
        isinstance(element, dict)
        and element.get("tag") == "select_static"
        and element.get("name") == "new_task_project_selector"
        for element in elements
    ) or context_type == "new_task"
    if action_tag == "select_static" and (
        action_name == "new_task_project_selector"
        or (not action_name and recognized_new_task_card)
    ):
        projects = available_project_names(user_id)
        if not selected_value or selected_value not in projects:
            log("card ignored reason=unknown-new-task-project")
            return
        with _state_lock:
            state = load_state()
            if chat_id and not is_authorized_chat(state, user_id, chat_id):
                if not recognized_new_task_card:
                    log("card ignored reason=unrecognized-chat-and-card")
                    return
                authorize_chat(state, user_id, chat_id)
            if not mark_processed(state, f"card:{event_id}"):
                return
            state.setdefault("last_projects", {})[user_id] = selected_value
            save_state(state)
            card = build_new_task_card(projects, selected_value)
            if message_id:
                remember_card_context(state, user_id, message_id, card)
        token = str(event.get("token") or "")
        if token and update_card(token, card):
            return
        if message_id:
            patch_card(message_id, card)
        return

    recognized_archived_card = any(
        isinstance(element, dict)
        and element.get("tag") == "select_static"
        and element.get("name")
        in {"archived_project_selector", "archived_task_selector"}
        for element in elements
    ) or context_type == "archived_tasks"
    if action_tag == "select_static" and (
        action_name in {"archived_project_selector", "archived_task_selector"}
        or (not action_name and recognized_archived_card)
    ):
        archived = archived_tasks(user_id)
        projects = {task["project"] for task in archived}
        task_ids = {task["id"] for task in archived}
        if not action_name:
            if selected_value in projects:
                action_name = "archived_project_selector"
            elif selected_value in task_ids:
                action_name = "archived_task_selector"
            else:
                log("card ignored reason=unknown-archived-selector")
                return
        with _state_lock:
            state = load_state()
            if chat_id and not is_authorized_chat(state, user_id, chat_id):
                if not recognized_archived_card:
                    log("card ignored reason=unrecognized-chat-and-card")
                    return
                authorize_chat(state, user_id, chat_id)
            if not mark_processed(state, f"card:{event_id}"):
                return
            if action_name == "archived_project_selector":
                if selected_value not in projects:
                    log("card ignored reason=unknown-archived-project")
                    return
                state.setdefault("archived_last_projects", {})[user_id] = selected_value
                state.setdefault("archived_task_pages", {})[user_id] = 0
                save_state(state)
                card = archived_task_card_for_state(user_id, state)
            else:
                selected = next(
                    (task for task in archived if task["id"] == selected_value),
                    None,
                )
                if selected is None:
                    if message_id:
                        reply(
                            message_id,
                            "该 Task 已经恢复或删除，请重新选择。",
                            f"archived-stale-{event_id}",
                        )
                    return
                state.setdefault("archived_last_projects", {})[user_id] = selected[
                    "project"
                ]
                save_state(state)
                card = archived_task_card_for_state(
                    user_id,
                    state,
                    selected["id"],
                )
            if message_id:
                remember_card_context(state, user_id, message_id, card)
        token = str(event.get("token") or "")
        if token and update_card(token, card):
            return
        if message_id:
            patch_card(message_id, card)
        return

    tasks = recent_tasks(user_id)
    recognized_card = any(
        isinstance(element, dict)
        and element.get("tag") == "select_static"
        and element.get("name") in {"project_selector", "task_selector"}
        for element in elements
    ) or context_type == "tasks"
    if action_tag != "select_static":
        log(
            "card ignored reason=unsupported-action "
            f"tag={action_tag or 'missing'} name={action_name or 'missing'}"
        )
        return
    if not selected_value:
        log(
            "card ignored reason=missing-option "
            f"name={action_name or 'missing'} card_recognized={recognized_card}"
        )
        return
    if action_name not in {"project_selector", "task_selector"}:
        projects = {task["project"] for task in tasks}
        task_ids = {task["id"] for task in tasks}
        if recognized_card and selected_value in projects:
            action_name = "project_selector"
        elif recognized_card and selected_value in task_ids:
            action_name = "task_selector"
        else:
            log(
                "card ignored reason=unknown-selector "
                f"name={action_name or 'missing'} card_recognized={recognized_card}"
            )
            return
        log(f"card selector inferred name={action_name}")
    with _state_lock:
        state = load_state()
        if chat_id and not is_authorized_chat(state, user_id, chat_id):
            if not recognized_card:
                log("card ignored reason=unrecognized-chat-and-card")
                return
            authorize_chat(state, user_id, chat_id)
        if not mark_processed(state, f"card:{event_id}"):
            return
        if action_name == "project_selector":
            projects = {task["project"] for task in tasks}
            if selected_value not in projects:
                log("card ignored reason=unknown-project")
                return
            state.setdefault("last_projects", {})[user_id] = selected_value
            state.setdefault("task_pages", {})[user_id] = 0
            state.setdefault("task_queries", {}).pop(user_id, None)
            selected = selected_task(user_id, state)
            save_state(state)
            card = build_task_card(
                tasks,
                selected["id"] if selected else None,
                selected_value,
            )
        else:
            selected = next((task for task in tasks if task["id"] == selected_value), None)
            if selected is None:
                if message_id:
                    reply(message_id, "该 Task 已归档或删除，请重新选择。", f"stale-{event_id}")
                return
            state.setdefault("selected", {})[user_id] = selected["id"]
            state.setdefault("last_projects", {})[user_id] = selected["project"]
            save_state(state)
            card = build_task_card(
                tasks,
                selected["id"],
                selected["project"],
                selection_changed=True,
            )
        if message_id:
            remember_card_context(state, user_id, message_id, card)
    visible_count = len(
        tasks
        if action_name != "project_selector"
        else [task for task in tasks if task["project"] == selected_value]
    )
    log(
        f"card selection saved name={action_name} "
        f"visible_tasks={visible_count} update_token={bool(event.get('token'))}"
    )
    token = str(event.get("token") or "")
    if token and update_card(token, card):
        log(f"card selection updated name={action_name}")
        return
    if message_id:
        if action_name == "project_selector":
            reply(message_id, "项目筛选已更新，请重新打开 Task 菜单。", f"project-{event_id}")
        else:
            reply(
                message_id,
                current_task_changed_text(selected),
                f"selected-{event_id}",
            )


def handle_menu_event(event: dict[str, Any]) -> None:
    user_id = str(event.get("operator_id") or "")
    event_key = str(event.get("event_key") or "")
    if event_key not in {
        TASK_MENU_EVENT_KEY,
        NEW_TASK_MENU_EVENT_KEY,
        ARCHIVE_TASK_MENU_EVENT_KEY,
    } or not authorized_user(user_id):
        return
    event_id = str(event.get("event_id") or "")
    if not event_id:
        return
    state = load_state()
    if not mark_processed(state, f"menu:{event_id}"):
        return
    if event_key == TASK_MENU_EVENT_KEY:
        send_task_card(user_id, state, event_id)
        return
    if event_key == NEW_TASK_MENU_EVENT_KEY:
        projects = available_project_names(user_id)
        selected_project = str(
            state.setdefault("last_projects", {}).get(user_id) or ""
        )
        send_menu_card(
            user_id,
            state,
            build_new_task_card(projects, selected_project),
            f"new-task-{event_id}",
        )
        return
    task = selected_task(user_id, state)
    busy = bool(task and active_run_for_task(str(task["id"])) is not None)
    send_menu_card(
        user_id,
        state,
        build_archive_task_card(task, busy=busy),
        f"archive-task-{event_id}",
    )


def dispatch_event(event: dict[str, Any]) -> None:
    if event.get("type") == "card.action.trigger":
        handle_card_event(event)
    elif event.get("type") == "application.bot.menu_v6":
        handle_menu_event(event)
    else:
        handle_message_event(event)


def tag_workflow_decision_inbox_event(event: dict[str, Any]) -> None:
    if (
        event.get("type") == "card.action.trigger"
        and action_payload(event).get("action") == "workflow_decision"
    ):
        event_id = str(event.get("event_id") or "")
        if event_id:
            event["_workflow_inbox_event_id"] = event_id


def enqueue_workflow_decision_inbox(events: queue.Queue[dict[str, Any]]) -> None:
    for event in _workflow_decision_inbox.pending():
        events.put(event)


def acknowledge_workflow_decision_inbox(event: dict[str, Any]) -> None:
    event_id = str(event.get("_workflow_inbox_event_id") or "")
    if event_id:
        _workflow_decision_inbox.acknowledge(event_id)


def log_consumer_stderr(stream: Any) -> None:
    for line in stream:
        log(line.rstrip())


def enqueue_events(stream: Any, events: queue.Queue[dict[str, Any]]) -> None:
    for line in stream:
        try:
            event = json.loads(line)
            if not isinstance(event, dict):
                raise json.JSONDecodeError("event must be an object", line, 0)
            tag_workflow_decision_inbox_event(event)
            events.put(event)
        except json.JSONDecodeError as exc:
            log(f"invalid event JSON: {exc}")


def stop(_signum: int, _frame: Any) -> None:
    stop_workflow_socket_server()
    for consumer in _consumers:
        if consumer.poll() is None:
            consumer.terminate()


def diagnostic_report() -> dict[str, Any]:
    try:
        config_permissions = CONFIG_PATH.stat().st_mode & 0o777 == 0o600
    except OSError:
        config_permissions = False
    checks = {
        "config_file": CONFIG_PATH.is_file(),
        "config_permissions": config_permissions,
        "allowed_users": allowed_users_config_valid(),
        "lark_cli": bool(LARK_CLI and Path(LARK_CLI).is_file()),
        "codex_cli": bool(CODEX_CLI and Path(CODEX_CLI).is_file()),
        "codex_state_db": state_db_path().is_file(),
        "desktop_catalog_db": DESKTOP_CATALOG_DB.is_file(),
        "workflow_configuration": workflow_configuration_valid(),
    }
    return {
        "ok": all(checks.values()),
        "checks": checks,
        "paths": {
            "config": str(CONFIG_PATH),
            "state": str(STATE_PATH),
            "log": str(LOG_PATH),
            "lark_cli": LARK_CLI,
            "codex_cli": CODEX_CLI,
        },
        "lark_profile": LARK_PROFILE,
        "authorized_user_count": len(ALLOWED_USERS),
    }


def self_test() -> int:
    report = diagnostic_report()
    assert report["ok"], json.dumps(report, ensure_ascii=False)
    assert Path(CODEX_CLI).is_file()
    assert normalized_content("@Codex 对话") == "对话"
    assert normalized_content(" 选择 2 ") == "选择 2"
    assert idempotency_key("om_test", "result") == idempotency_key("om_test", "result")
    assert sent_chat_id('{"data":{"chat_id":"oc_test"}}') == "oc_test"
    assert sent_chat_id('{"data":{"message":{"chat_id":"oc_nested"}}}') == "oc_nested"
    assert PRIMARY_ALLOWED_USER
    assert authorized_user(PRIMARY_ALLOWED_USER)
    assert not authorized_user("ou_not_authorized_self_test")
    tasks = recent_tasks(PRIMARY_ALLOWED_USER)
    assert tasks and all(task["id"] and task["title"] for task in tasks)
    assert task_by_id(tasks[0]["id"], PRIMARY_ALLOWED_USER) == tasks[0]
    card = build_task_card(tasks, tasks[0]["id"])
    assert card["schema"] == "2.0"
    selectors = {
        element.get("name"): element
        for element in card["body"]["elements"]
        if isinstance(element, dict) and element.get("tag") == "select_static"
    }
    assert selectors["task_selector"]["initial_option"] == tasks[0]["id"]
    assert [
        option["text"]["content"]
        for option in selectors["task_selector"]["options"]
    ] == [
        option_text(task)
        for task in tasks
        if task["project"] == tasks[0]["project"]
    ]
    assert card["header"]["title"]["content"] == task_title_text(tasks[0])
    assert card["header"]["subtitle"]["content"] == task_project_text(tasks[0])
    assert card["header"]["text_tag_list"][0]["text"]["content"] == "当前 Task"
    updated = updated_task_card(json.dumps(card), tasks[-1], tasks)
    assert updated is not None
    assert updated["header"]["template"] == "green"
    updated_selector = next(
        element
        for element in updated["body"]["elements"]
        if isinstance(element, dict) and element.get("name") == "task_selector"
    )
    assert updated_selector["initial_option"] == tasks[-1]["id"]

    test_user = "ou_project_filter_self_test"
    project = tasks[0]["project"]
    ALLOWED_USERS[test_user] = {project}
    try:
        filtered = recent_tasks(test_user)
        assert filtered and all(task["project"] == project for task in filtered)
        assert task_by_id(filtered[0]["id"], test_user) == filtered[0]
        hidden = next((task for task in tasks if task["project"] != project), None)
        if hidden:
            assert task_by_id(hidden["id"], test_user) is None
    finally:
        ALLOWED_USERS.pop(test_user, None)
    print(f"self-test passed; {len(tasks)} recent tasks visible")
    return 0


def main() -> int:
    if sys.argv[1:] == ["--diagnose-json"]:
        report = diagnostic_report()
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0 if report["ok"] else 1
    if sys.argv[1:] == ["--self-test"]:
        return self_test()
    if sys.argv[1:] == ["--print-task-card"]:
        state = load_state()
        print(
            json.dumps(
                task_card_for_state(PRIMARY_ALLOWED_USER, state),
                ensure_ascii=False,
            )
        )
        return 0
    report = diagnostic_report()
    if not report["ok"]:
        log("bridge refused to start: " + json.dumps(report, ensure_ascii=False))
        return 2
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    events: queue.Queue[dict[str, Any]] = queue.Queue()
    if workflow_notifications_enabled() and not start_workflow_socket_server():
        log("bridge refused to start: workflow endpoint unavailable")
        return 2
    if workflow_notifications_enabled():
        try:
            enqueue_workflow_decision_inbox(events)
        except WorkflowStateError:
            log("bridge refused to start: workflow decision inbox unavailable")
            stop_workflow_socket_server()
            return 2
    for event_key in EVENT_KEYS:
        command = [
            LARK_CLI,
            "--profile",
            LARK_PROFILE,
            "event",
            "consume",
            event_key,
            "--as",
            "bot",
        ]
        consumer_environment = lark_environment()
        if event_key == "card.action.trigger" and workflow_notifications_enabled():
            consumer_environment["CODEX_FEISHU_WORKFLOW_DECISION_INBOX"] = str(
                WORKFLOW_DECISION_INBOX_PATH
            )
        consumer = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
            env=consumer_environment,
        )
        _consumers.append(consumer)
        assert consumer.stdout is not None and consumer.stderr is not None
        threading.Thread(
            target=log_consumer_stderr,
            args=(consumer.stderr,),
            daemon=True,
        ).start()
        threading.Thread(
            target=enqueue_events,
            args=(consumer.stdout, events),
            daemon=True,
        ).start()

    next_pending_retry = 0.0
    next_input_retry = 0.0
    next_workflow_retry = 0.0
    while any(consumer.poll() is None for consumer in _consumers):
        now = time.time()
        if now >= next_pending_retry:
            try:
                retry_pending_replies(now)
            except Exception as exc:
                log(f"pending reply loop failed: {type(exc).__name__}: {exc}")
            next_pending_retry = now + 1
        if now >= next_input_retry:
            try:
                start_pending_inputs(now)
            except Exception as exc:
                log(f"pending input loop failed: {type(exc).__name__}: {exc}")
            next_input_retry = now + 1
        if now >= next_workflow_retry:
            try:
                if workflow_notifications_enabled():
                    enqueue_workflow_decision_inbox(events)
                retry_workflow_notifications(now)
                retry_workflow_recoveries(now)
            except Exception as exc:
                log(f"workflow loop failed: {type(exc).__name__}: {exc}")
            next_workflow_retry = now + 1
        try:
            event = events.get(timeout=0.5)
        except queue.Empty:
            continue
        try:
            dispatch_event(event)
            acknowledge_workflow_decision_inbox(event)
        except Exception as exc:
            log(f"event failed: {type(exc).__name__}: {exc}")
    return next(
        (consumer.returncode for consumer in _consumers if consumer.returncode),
        0,
    )


if __name__ == "__main__":
    raise SystemExit(main())
