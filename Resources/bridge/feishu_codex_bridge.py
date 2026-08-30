#!/usr/bin/env python3
"""Route authorized Feishu messages to a selected local Codex task."""

from __future__ import annotations

from datetime import datetime, timezone
import fcntl
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
from urllib.error import URLError
from urllib.parse import unquote, urlsplit
from urllib.request import Request, urlopen
import uuid


BRIDGE_RESOURCE_DIR = Path(__file__).resolve().parent
if str(BRIDGE_RESOURCE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_RESOURCE_DIR))

try:
    from workflow_notifications import (  # noqa: E402
        ORI_ONE_WORKFLOW_ID,
        WorkflowNotificationError,
        WorkflowDecisionInbox,
        WorkflowStateError,
        WorkflowStore,
        event_key as workflow_event_key,
        validate_payload as validate_workflow_payload,
    )
except ModuleNotFoundError as exc:
    if exc.name != "workflow_notifications":
        raise
    ORI_ONE_WORKFLOW_ID = ""
    WorkflowNotificationError = ValueError
    WorkflowStateError = RuntimeError
    WorkflowDecisionInbox = None
    WorkflowStore = None
    workflow_event_key = None
    validate_workflow_payload = None


WORKFLOW_EXTENSION_AVAILABLE = WorkflowStore is not None


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
_workflow_store = (
    WorkflowStore(WORKFLOW_STATE_PATH) if WORKFLOW_EXTENSION_AVAILABLE else None
)
_workflow_decision_inbox = (
    WorkflowDecisionInbox(WORKFLOW_DECISION_INBOX_PATH)
    if WORKFLOW_EXTENSION_AVAILABLE
    else None
)
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
MAX_RESULT_AUDIO = max(0, int(CONFIG.get("max_result_audio", 4)))
MAX_RESULT_FILES = max(0, int(CONFIG.get("max_result_files", 4)))
MAX_RESULT_FILE_BYTES = max(
    1,
    int(CONFIG.get("max_result_file_bytes", 50 * 1024 * 1024)),
)
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
NATIVE_AUDIO_SUFFIXES = {".ogg", ".opus"}
FILE_SUFFIXES = DOCUMENT_SUFFIXES | AUDIO_SUFFIXES
MARKDOWN_IMAGE_PATTERN = re.compile(r"!\[[^\]\n]*\]\(\s*(<[^>\n]+>|[^)\n]+)\s*\)")
MARKDOWN_FILE_PATTERN = re.compile(
    r"(?<!!)\[([^\]\n]*)\]\(\s*(<[^>\n]+>|[^)\n]+)\s*\)"
)
MARKDOWN_AUDIO_PATTERN = re.compile(
    r"!?\[([^\]\n]*)\]\(\s*(<[^>\n]+>|[^)\n]+)\s*\)"
)
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
CURRENT_TASK_MENU_EVENT_KEY = str(
    CONFIG.get("current_task_menu_event_key") or "current_task"
)
TASK_MENU_EVENT_KEY = str(CONFIG.get("task_menu_event_key") or "select_task")
NEW_TASK_MENU_EVENT_KEY = str(
    CONFIG.get("new_task_menu_event_key") or "new_task"
)
ARCHIVE_TASK_MENU_EVENT_KEY = str(
    CONFIG.get("archive_task_menu_event_key") or "archive_task"
)
USAGE_MENU_EVENT_KEY = str(
    CONFIG.get("usage_menu_event_key") or "codex_usage"
)
DESKTOP_SYNC_MENU_EVENT_KEY = str(
    CONFIG.get("desktop_sync_menu_event_key") or "sync_desktop"
)
DESKTOP_SYNC_SWITCH_MENU_EVENT_KEY = str(
    CONFIG.get("desktop_sync_switch_menu_event_key") or "sync_desktop_switch"
)
TASK_SUBSCRIPTIONS_MENU_EVENT_KEY = str(
    CONFIG.get("task_subscriptions_menu_event_key") or "task_subscriptions"
)
TASK_SETTINGS_MENU_EVENT_KEY = str(
    CONFIG.get("task_settings_menu_event_key") or "task_settings"
)
COMPACT_CONTEXT_MENU_EVENT_KEY = str(
    CONFIG.get("compact_context_menu_event_key") or "compact_task_context"
)
PROMLIGHT_MENU_EVENT_KEY = str(
    CONFIG.get("promlight_menu_event_key") or "promlight"
)
PROMLIGHT_LEGEND_MENU_EVENT_KEY = str(
    CONFIG.get("promlight_legend_menu_event_key") or "promlight_legend"
)
PROMLIGHT_API_BASE = "http://127.0.0.1:7800"
PROMLIGHT_HTTP_TIMEOUT_SECONDS = 2
PROMLIGHT_TASK_STATUSES = {"idle", "running", "human_gate", "error", "unknown"}
PROMLIGHT_STATUS_PRIORITY = {
    "unknown": 0,
    "idle": 1,
    "running": 2,
    "human_gate": 3,
    "error": 4,
}
PROMLIGHT_STATUS_COMMANDS = {
    "idle": "led green on --only",
    "running": "led yellow on --only",
    "human_gate": "led yellow blink --only",
    "error": "led red blink --only",
}
PROMLIGHT_LEGEND_TEXT = (
    "绿灯常亮：已完成，当前无需处理\n"
    "黄灯常亮：正在处理中\n"
    "黄灯闪烁：需要你处理\n"
    "红灯闪烁：执行出错，请查看 Task"
)
REPLY_RETRY_DELAYS = (1.0, 2.0)
CARD_PATCH_RETRY_DELAYS: tuple[float, ...] = ()
CARD_PATCH_TIMEOUT_SECONDS = 3
CARD_SEND_TIMEOUT_SECONDS = 5
PENDING_REPLY_DELAYS = (15, 30, 60, 120, 300, 600)
PENDING_CARD_PATCH_DELAYS = (2, 5, 15, 30, 60, 120)
MAX_PENDING_REPLIES = 50
MAX_PROCESSED_EVENTS = 10_000
PROCESSED_EVENT_TTL_SECONDS = 7 * 24 * 60 * 60
MAX_PENDING_IMAGE_BYTES = max(
    1,
    int(CONFIG.get("max_pending_image_bytes", 20 * 1024 * 1024)),
)
MAX_PENDING_IMAGE_SPOOL_BYTES = max(
    MAX_PENDING_IMAGE_BYTES,
    int(CONFIG.get("max_pending_image_spool_bytes", 100 * 1024 * 1024)),
)
MAX_PENDING_FILE_BYTES = max(
    MAX_RESULT_FILE_BYTES,
    int(CONFIG.get("max_pending_file_bytes", 50 * 1024 * 1024)),
)
MAX_PENDING_FILE_SPOOL_BYTES = max(
    MAX_PENDING_FILE_BYTES,
    int(CONFIG.get("max_pending_file_spool_bytes", 200 * 1024 * 1024)),
)
MAX_PENDING_INPUTS = max(1, int(CONFIG.get("max_pending_inputs", 50)))
MAX_PENDING_INPUTS_PER_TASK = max(
    1,
    int(CONFIG.get("max_pending_inputs_per_task", 10)),
)
MAX_CONCURRENT_RUNS = max(1, int(CONFIG.get("max_concurrent_runs", 2)))
MAX_PENDING_CLI_FALLBACKS = 50
CLI_FALLBACK_TTL_SECONDS = 24 * 60 * 60
SQLITE_READ_RETRY_DELAYS = (0.2, 0.5)
DESKTOP_UNAVAILABLE_RETRY_DELAYS = (0.3, 0.7)
DESKTOP_TASK_ACTIVATION_SETTLE_SECONDS = 0.75
ALLOW_ACCESS_REQUESTS = CONFIG.get("allow_access_requests", True) is not False
TASKS_PER_PAGE = max(10, min(50, int(CONFIG.get("tasks_per_page", 50))))
MAX_TASK_SUBSCRIPTIONS_PER_USER = 20
TASK_SUBSCRIPTION_POLL_SECONDS = 2
RECENT_TASK_LIMIT = 20
TASK_SUMMARY_CHARS = 120

_last_reply_failure_reason = ""
_reply_failure_context = threading.local()

_consumers: list[subprocess.Popen[str]] = []
_promlight_delivery_lock = threading.RLock()
_promlight_work_condition = threading.Condition()
_promlight_pending_statuses: dict[
    str, tuple[str, str, bool, float, str, str]
] = {}
_promlight_pending_lamps: dict[str, bool] = {}


class InterprocessStateLock:
    """Reentrant thread lock plus a process-wide lock for state.json mutations."""

    def __init__(self) -> None:
        self._thread_lock = threading.RLock()
        self._local = threading.local()

    def acquire(self, blocking: bool = True, timeout: float = -1) -> bool:
        if timeout == -1:
            acquired = self._thread_lock.acquire(blocking)
        else:
            acquired = self._thread_lock.acquire(blocking, timeout)
        if not acquired:
            return False
        depth = int(getattr(self._local, "depth", 0))
        if depth == 0:
            lock_path = STATE_PATH.with_name("state.lock")
            try:
                lock_path.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
                parent_stat = lock_path.parent.lstat()
                if (
                    lock_path.parent.is_symlink()
                    or not stat.S_ISDIR(parent_stat.st_mode)
                    or parent_stat.st_uid != os.getuid()
                ):
                    raise RuntimeError("bridge state lock directory is unsafe")
                lock_path.parent.chmod(0o700)
                flags = os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0)
                descriptor = os.open(lock_path, flags, 0o600)
                opened = os.fstat(descriptor)
                if (
                    not stat.S_ISREG(opened.st_mode)
                    or opened.st_uid != os.getuid()
                ):
                    raise RuntimeError("bridge state lock is unsafe")
                os.fchmod(descriptor, 0o600)
                fcntl.flock(descriptor, fcntl.LOCK_EX)
            except Exception:
                if "descriptor" in locals():
                    os.close(descriptor)
                self._thread_lock.release()
                raise
            self._local.descriptor = descriptor
        self._local.depth = depth + 1
        return True

    def release(self) -> None:
        depth = int(getattr(self._local, "depth", 0))
        if depth <= 0:
            raise RuntimeError("cannot release un-acquired state lock")
        if depth == 1:
            descriptor = int(self._local.descriptor)
            try:
                fcntl.flock(descriptor, fcntl.LOCK_UN)
            finally:
                os.close(descriptor)
                del self._local.descriptor
        self._local.depth = depth - 1
        self._thread_lock.release()

    def __enter__(self) -> "InterprocessStateLock":
        self.acquire()
        return self

    def __exit__(self, _type: Any, _value: Any, _traceback: Any) -> None:
        self.release()


_state_lock = InterprocessStateLock()
_event_lanes_lock = threading.Lock()
_event_lanes: dict[str, queue.Queue[dict[str, Any]]] = {}
_shutdown_event = threading.Event()
_ui_intent_lock = threading.Lock()
_ui_intent_sequences: dict[str, int] = {}
_identity_refresh_condition = threading.Condition()
_identity_refresh_pending: dict[str, tuple[str, dict[str, str] | None, float]] = {}
_queued_card_refresh_pending: dict[str, float] = {}
_active_runs_lock = threading.RLock()
_active_runs: dict[str, dict[str, Any]] = {}
_result_delivery_locks_lock = threading.Lock()
_result_delivery_locks: dict[str, Any] = {}
_last_feishu_event_at = 0.0
_runtime_status_lock = threading.Lock()
_last_runtime_status_signature: tuple[Any, ...] | None = None
_codex_usage_lock = threading.Lock()
_codex_usage: dict[str, Any] = {}
_codex_usage_refreshing = False
_task_usage_cache_lock = threading.Lock()
_task_usage_cache: dict[tuple[str, str, int, str], tuple[float, dict[str, Any]]] = {}
TASK_USAGE_CACHE_SECONDS = 300


class DesktopUnavailableError(RuntimeError):
    def __init__(self, reason: str):
        self.reason = reason
        super().__init__(desktop_unavailable_message(reason))


def desktop_unavailable_message(reason: str) -> str:
    explanations = {
        "ipc-socket-missing": "Codex Desktop 当前没有提供桥接连接",
        "task-record-missing": "桥接暂时找不到这个 Task 的本地记录",
        "no-client-found": "Codex Desktop 当前没有打开或接管这个 Task",
        "ipc-timeout": "Codex Desktop 的桥接连接暂时没有响应",
        "ipc-connect-failed": "桥接暂时无法连接 Codex Desktop",
        "ipc-initialize-failed": "Codex Desktop 的桥接连接初始化失败",
    }
    detail = explanations.get(reason, "Codex Desktop 当前不可用")
    return (
        f"{detail}。这条消息尚未提交。请点击“重试 Desktop”；"
        "如确认接受 Desktop 暂时无法实时显示，也可以主动选择“使用备用 CLI”。"
    )


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
    if not WORKFLOW_EXTENSION_AVAILABLE:
        return False
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


def promlight_state(state: dict[str, Any]) -> dict[str, Any]:
    namespace = state.setdefault("promlight", {})
    if not isinstance(namespace, dict):
        namespace = {}
        state["promlight"] = namespace
    for key in ("lamps", "task_statuses", "selected_lamps", "selected_tasks", "selected_projects", "pending_renames"):
        value = namespace.setdefault(key, {})
        if not isinstance(value, dict):
            namespace[key] = {}
    return namespace


def promlight_http_json(path: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
    if not path.startswith("/"):
        raise ValueError("PromLight API path must be local")
    data = None
    headers: dict[str, str] = {}
    if body is not None:
        data = json.dumps(body, separators=(",", ":")).encode("utf-8")
        headers["Content-Type"] = "application/json"
    request = Request(PROMLIGHT_API_BASE + path, data=data, headers=headers)
    with urlopen(request, timeout=PROMLIGHT_HTTP_TIMEOUT_SECONDS) as response:
        payload = json.loads(response.read().decode("utf-8"))
    if not isinstance(payload, dict):
        raise ValueError("PromLight API returned a non-object response")
    return payload


def discover_promlight_devices() -> list[dict[str, Any]]:
    try:
        payload = promlight_http_json("/api/status")
    except (OSError, URLError, ValueError, json.JSONDecodeError):
        return []
    devices = payload.get("devices")
    if not isinstance(devices, list):
        return []
    discovered: list[dict[str, Any]] = []
    for item in devices:
        if not isinstance(item, dict):
            continue
        if item.get("opened") is not True:
            continue
        relay_ref = str(item.get("mac") or "").strip()
        if not relay_ref:
            continue
        label = str(item.get("label") or item.get("product") or "PromLight").strip()
        discovered.append(
            {
                "relay_ref": relay_ref,
                "label": label[:80] or "PromLight",
                "online": True,
            }
        )
    return discovered


def user_promlight_lamps(state: dict[str, Any], user_id: str) -> list[dict[str, Any]]:
    lamps = promlight_state(state)["lamps"]
    return sorted(
        [
            dict(lamp)
            for lamp in lamps.values()
            if isinstance(lamp, dict) and lamp.get("owner_open_id") == user_id
        ],
        key=lambda lamp: (
            not bool(lamp.get("is_default")),
            str(lamp.get("name") or "").lower(),
            str(lamp.get("lamp_id") or ""),
        ),
    )


def owned_promlight_lamp(
    state: dict[str, Any],
    user_id: str,
    lamp_id: str,
) -> dict[str, Any]:
    lamp = promlight_state(state)["lamps"].get(lamp_id)
    if not isinstance(lamp, dict) or lamp.get("owner_open_id") != user_id:
        raise PermissionError("提示灯不存在或不属于当前用户")
    return lamp


def bind_promlight(user_id: str, relay_ref: str, name: str = "") -> str:
    if not authorized_user(user_id):
        raise PermissionError("当前用户未获桥接授权")
    relay_ref = str(relay_ref or "").strip()
    devices = {item["relay_ref"]: item for item in discover_promlight_devices()}
    discovered = devices.get(relay_ref)
    if discovered is None:
        raise ValueError("没有发现这盏在线提示灯")
    with _promlight_delivery_lock, _state_lock:
        state = load_state()
        namespace = promlight_state(state)
        for lamp in namespace["lamps"].values():
            if not isinstance(lamp, dict) or lamp.get("relay_ref") != relay_ref:
                continue
            if lamp.get("owner_open_id") != user_id:
                raise PermissionError("这盏提示灯已归属于其他用户")
            if lamp.get("pending_unbind"):
                raise ValueError("这盏提示灯正在解绑待收口，请等待设备恢复在线后再绑定")
            return str(lamp["lamp_id"])
        lamp_id = "light_" + uuid.uuid4().hex[:12]
        existing = user_promlight_lamps(state, user_id)
        lamp_name = " ".join(str(name or discovered["label"]).split())[:40]
        namespace["lamps"][lamp_id] = {
            "lamp_id": lamp_id,
            "owner_open_id": user_id,
            "name": lamp_name or "我的提示灯",
            "relay_ref": relay_ref,
            "relay_type": "desktop",
            "active_relay": "desktop",
            "task_ids": [],
            "is_default": not existing,
            "online": True,
            "last_logical_status": "idle",
            "last_delivery": "not_sent",
            "last_verified": False,
            "revision": 1,
            "updated_at": time.time(),
        }
        namespace["selected_lamps"][user_id] = lamp_id
        save_state(state)
    return lamp_id


def rename_promlight(user_id: str, lamp_id: str, name: str) -> None:
    normalized = " ".join(str(name or "").split())[:40]
    if not normalized:
        raise ValueError("提示灯名称不能为空")
    with _state_lock:
        state = load_state()
        owned_promlight_lamp(state, user_id, lamp_id)["name"] = normalized
        save_state(state)


def set_default_promlight(user_id: str, lamp_id: str) -> None:
    with _state_lock:
        state = load_state()
        selected = owned_promlight_lamp(state, user_id, lamp_id)
        for lamp in promlight_state(state)["lamps"].values():
            if isinstance(lamp, dict) and lamp.get("owner_open_id") == user_id:
                lamp["is_default"] = lamp is selected
        save_state(state)


def unbind_promlight(user_id: str, lamp_id: str) -> None:
    with _state_lock:
        state = load_state()
        lamp = owned_promlight_lamp(state, user_id, lamp_id)
        lamp["task_ids"] = []
        lamp["pending_unbind"] = True
        lamp["pending_unbind_reason"] = "user_request"
        lamp["pending_idle"] = False
        lamp["revision"] = int(lamp.get("revision") or 0) + 1
        lamp["next_retry_at"] = 0
        save_state(state)
    schedule_promlight_lamp_refresh(lamp_id, force=True)


def set_promlight_task_subscription(
    user_id: str,
    lamp_id: str,
    task_id: str,
    enabled: bool,
) -> bool:
    task = task_by_id(task_id, user_id)
    if enabled and (task is None or not user_can_access_task(user_id, task)):
        raise PermissionError("该 Task 已归档、删除或不再属于你的授权项目")
    with _state_lock:
        state = load_state()
        lamp = owned_promlight_lamp(state, user_id, lamp_id)
        task_ids = [str(value) for value in lamp.get("task_ids", []) if str(value)]
        previous_ids = list(task_ids)
        if enabled and task_id not in task_ids:
            task_ids.append(task_id)
        elif not enabled and task_id in task_ids:
            task_ids.remove(task_id)
        lamp["task_ids"] = task_ids
        if task_ids != previous_ids:
            lamp["revision"] = int(lamp.get("revision") or 0) + 1
        if task_ids:
            lamp["pending_idle"] = False
        elif previous_ids:
            lamp["pending_idle"] = True
            lamp["next_retry_at"] = 0
        namespace = promlight_state(state)
        namespace["selected_lamps"][user_id] = lamp_id
        if task is not None:
            namespace["selected_tasks"][user_id] = task_id
            namespace["selected_projects"][user_id] = task["project"]
        save_state(state)
    refresh_promlight_lamp(lamp_id, force=True)
    return enabled


def aggregate_promlight_status(statuses: list[str]) -> str:
    effective = [status for status in statuses if status in PROMLIGHT_STATUS_PRIORITY]
    if not effective:
        return "idle"
    return max(effective, key=lambda status: PROMLIGHT_STATUS_PRIORITY[status])


def promlight_command_for_status(status: str) -> str:
    if status not in PROMLIGHT_STATUS_COMMANDS:
        raise ValueError("PromLight status has no safe physical command")
    return PROMLIGHT_STATUS_COMMANDS[status]


def remove_promlight_binding(state: dict[str, Any], lamp_id: str) -> None:
    namespace = promlight_state(state)
    current = namespace["lamps"].pop(lamp_id, None)
    if not isinstance(current, dict):
        return
    owner = str(current.get("owner_open_id") or "")
    if current.get("is_default"):
        remaining = user_promlight_lamps(state, owner)
        if remaining:
            namespace["lamps"][remaining[0]["lamp_id"]]["is_default"] = True
    if namespace["selected_lamps"].get(owner) == lamp_id:
        namespace["selected_lamps"].pop(owner, None)


def deliver_promlight_effect(lamp: dict[str, Any], status: str) -> dict[str, Any]:
    if str(lamp.get("active_relay") or "") != "desktop":
        return {"online": False, "delivery": "unknown", "verified": False}
    relay_ref = str(lamp.get("relay_ref") or "").strip()
    if not relay_ref:
        return {"online": False, "delivery": "unknown", "verified": False}
    try:
        response = promlight_http_json(
            "/api/command",
            {"device": relay_ref, "cmd": promlight_command_for_status(status)},
        )
    except (OSError, URLError, ValueError, json.JSONDecodeError):
        return {"online": False, "delivery": "unknown", "verified": False}
    results = response.get("results")
    statuses = [
        str(item.get("status") or "")
        for item in results
        if isinstance(item, dict)
    ] if isinstance(results, list) else []
    acknowledged = bool(response.get("ok")) and bool(statuses) and all(
        value == "ok" for value in statuses
    )
    return {
        "online": acknowledged,
        "delivery": "acknowledged" if acknowledged else "unknown",
        "verified": False,
    }


def refresh_promlight_lamp(lamp_id: str, force: bool = False) -> bool:
    with _promlight_delivery_lock:
        with _state_lock:
            state = load_state()
            namespace = promlight_state(state)
            current = namespace["lamps"].get(lamp_id)
            if not isinstance(current, dict):
                return False
            lamp = dict(current)
            task_ids = [str(value) for value in lamp.get("task_ids", []) if str(value)]
            statuses = namespace["task_statuses"]
            owner = str(lamp.get("owner_open_id") or "")
            lamp_statuses: list[str] = []
            for task_id in task_ids:
                entry = statuses.get(task_id)
                if not isinstance(entry, dict):
                    continue
                entry_status = str(entry.get("status") or "idle")
                target_user_id = str(entry.get("target_user_id") or "")
                if entry_status == "human_gate" and target_user_id != owner:
                    entry_status = "running"
                lamp_statuses.append(entry_status)
            pending_unbind = bool(lamp.get("pending_unbind"))
            pending_idle = bool(lamp.get("pending_idle"))
            logical_status = (
                "idle"
                if pending_unbind or pending_idle or (force and not task_ids)
                else "unknown"
                if task_ids and not lamp_statuses
                else aggregate_promlight_status(lamp_statuses)
            )
            if not task_ids and not force and not pending_unbind and not pending_idle:
                return False
            now = time.time()
            if (
                not force
                and lamp.get("last_logical_status") == logical_status
                and lamp.get("last_delivery") == "acknowledged"
            ):
                return False
            if (
                not force
                and lamp.get("last_delivery") != "acknowledged"
                and now < float(lamp.get("next_retry_at") or 0)
            ):
                return False
            if logical_status == "unknown":
                current["online"] = False
                current["last_delivery"] = "unknown"
                current["last_verified"] = False
                current["pending_delivery_status"] = ""
                current["next_retry_at"] = 0
                current["updated_at"] = now
                save_state(state)
                return True
        result = deliver_promlight_effect(lamp, logical_status)
        with _state_lock:
            state = load_state()
            current = promlight_state(state)["lamps"].get(lamp_id)
            if not isinstance(current, dict):
                return False
            if int(current.get("revision") or 0) != int(lamp.get("revision") or 0):
                return False
            acknowledged = result.get("delivery") == "acknowledged"
            if acknowledged:
                current["last_logical_status"] = logical_status
            current["online"] = bool(result.get("online"))
            current["last_delivery"] = str(result.get("delivery") or "unknown")
            current["last_verified"] = bool(result.get("verified"))
            current["updated_at"] = time.time()
            if acknowledged:
                current["retry_count"] = 0
                current["next_retry_at"] = 0
                current["pending_delivery_status"] = ""
                if current.get("pending_unbind") and logical_status == "idle":
                    remove_promlight_binding(state, lamp_id)
                elif current.get("pending_idle") and logical_status == "idle":
                    current["pending_idle"] = False
            else:
                retry_count = int(current.get("retry_count") or 0) + 1
                current["retry_count"] = retry_count
                current["pending_delivery_status"] = logical_status
                current["next_retry_at"] = time.time() + min(30, 2 ** min(retry_count, 5))
            save_state(state)
        return True


def record_promlight_task_status(
    task_id: str,
    status: str,
    source: str,
    definitive: bool,
    observed_at: float | None = None,
    target_user_id: str = "",
    defer_delivery: bool = False,
) -> bool:
    if status not in PROMLIGHT_TASK_STATUSES:
        raise ValueError("unknown PromLight task status")
    effective = status if status != "error" or definitive else "unknown"
    timestamp = time.time() if observed_at is None else float(observed_at)
    with _state_lock:
        state = load_state()
        namespace = promlight_state(state)
        previous = namespace["task_statuses"].get(task_id)
        if isinstance(previous, dict) and float(previous.get("updated_at") or 0) > timestamp:
            return False
        next_value = {
            "status": effective,
            "reported_status": status,
            "source": str(source)[:40],
            "definitive": bool(definitive),
            "target_user_id": str(target_user_id) if effective == "human_gate" else "",
            "updated_at": timestamp,
        }
        namespace["task_statuses"][task_id] = next_value
        lamp_ids = [
            lamp_id
            for lamp_id, lamp in namespace["lamps"].items()
            if isinstance(lamp, dict) and task_id in lamp.get("task_ids", [])
        ]
        changed = previous != next_value
        save_state(state)
    for lamp_id in lamp_ids:
        if defer_delivery:
            schedule_promlight_lamp_refresh(lamp_id)
        else:
            refresh_promlight_lamp(lamp_id)
    return changed


def schedule_promlight_task_status(
    task_id: str,
    status: str,
    source: str,
    definitive: bool,
    target_user_id: str = "",
    turn_id: str = "",
) -> None:
    with _promlight_work_condition:
        pending = _promlight_pending_statuses.get(task_id)
        if (
            pending is not None
            and pending[1] == "bridge_run"
            and pending[0] in {"human_gate", "error"}
            and source == "rollout"
        ):
            return
        _promlight_pending_statuses[task_id] = (
            status,
            source,
            definitive,
            time.time(),
            target_user_id,
            turn_id,
        )
        _promlight_work_condition.notify()


def schedule_promlight_lamp_refresh(lamp_id: str, force: bool = False) -> None:
    with _promlight_work_condition:
        _promlight_pending_lamps[lamp_id] = (
            bool(force) or _promlight_pending_lamps.get(lamp_id, False)
        )
        _promlight_work_condition.notify()


def process_promlight_work_once() -> bool:
    task_work: tuple[str, tuple[str, str, bool, float, str, str]] | None = None
    lamp_work: tuple[str, bool] | None = None
    with _promlight_work_condition:
        if _promlight_pending_statuses:
            task_work = _promlight_pending_statuses.popitem()
        elif _promlight_pending_lamps:
            lamp_work = _promlight_pending_lamps.popitem()
    if task_work is not None:
        task_id, work = task_work
        status, source, definitive, observed_at, target_user_id, turn_id = work
        record_promlight_task_status(
            task_id,
            status,
            source,
            definitive,
            observed_at,
            target_user_id,
            defer_delivery=True,
        )
        if turn_id:
            with _state_lock:
                state = load_state()
                entry = promlight_state(state)["task_statuses"].get(task_id)
                if isinstance(entry, dict):
                    entry["turn_id"] = turn_id
                    save_state(state)
        return True
    if lamp_work is not None:
        refresh_promlight_lamp(*lamp_work)
        return True
    return False


def promlight_worker_loop() -> None:
    while not _shutdown_event.is_set():
        with _promlight_work_condition:
            if not _promlight_pending_statuses and not _promlight_pending_lamps:
                _promlight_work_condition.wait(timeout=0.5)
        try:
            process_promlight_work_once()
        except Exception as exc:
            log(f"PromLight worker failed: {type(exc).__name__}: {exc}")


def reconcile_promlight_state() -> bool:
    idle_lamps: dict[str, bool] = {}
    changed = False
    with _promlight_delivery_lock, _state_lock:
        state = load_state()
        namespace = promlight_state(state)
        for lamp_id, value in list(namespace["lamps"].items()):
            if not isinstance(value, dict):
                namespace["lamps"].pop(lamp_id, None)
                changed = True
                continue
            owner = str(value.get("owner_open_id") or "")
            if not authorized_user(owner):
                if not value.get("pending_unbind"):
                    value["task_ids"] = []
                    value["pending_unbind"] = True
                    value["pending_unbind_reason"] = "permission_revoked"
                    value["pending_idle"] = False
                    value["revision"] = int(value.get("revision") or 0) + 1
                    value["next_retry_at"] = 0
                    idle_lamps[lamp_id] = True
                    changed = True
                elif time.time() >= float(value.get("next_retry_at") or 0):
                    idle_lamps[lamp_id] = False
                for key in ("selected_lamps", "selected_tasks", "selected_projects", "pending_renames"):
                    if owner in namespace[key]:
                        namespace[key].pop(owner, None)
                        changed = True
                continue
            try:
                valid_ids = {task["id"] for task in recent_tasks(owner)}
            except (OSError, sqlite3.Error):
                continue
            before = [str(item) for item in value.get("task_ids", []) if str(item)]
            after = [task_id for task_id in before if task_id in valid_ids]
            if after != before:
                value["task_ids"] = after
                value["revision"] = int(value.get("revision") or 0) + 1
                changed = True
                if not after:
                    value["pending_idle"] = True
                    value["next_retry_at"] = 0
                    idle_lamps[lamp_id] = True
            elif (
                value.get("pending_idle")
                and time.time() >= float(value.get("next_retry_at") or 0)
            ):
                idle_lamps[lamp_id] = False
        for owner in list(namespace["selected_lamps"]):
            selected = str(namespace["selected_lamps"].get(owner) or "")
            lamp = namespace["lamps"].get(selected)
            if not isinstance(lamp, dict) or lamp.get("owner_open_id") != owner:
                namespace["selected_lamps"].pop(owner, None)
                changed = True
        watched = {
            str(task_id)
            for lamp in namespace["lamps"].values()
            if isinstance(lamp, dict)
            for task_id in lamp.get("task_ids", [])
            if str(task_id)
        }
        for task_id in list(namespace["task_statuses"]):
            if task_id not in watched:
                namespace["task_statuses"].pop(task_id, None)
                changed = True
        if changed:
            save_state(state)
    for lamp_id, force in idle_lamps.items():
        schedule_promlight_lamp_refresh(lamp_id, force)
    return changed


def promlight_task_is_watched(task_id: str) -> bool:
    with _state_lock:
        lamps = promlight_state(load_state())["lamps"]
        return any(
            isinstance(lamp, dict) and task_id in lamp.get("task_ids", [])
            for lamp in lamps.values()
        )


def poll_promlight_task_statuses() -> bool:
    did_work = reconcile_promlight_state()
    with _state_lock:
        state = load_state()
        namespace = promlight_state(state)
        watched = list(
            dict.fromkeys(
                str(task_id)
                for lamp in namespace["lamps"].values()
                if isinstance(lamp, dict)
                for task_id in lamp.get("task_ids", [])
                if str(task_id)
            )
        )
        previous = dict(namespace["task_statuses"])
    for task_id in watched:
        try:
            snapshot = latest_rollout_turn(rollout_path_for_task(task_id))
        except (OSError, sqlite3.Error):
            continue
        snapshot_status = str(snapshot.get("status") or "none")
        mapped = {
            "running": "running",
            "completed": "idle",
            "failed": "error",
            "none": "unknown",
        }.get(snapshot_status, "unknown")
        existing = previous.get(task_id)
        if (
            mapped == "running"
            and isinstance(existing, dict)
            and existing.get("status") == "human_gate"
            and existing.get("source") == "bridge_run"
        ):
            continue
        definitive = snapshot_status in {"failed", "completed", "running"}
        existing_status = str(existing.get("status") or "") if isinstance(existing, dict) else ""
        existing_turn = str(existing.get("turn_id") or "") if isinstance(existing, dict) else ""
        turn_id = str(snapshot.get("turn_id") or "")
        if existing_status == mapped and existing_turn == turn_id:
            with _state_lock:
                state = load_state()
                retry_lamps = [
                    str(lamp_id)
                    for lamp_id, lamp in promlight_state(state)["lamps"].items()
                    if isinstance(lamp, dict)
                    and task_id in lamp.get("task_ids", [])
                    and bool(lamp.get("pending_delivery_status"))
                    and time.time() >= float(lamp.get("next_retry_at") or 0)
                ]
            for lamp_id in retry_lamps:
                schedule_promlight_lamp_refresh(lamp_id)
            continue
        schedule_promlight_task_status(
            task_id,
            mapped,
            "rollout",
            definitive,
            turn_id=turn_id,
        )
        did_work = True
    return did_work


def remove_access_requests(open_ids: set[str]) -> int:
    with _state_lock:
        state = load_state()
        requests = state.get("access_requests")
        if not isinstance(requests, list):
            return 0
        kept = [
            request
            for request in requests
            if not (
                isinstance(request, dict)
                and str(request.get("open_id") or "") in open_ids
            )
        ]
        removed = len(requests) - len(kept)
        if removed:
            state["access_requests"] = kept
            save_state(state)
        return removed


def runtime_status_path() -> Path:
    configured = str(os.environ.get("CODEX_FEISHU_RUNTIME_STATUS") or "").strip()
    return Path(configured).expanduser() if configured else STATE_PATH.with_name("runtime-status.json")


def write_runtime_status(active_runs: int | None = None) -> None:
    global _last_runtime_status_signature

    with _active_runs_lock:
        running = (
            sum(
                run.get("outcome") in {"running", "approval"}
                for run in _active_runs.values()
            )
            if active_runs is None
            else active_runs
        )
    destination = runtime_status_path()
    consumers = sum(consumer.poll() is None for consumer in _consumers)
    with _codex_usage_lock:
        codex_usage = json.loads(json.dumps(_codex_usage)) if _codex_usage else {}
    usage_signature = json.dumps(codex_usage, sort_keys=True, separators=(",", ":"))
    signature = (
        running,
        consumers,
        MAX_CONCURRENT_RUNS,
        _last_feishu_event_at,
        usage_signature,
    )
    with _runtime_status_lock:
        if signature == _last_runtime_status_signature and destination.is_file():
            return
        try:
            destination.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
            destination.parent.chmod(0o700)
            payload = {
                "active_runs": running,
                "active_consumers": consumers,
                "max_concurrent_runs": MAX_CONCURRENT_RUNS,
                "last_feishu_event_at": _last_feishu_event_at,
                "codex_usage": codex_usage,
                "updated_at": time.time(),
            }
            temporary = destination.parent / (
                f".{destination.name}.{os.getpid()}.{uuid.uuid4().hex}.tmp"
            )
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
            try:
                descriptor = os.open(temporary, flags, 0o600)
                with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
                    json.dump(payload, handle, ensure_ascii=False, separators=(",", ":"))
                    handle.write("\n")
                    handle.flush()
                    os.fsync(handle.fileno())
                os.replace(temporary, destination)
                destination.chmod(0o600)
                _last_runtime_status_signature = signature
            finally:
                temporary.unlink(missing_ok=True)
        except OSError:
            return


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


def retry_sqlite_read(operation: Callable[[], Any]) -> Any:
    for attempt in range(len(SQLITE_READ_RETRY_DELAYS) + 1):
        try:
            return operation()
        except sqlite3.OperationalError:
            if attempt >= len(SQLITE_READ_RETRY_DELAYS):
                raise
            log(f"task database read retry attempt={attempt + 1}")
            time.sleep(SQLITE_READ_RETRY_DELAYS[attempt])
    raise RuntimeError("unreachable")


def _read_tasks_by_archive_state(archived: bool) -> list[sqlite3.Row]:
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
    return rows


def tasks_by_archive_state(
    user_id: str,
    archived: bool,
) -> list[dict[str, str]]:
    rows = retry_sqlite_read(lambda: _read_tasks_by_archive_state(archived))
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


def _read_task_by_id(thread_id: str) -> sqlite3.Row | None:
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
    return row


def task_by_id(thread_id: str, user_id: str) -> dict[str, str] | None:
    row = retry_sqlite_read(lambda: _read_task_by_id(thread_id))
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


def complete_jsonl_size(handle: Any, file_size: int) -> int:
    if file_size <= 0:
        return 0
    handle.seek(file_size - 1)
    if handle.read(1) == b"\n":
        return file_size
    position = file_size
    while position > 0:
        read_size = min(64 * 1024, position)
        position -= read_size
        handle.seek(position)
        newline = handle.read(read_size).rfind(b"\n")
        if newline >= 0:
            return position + newline + 1
    return 0


def reverse_jsonl_lines(handle: Any, end_offset: int) -> Any:
    position = end_offset
    buffer = b""
    while position > 0:
        read_size = min(64 * 1024, position)
        position -= read_size
        handle.seek(position)
        buffer = handle.read(read_size) + buffer
        lines = buffer.split(b"\n")
        buffer = lines[0]
        for line in reversed(lines[1:]):
            if line:
                yield line
    if buffer:
        yield buffer


def latest_rollout_turn(path: Path | None) -> dict[str, Any]:
    snapshot: dict[str, Any] = {
        "status": "none",
        "turn_id": "",
        "message": "",
        "images": [],
        "cursor_offset": 0,
    }
    if path is None or not path.is_file():
        return snapshot
    try:
        with path.open("rb") as handle:
            complete_size = complete_jsonl_size(handle, path.stat().st_size)
            snapshot["cursor_offset"] = complete_size
            terminals: dict[str, tuple[str, str]] = {}
            reverse_images: list[str] = []
            for line in reverse_jsonl_lines(handle, complete_size):
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue
                payload = record.get("payload")
                if not isinstance(payload, dict):
                    continue
                event_type = str(payload.get("type") or "")
                turn_id = str(payload.get("turn_id") or "")
                if event_type == "task_complete" and turn_id:
                    terminals.setdefault(
                        turn_id,
                        (
                            "completed",
                            str(payload.get("last_agent_message") or "").strip(),
                        ),
                    )
                elif event_type in {"task_failed", "turn_aborted"} and turn_id:
                    terminals.setdefault(
                        turn_id,
                        ("failed", "Codex Desktop 没有完成这一轮运行。"),
                    )
                elif event_type == "image_generation_end":
                    image = normalized_image_reference(
                        str(payload.get("saved_path") or ""),
                        trusted_local=True,
                    )
                    if image is not None and image not in reverse_images:
                        reverse_images.append(image)
                elif event_type == "task_started":
                    status, message = terminals.get(turn_id, ("running", ""))
                    snapshot.update(
                        {
                            "status": status,
                            "turn_id": turn_id,
                            "message": message,
                            "images": list(reversed(reverse_images)),
                        }
                    )
                    return snapshot
    except OSError:
        return {
            "status": "none",
            "turn_id": "",
            "message": "",
            "images": [],
            "cursor_offset": 0,
        }
    return snapshot


def advance_rollout_turn(
    path: Path | None,
    turn_id: str,
    cursor_offset: int,
    existing_images: list[str] | None = None,
) -> dict[str, Any]:
    snapshot: dict[str, Any] = {
        "status": "running",
        "turn_id": turn_id,
        "message": "",
        "images": list(existing_images or []),
        "cursor_offset": max(0, int(cursor_offset)),
    }
    if path is None or not path.is_file() or not turn_id:
        snapshot["status"] = "missing"
        return snapshot
    try:
        if path.stat().st_size < snapshot["cursor_offset"]:
            snapshot["status"] = "missing"
            return snapshot
        with path.open("r", encoding="utf-8") as handle:
            handle.seek(snapshot["cursor_offset"])
            while True:
                line_start = handle.tell()
                line = handle.readline()
                if not line:
                    snapshot["cursor_offset"] = line_start
                    break
                if not line.endswith("\n"):
                    snapshot["cursor_offset"] = line_start
                    break
                snapshot["cursor_offset"] = handle.tell()
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue
                payload = record.get("payload")
                if not isinstance(payload, dict):
                    continue
                event_type = str(payload.get("type") or "")
                if event_type == "image_generation_end":
                    image = normalized_image_reference(
                        str(payload.get("saved_path") or ""),
                        trusted_local=True,
                    )
                    if image is not None and image not in snapshot["images"]:
                        snapshot["images"].append(image)
                    continue
                payload_turn_id = str(payload.get("turn_id") or "")
                if payload_turn_id != turn_id:
                    continue
                if event_type == "task_complete":
                    snapshot["status"] = "completed"
                    snapshot["message"] = str(
                        payload.get("last_agent_message") or ""
                    ).strip()
                    break
                if event_type in {"task_failed", "turn_aborted"}:
                    snapshot["status"] = "failed"
                    snapshot["message"] = "Codex Desktop 没有完成这一轮运行。"
                    break
    except OSError:
        snapshot["status"] = "missing"
    return snapshot


def scan_task_subscription_rollout(
    path: Path | None,
    cursor_offset: int,
    active_turn_id: str = "",
    existing_images: list[str] | None = None,
) -> dict[str, Any]:
    """Read only complete records appended after a subscription cursor."""
    result: dict[str, Any] = {
        "cursor_offset": max(0, int(cursor_offset)),
        "active_turn_id": active_turn_id,
        "images": list(existing_images or []),
        "deliveries": [],
        "available": False,
    }
    if path is None or not path.is_file():
        return result
    try:
        with path.open("rb") as handle:
            complete_size = complete_jsonl_size(handle, path.stat().st_size)
            if result["cursor_offset"] > complete_size:
                result["cursor_offset"] = complete_size
                result["active_turn_id"] = ""
                result["images"] = []
                result["available"] = True
                return result
            handle.seek(result["cursor_offset"])
            while handle.tell() < complete_size:
                line = handle.readline()
                if not line or handle.tell() > complete_size:
                    break
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    result["cursor_offset"] = handle.tell()
                    continue
                payload = record.get("payload")
                if not isinstance(payload, dict):
                    result["cursor_offset"] = handle.tell()
                    continue
                event_type = str(payload.get("type") or "")
                turn_id = str(payload.get("turn_id") or "")
                if event_type == "task_started" and turn_id:
                    result["active_turn_id"] = turn_id
                    result["images"] = []
                elif event_type == "image_generation_end" and result["active_turn_id"]:
                    image = normalized_image_reference(
                        str(payload.get("saved_path") or ""),
                        trusted_local=True,
                    )
                    if image is not None and image not in result["images"]:
                        result["images"].append(image)
                elif event_type in {"task_complete", "task_failed", "turn_aborted"} and turn_id:
                    if not result["active_turn_id"] or result["active_turn_id"] == turn_id:
                        status = "completed" if event_type == "task_complete" else "failed"
                        message = (
                            str(payload.get("last_agent_message") or "").strip()
                            if status == "completed"
                            else "Codex Desktop 没有完成这一轮运行。"
                        )
                        result["active_turn_id"] = ""
                        result["deliveries"].append(
                            {
                                "status": status,
                                "turn_id": turn_id,
                                "message": message,
                                "images": list(result["images"]),
                                "cursor_offset": handle.tell(),
                                "active_turn_id": "",
                                "remaining_images": [],
                            }
                        )
                        result["images"] = []
                result["cursor_offset"] = handle.tell()
            result["available"] = True
    except OSError:
        return result
    return result


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
            dict[str, Any] | None | Callable[[list[dict[str, Any]]], dict[str, Any]],
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
                        "title": "DeepOri Bridge",
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


def codex_task_settings(thread_id: str) -> dict[str, Any]:
    results = codex_app_server_requests(
        [("model/list", {"includeHidden": False, "limit": 100})],
        timeout=10,
    )
    raw_models = results[0].get("data") if results else []
    thread = desktop_task_state(thread_id)

    models: list[dict[str, Any]] = []
    for raw in raw_models if isinstance(raw_models, list) else []:
        if not isinstance(raw, dict) or raw.get("hidden") is True:
            continue
        model = str(raw.get("model") or raw.get("id") or "").strip()
        if not model:
            continue
        efforts = []
        for option in raw.get("supportedReasoningEfforts", []):
            if not isinstance(option, dict):
                continue
            effort = str(option.get("reasoningEffort") or "").strip()
            if effort and effort not in efforts:
                efforts.append(effort)
        service_tiers: list[dict[str, str]] = []
        for option in raw.get("serviceTiers", []):
            if not isinstance(option, dict):
                continue
            tier_id = str(option.get("id") or "").strip()
            if not tier_id or any(item["id"] == tier_id for item in service_tiers):
                continue
            service_tiers.append(
                {
                    "id": tier_id,
                    "name": str(option.get("name") or tier_id).strip()[:80],
                    "description": str(option.get("description") or "").strip()[:160],
                }
            )
        if not service_tiers:
            for legacy_tier in raw.get("additionalSpeedTiers", []):
                tier_id = str(legacy_tier or "").strip()
                if tier_id and not any(
                    item["id"] == tier_id for item in service_tiers
                ):
                    service_tiers.append(
                        {
                            "id": tier_id,
                            "name": "快速" if tier_id == "fast" else tier_id,
                            "description": "",
                        }
                    )
        models.append(
            {
                "model": model,
                "display_name": str(raw.get("displayName") or model).strip()[:80],
                "default_effort": str(raw.get("defaultReasoningEffort") or "").strip(),
                "efforts": efforts,
                "service_tiers": service_tiers,
            }
        )

    thread_settings = (
        thread.get("latestThreadSettings")
        if isinstance(thread.get("latestThreadSettings"), dict)
        else {}
    )
    current_model = str(
        thread_settings.get("model") or thread.get("latestModel") or ""
    ).strip()
    current_effort = str(
        thread_settings.get("effort") or thread.get("latestReasoningEffort") or ""
    ).strip()
    current_service_tier = str(
        thread_settings.get("serviceTier")
        or thread.get("latestServiceTier")
        or "default"
    ).strip()
    if current_model and not any(item["model"] == current_model for item in models):
        models.insert(
            0,
            {
                "model": current_model,
                "display_name": current_model,
                "default_effort": current_effort,
                "efforts": [current_effort] if current_effort else [],
                "service_tiers": [],
            },
        )
    if not current_effort:
        current_entry = next(
            (item for item in models if item["model"] == current_model),
            None,
        )
        if current_entry is not None:
            current_effort = str(current_entry.get("default_effort") or "")
    current_entry = next(
        (item for item in models if item["model"] == current_model),
        None,
    )
    supported_tiers = {
        str(item.get("id") or "")
        for item in (current_entry or {}).get("service_tiers", [])
        if isinstance(item, dict)
    }
    if current_service_tier == "fast" and "priority" in supported_tiers:
        current_service_tier = "priority"
    if current_service_tier not in supported_tiers | {"default"}:
        current_service_tier = "default"
    return {
        "model": current_model,
        "effort": current_effort,
        "service_tier": current_service_tier,
        "models": models,
    }


def normalize_codex_usage(result: dict[str, Any], now: float | None = None) -> dict[str, Any]:
    raw_buckets = result.get("rateLimitsByLimitId")
    if not isinstance(raw_buckets, dict) or not raw_buckets:
        fallback = result.get("rateLimits")
        raw_buckets = {
            str(fallback.get("limitId") or "codex"): fallback
        } if isinstance(fallback, dict) else {}

    buckets: list[dict[str, Any]] = []
    ordered = sorted(
        raw_buckets.items(),
        key=lambda item: (str(item[0]) != "codex", str(item[0])),
    )
    for limit_id, raw_snapshot in ordered:
        if not isinstance(raw_snapshot, dict):
            continue
        windows: list[dict[str, Any]] = []
        for key in ("primary", "secondary"):
            raw_window = raw_snapshot.get(key)
            if not isinstance(raw_window, dict):
                continue
            used = raw_window.get("usedPercent")
            if not isinstance(used, (int, float)) or isinstance(used, bool):
                continue
            reset = raw_window.get("resetsAt")
            duration = raw_window.get("windowDurationMins")
            windows.append(
                {
                    "remaining_percent": max(0, min(100, int(round(100 - used)))),
                    "window_minutes": int(duration) if isinstance(duration, (int, float)) else 0,
                    "resets_at": int(reset) if isinstance(reset, (int, float)) else 0,
                }
            )
        if not windows:
            continue
        name = str(raw_snapshot.get("limitName") or "").strip()
        if not name:
            name = "Codex" if str(limit_id) == "codex" else str(limit_id)
        buckets.append({"id": str(limit_id), "name": name[:80], "windows": windows})
    return {
        "buckets": buckets,
        "updated_at": time.time() if now is None else now,
    } if buckets else {}


def refresh_codex_usage() -> bool:
    global _codex_usage, _codex_usage_refreshing

    with _codex_usage_lock:
        if _codex_usage_refreshing:
            return False
        _codex_usage_refreshing = True
    try:
        results = codex_app_server_requests(
            [("account/rateLimits/read", None)],
            timeout=10,
        )
        usage = normalize_codex_usage(results[0] if results else {})
        if usage:
            with _codex_usage_lock:
                _codex_usage = usage
            write_runtime_status()
            return True
    except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
        log(f"Codex usage refresh failed: {type(exc).__name__}")
    finally:
        with _codex_usage_lock:
            _codex_usage_refreshing = False
    return False


def codex_usage_snapshot() -> dict[str, Any]:
    with _codex_usage_lock:
        return json.loads(json.dumps(_codex_usage)) if _codex_usage else {}


def usage_window_label(minutes: int) -> str:
    if minutes == 10080:
        return "每周"
    if minutes > 0 and minutes % 60 == 0:
        return f"{minutes // 60} 小时"
    return f"{minutes} 分钟" if minutes > 0 else "额度"


def codex_usage_lines(usage: dict[str, Any]) -> list[str]:
    lines: list[str] = []
    for bucket in usage.get("buckets", []):
        if not isinstance(bucket, dict):
            continue
        name = str(bucket.get("name") or "Codex")
        for window in bucket.get("windows", []):
            if not isinstance(window, dict):
                continue
            remaining = int(window.get("remaining_percent") or 0)
            label = usage_window_label(int(window.get("window_minutes") or 0))
            resets_at = int(window.get("resets_at") or 0)
            reset_text = (
                time.strftime("%m-%d %H:%M", time.localtime(resets_at)) + " 重置"
                if resets_at > 0
                else "重置时间未知"
            )
            lines.append(f"{name} · {label}：剩余 {remaining}% · {reset_text}")
    return lines


def build_codex_usage_card(
    usage: dict[str, Any],
    status: str = "",
) -> dict[str, Any]:
    lines = []
    if status:
        lines.append(f"✅ **{card_markdown_escape(status)}**")
    usage_lines = codex_usage_lines(usage)
    lines.extend(
        f"**{card_markdown_escape(line)}**"
        for line in usage_lines
    )
    if not usage_lines:
        lines.append("暂时没有额度数据，请点击下方按钮刷新。")
    try:
        updated_at = float(usage.get("updated_at") or 0)
    except (TypeError, ValueError):
        updated_at = 0
    if updated_at > 0:
        lines.append(
            "<font color='grey'>数据更新："
            f"{time.strftime('%m-%d %H:%M:%S', time.localtime(updated_at))}"
            "</font>"
        )
    buttons = [
        {
            "tag": "button",
            "text": {"tag": "plain_text", "content": "当日 Task 用量分析"},
            "type": "default",
            "width": "fill",
            "behaviors": [
                {
                    "type": "callback",
                    "value": {"action": "show_daily_task_usage_analysis"},
                }
            ],
        },
        {
            "tag": "button",
            "text": {"tag": "plain_text", "content": "当期 Task 用量分析"},
            "type": "default",
            "width": "fill",
            "behaviors": [
                {
                    "type": "callback",
                    "value": {"action": "show_period_task_usage_analysis"},
                }
            ],
        },
        {
            "tag": "button",
            "text": {"tag": "plain_text", "content": "刷新用量"},
            "type": "primary_filled",
            "width": "fill",
            "behaviors": [
                {
                    "type": "callback",
                    "value": {"action": "refresh_codex_usage"},
                }
            ],
        }
    ]
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": "Codex 用量"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": "Codex 用量"},
            "subtitle": {
                "tag": "plain_text",
                "content": "本机 Codex 账号的实时额度",
            },
            "template": "blue",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {"tag": "markdown", "content": "\n\n".join(lines)},
                *buttons,
            ],
        },
    }


def usage_analysis_button(label: str, action: str, primary: bool = False) -> dict[str, Any]:
    return {
        "tag": "button",
        "text": {"tag": "plain_text", "content": label},
        "type": "primary_filled" if primary else "default",
        "width": "fill",
        "behaviors": [
            {
                "type": "callback",
                "value": {"action": action},
            }
        ],
    }


def task_usage_time_range(
    scope: str,
    usage: dict[str, Any],
    now: float | None = None,
) -> dict[str, Any]:
    end_at = time.time() if now is None else now
    if scope == "daily":
        local_now = datetime.fromtimestamp(end_at).astimezone()
        start_at = local_now.replace(hour=0, minute=0, second=0, microsecond=0).timestamp()
        return {
            "scope": scope,
            "title": "当日 Task 用量分析",
            "subtitle": time.strftime("%m-%d 00:00 至现在", time.localtime(end_at)),
            "start_at": start_at,
            "end_at": end_at,
            "fallback": False,
        }
    window = next(
        (
            window
            for bucket in usage.get("buckets", [])
            if isinstance(bucket, dict) and str(bucket.get("id") or "") == "codex"
            for window in bucket.get("windows", [])
            if isinstance(window, dict)
            and int(window.get("window_minutes") or 0) > 0
            and int(window.get("resets_at") or 0) > 0
        ),
        None,
    )
    if window is None:
        start_at = end_at - 7 * 24 * 60 * 60
        reset_at = 0
    else:
        reset_at = int(window["resets_at"])
        start_at = reset_at - int(window["window_minutes"]) * 60
    return {
        "scope": scope,
        "title": "当期 Task 用量分析",
        "subtitle": (
            f"{time.strftime('%m-%d %H:%M', time.localtime(start_at))} 至 "
            f"{time.strftime('%m-%d %H:%M', time.localtime(reset_at))} 重置"
            if reset_at
            else "最近 7 天（额度周期暂不可用）"
        ),
        "start_at": start_at,
        "end_at": end_at,
        "fallback": not bool(reset_at),
    }


def rollout_timestamp(value: Any) -> float:
    if not isinstance(value, str) or not value:
        return 0
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.timestamp()
    except ValueError:
        return 0


def rollout_offset_for_time(path: Path, since: float) -> int:
    timestamp_pattern = re.compile(br'"timestamp"\s*:\s*"([^"]+)"')
    size = path.stat().st_size
    low, high = 0, size
    with path.open("rb") as handle:
        while high - low > 4096:
            middle = (low + high) // 2
            handle.seek(middle)
            if middle:
                handle.readline()
            prefix = handle.readline(4096)
            match = timestamp_pattern.search(prefix)
            observed = rollout_timestamp(
                match.group(1).decode("utf-8", errors="ignore") if match else ""
            )
            if observed and observed < since:
                low = handle.tell()
            else:
                high = middle
    return max(0, low - 1)


def task_usage_from_rollout(path: Path, since: float, until: float) -> dict[str, int]:
    fields = (
        "input_tokens",
        "cached_input_tokens",
        "cache_write_input_tokens",
        "output_tokens",
        "reasoning_output_tokens",
        "total_tokens",
    )
    result = {field: 0 for field in fields}
    result.update(
        {
            "model_calls": 0,
            "turns": 0,
            "tool_calls": 0,
            "compactions": 0,
            "file_changes": 0,
            "subagent_events": 0,
        }
    )
    markers = (
        b'"token_count"',
        b'"user_message"',
        b'"custom_tool_call"',
        b'"function_call"',
        b'"context_compacted"',
        b'"patch_apply_end"',
        b'"sub_agent_activity"',
    )
    previous_total: dict[str, int] | None = None
    with path.open("rb") as handle:
        offset = rollout_offset_for_time(path, since)
        handle.seek(offset)
        if offset:
            handle.readline()
        for raw_line in handle:
            if not any(marker in raw_line for marker in markers):
                continue
            try:
                record = json.loads(raw_line)
            except (UnicodeDecodeError, json.JSONDecodeError):
                continue
            observed = rollout_timestamp(record.get("timestamp"))
            if observed < since:
                continue
            if observed > until:
                break
            payload = record.get("payload")
            if not isinstance(payload, dict):
                continue
            payload_type = str(payload.get("type") or "")
            record_type = str(record.get("type") or "")
            if record_type == "event_msg" and payload_type == "token_count":
                info = payload.get("info")
                if not isinstance(info, dict):
                    continue
                total_raw = info.get("total_token_usage")
                last_raw = info.get("last_token_usage")
                if not isinstance(total_raw, dict) or not isinstance(last_raw, dict):
                    continue
                total = {field: max(0, int(total_raw.get(field) or 0)) for field in fields}
                if previous_total == total:
                    continue
                if previous_total is not None and all(
                    total[field] >= previous_total[field] for field in fields
                ):
                    delta = {
                        field: total[field] - previous_total[field] for field in fields
                    }
                else:
                    delta = {
                        field: max(0, int(last_raw.get(field) or 0)) for field in fields
                    }
                for field in fields:
                    result[field] += delta[field]
                result["model_calls"] += 1
                previous_total = total
            elif record_type == "event_msg" and payload_type == "user_message":
                result["turns"] += 1
            elif record_type == "response_item" and payload_type in {
                "custom_tool_call",
                "function_call",
            }:
                result["tool_calls"] += 1
            elif record_type == "event_msg" and payload_type == "context_compacted":
                result["compactions"] += 1
            elif record_type == "event_msg" and payload_type == "patch_apply_end":
                result["file_changes"] += 1
            elif record_type == "event_msg" and payload_type == "sub_agent_activity":
                result["subagent_events"] += 1
    return result


def task_usage_reason(item: dict[str, Any], median_per_call: float) -> tuple[str, str]:
    total = int(item.get("total_tokens") or 0)
    calls = max(1, int(item.get("model_calls") or 0))
    turns = int(item.get("turns") or 0)
    tools = int(item.get("tool_calls") or 0)
    compactions = int(item.get("compactions") or 0)
    per_call = total / calls
    input_tokens = max(1, int(item.get("input_tokens") or 0))
    output_tokens = max(1, int(item.get("output_tokens") or 0))
    cached_ratio = int(item.get("cached_input_tokens") or 0) / input_tokens
    reasoning_ratio = int(item.get("reasoning_output_tokens") or 0) / output_tokens
    reasons: list[str] = []
    if cached_ratio >= 0.6:
        reasons.append(f"长上下文/缓存输入 {cached_ratio:.0%}")
    if tools >= 5:
        reasons.append(f"工具调用 {tools} 次")
    if reasoning_ratio >= 0.3:
        reasons.append(f"推理输出占比 {reasoning_ratio:.0%}")
    if compactions:
        reasons.append(f"上下文压缩 {compactions} 次")
    if not reasons:
        reasons.append(f"{turns} 轮交互、{calls} 次模型调用")
    unusually_large_call = calls >= 2 and per_call > max(50_000, median_per_call * 1.8)
    long_context_pressure = compactions >= 2 and cached_ratio >= 0.75
    if unusually_large_call or long_context_pressure:
        assessment = "偏高，需要关注"
    elif calls >= max(6, turns * 2) or tools >= 10:
        assessment = "正常，任务活跃度较高"
    else:
        assessment = "正常"
    return assessment, "；".join(reasons[:3])


def analyze_user_task_usage(
    user_id: str,
    time_range: dict[str, Any],
    force: bool = False,
) -> dict[str, Any]:
    cache_key = (
        user_id,
        str(time_range["scope"]),
        int(float(time_range["start_at"])),
        "\0".join(sorted(allowed_projects_for(user_id))),
    )
    with _task_usage_cache_lock:
        cached = _task_usage_cache.get(cache_key)
        if not force and cached and time.time() - cached[0] < TASK_USAGE_CACHE_SECONDS:
            return json.loads(json.dumps(cached[1]))
    tasks = recent_tasks(user_id) + archived_tasks(user_id)
    unique_tasks = {str(task["id"]): task for task in tasks}
    items: list[dict[str, Any]] = []
    since = float(time_range["start_at"])
    until = float(time_range["end_at"])
    for task in unique_tasks.values():
        path = rollout_path_for_task(str(task["id"]))
        if path is None or not path.is_file() or path.stat().st_mtime < since:
            continue
        metrics = task_usage_from_rollout(path, since, until)
        if int(metrics.get("total_tokens") or 0) <= 0:
            continue
        items.append({"task": task, **metrics})
    items.sort(key=lambda item: int(item["total_tokens"]), reverse=True)
    total_tokens = sum(int(item["total_tokens"]) for item in items)
    per_call_values = sorted(
        int(item["total_tokens"]) / max(1, int(item["model_calls"]))
        for item in items
    )
    median_per_call = (
        per_call_values[len(per_call_values) // 2] if per_call_values else 0
    )
    for item in items:
        item["share_percent"] = round(
            int(item["total_tokens"]) / total_tokens * 100 if total_tokens else 0
        )
        assessment, reason = task_usage_reason(item, median_per_call)
        item["assessment"] = assessment
        item["reason"] = reason
    analysis = {
        **time_range,
        "tasks": items,
        "total_tokens": total_tokens,
        "analyzed_at": time.time(),
    }
    with _task_usage_cache_lock:
        _task_usage_cache[cache_key] = (time.time(), analysis)
    return json.loads(json.dumps(analysis))


def compact_token_count(value: int) -> str:
    if value >= 1_000_000:
        return f"{value / 1_000_000:.1f}M"
    if value >= 1_000:
        return f"{value / 1_000:.1f}K"
    return str(value)


def build_task_usage_analysis_card(
    analysis: dict[str, Any],
    status: str = "",
) -> dict[str, Any]:
    title = str(analysis.get("title") or "Task 用量分析")
    subtitle = str(analysis.get("subtitle") or "正在统计")
    lines: list[str] = []
    if status:
        lines.append(f"✅ **{card_markdown_escape(status)}**")
    tasks = analysis.get("tasks", [])
    total = int(analysis.get("total_tokens") or 0)
    if tasks:
        lines.append(
            f"共 {len(tasks)} 个活跃 Task · 可见 Token 合计 {compact_token_count(total)}"
        )
        for index, item in enumerate(tasks[:5], start=1):
            task = item.get("task", {})
            identity = f"{task.get('project') or '无项目'} · {task.get('title') or '未命名 Task'}"
            calls = max(1, int(item.get("model_calls") or 0))
            per_call = int(item.get("total_tokens") or 0) // calls
            lines.extend(
                [
                    f"**{index}. {card_markdown_escape(identity)}**",
                    f"用量：{compact_token_count(int(item['total_tokens']))} Token "
                    f"· 占可见 Task {int(item.get('share_percent') or 0)}% "
                    f"· 单次模型调用约 {compact_token_count(per_call)}",
                    f"判断：{card_markdown_escape(str(item.get('assessment') or '正常'))}",
                    f"主要原因：{card_markdown_escape(str(item.get('reason') or '常规交互'))}",
                ]
            )
    else:
        lines.append("这个统计区间内没有检测到可见 Task 的 Token 用量。")
    if analysis.get("fallback"):
        lines.append("<font color='orange'>官方额度周期暂不可用，当前按最近 7 天统计。</font>")
    lines.append(
        "<font color='grey'>仅统计你有权查看的项目；Token 用于 Task 间比较和异常诊断，"
        "不等同于官方额度或账单的按 Task 扣减。偏高表示单次调用明显高于同期 Task，"
        "或长上下文伴随反复压缩。</font>"
    )
    refresh_action = (
        "show_daily_task_usage_analysis"
        if analysis.get("scope") == "daily"
        else "show_period_task_usage_analysis"
    )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": title},
        },
        "header": {
            "title": {"tag": "plain_text", "content": title},
            "subtitle": {"tag": "plain_text", "content": subtitle},
            "template": "blue",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {"tag": "markdown", "content": "\n\n".join(lines)},
                usage_analysis_button("重新分析", refresh_action, True),
                usage_analysis_button("返回实时用量", "show_codex_usage"),
            ],
        },
    }


def build_task_usage_loading_card(scope: str) -> dict[str, Any]:
    time_range = task_usage_time_range(scope, codex_usage_snapshot())
    return build_task_usage_analysis_card(time_range, "正在读取 Task 用量并分析原因")


def refresh_task_usage_analysis_card(
    user_id: str,
    message_id: str,
    scope: str,
) -> None:
    refresh_codex_usage()
    time_range = task_usage_time_range(scope, codex_usage_snapshot())
    try:
        analysis = analyze_user_task_usage(user_id, time_range, force=True)
        card = build_task_usage_analysis_card(analysis, "分析已更新")
    except (OSError, RuntimeError, sqlite3.Error) as exc:
        log(f"Task usage analysis failed: {type(exc).__name__}")
        card = build_task_usage_analysis_card(
            {**time_range, "tasks": [], "total_tokens": 0},
            "分析暂时不可用，请稍后重试",
        )
    if message_id:
        patch_card(message_id, card)


def refresh_codex_usage_card(message_id: str) -> None:
    refreshed = refresh_codex_usage()
    if message_id:
        patch_card(
            message_id,
            build_codex_usage_card(
                codex_usage_snapshot(),
                "用量已刷新" if refreshed else "用量暂时无法刷新",
            ),
        )


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
        remember_recent_task(state, user_id, str(task["id"]))
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
    schedule_user_task_identity_refresh(user_id, "当前 Task 已新建", task)


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


def pending_card_patch_delay(attempts: int) -> int:
    index = min(max(attempts, 0), len(PENDING_CARD_PATCH_DELAYS) - 1)
    return PENDING_CARD_PATCH_DELAYS[index]


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
        state["pending_replies"] = trim_pending_replies(pending)
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
                else timestamp + pending_card_patch_delay(0)
            ),
        }
        pending = [item for item in pending if item is not existing]
        pending.append(entry)
        state["pending_replies"] = trim_pending_replies(pending)
        save_state(state)
    log(f"card patch queued reason={entry['reason']}")


def queue_pending_menu_card(
    user_id: str,
    card: dict[str, Any],
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
            "operation": "menu_card",
            "user_id": user_id,
            "card": card,
            "kind": kind,
            "reason": reason or "飞书 API 调用失败",
            "attempts": 0,
            "created_at": timestamp,
            "next_attempt_at": timestamp + pending_card_patch_delay(0),
        }
        pending = [
            item
            for item in pending
            if not (
                isinstance(item, dict)
                and item.get("operation") == "menu_card"
                and item.get("kind") == kind
            )
        ]
        pending.append(entry)
        state["pending_replies"] = trim_pending_replies(pending)
        save_state(state)
    log("menu card queued reason=" + entry["reason"])


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
        state["pending_replies"] = trim_pending_replies(pending)
        save_state(state)
    log(f"queue card queued reason={entry['reason']}")


def pending_image_spool_directory() -> Path:
    return STATE_PATH.parent / "reply-images"


def pending_file_spool_directory() -> Path:
    return STATE_PATH.parent / "reply-files"


def remove_pending_resource_file(item: dict[str, Any]) -> None:
    operation = item.get("operation")
    if operation == "image_reply":
        if item.get("remote") is True:
            return
        raw_path = str(item.get("image") or "")
        spool = pending_image_spool_directory()
    elif operation in {"audio_reply", "file_reply"}:
        raw_path = str(item.get("file") or "")
        spool = pending_file_spool_directory()
    else:
        return
    if not raw_path:
        return
    path = Path(raw_path)
    try:
        path.resolve().relative_to(spool.resolve())
        path.unlink(missing_ok=True)
        if operation in {"audio_reply", "file_reply"} and path.parent != spool:
            path.parent.rmdir()
    except (OSError, ValueError):
        return


def trim_pending_replies(pending: list[Any]) -> list[Any]:
    transient_operations = {"card_patch", "menu_card", "queue_card"}
    transient_indexes = [
        index
        for index, item in enumerate(pending)
        if isinstance(item, dict)
        and str(item.get("operation") or "") in transient_operations
    ]
    while len(transient_indexes) > MAX_PENDING_REPLIES:
        pending.pop(transient_indexes.pop(0))
        transient_indexes = [index - 1 for index in transient_indexes]
    return pending


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
            f"{message_id}:{index}:{source}:{source.stat().st_mtime_ns}:{size}".encode(
                "utf-8"
            )
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
        new_entry = {
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
        candidate = [item for item in pending if item not in replaced] + [new_entry]
        if not remote:
            local_items = [
                item
                for item in candidate
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
            if total > MAX_PENDING_IMAGE_SPOOL_BYTES:
                remove_pending_resource_file(
                    {"operation": "image_reply", "image": stored_image}
                )
                return False
        for item in replaced:
            if str(item.get("image") or "") != stored_image:
                remove_pending_resource_file(item)
        state["pending_replies"] = trim_pending_replies(candidate)
        save_state(state)
    log(f"image reply queued index={index} reason={reason or '飞书 API 调用失败'}")
    return True


def queue_pending_local_file(
    message_id: str,
    file_path: str,
    index: int,
    reason: str,
    operation: str,
    now: float | None = None,
) -> bool:
    source = Path(file_path).resolve()
    try:
        size = source.stat().st_size
    except OSError:
        return False
    if (
        operation not in {"audio_reply", "file_reply"}
        or source.suffix.lower()
        not in (AUDIO_SUFFIXES if operation == "audio_reply" else FILE_SUFFIXES)
        or size <= 0
        or size > MAX_PENDING_FILE_BYTES
    ):
        return False
    spool = pending_file_spool_directory()
    spool.mkdir(parents=True, exist_ok=True)
    spool.chmod(0o700)
    digest = hashlib.sha256(
        (
            f"{operation}:{message_id}:{index}:{source}:"
            f"{source.stat().st_mtime_ns}:{size}"
        ).encode("utf-8")
    ).hexdigest()[:32]
    item_directory = spool / digest
    item_directory.mkdir(mode=0o700, exist_ok=True)
    item_directory.chmod(0o700)
    target = item_directory / source.name
    temporary = item_directory / f".{source.name}.tmp"
    try:
        shutil.copy2(source, temporary)
        temporary.replace(target)
        target.chmod(0o600)
    except OSError:
        temporary.unlink(missing_ok=True)
        return False

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
            and item.get("operation") == operation
            and item.get("message_id") == message_id
            and int(item.get("index") or 0) == index
        ]
        new_entry = {
            "operation": operation,
            "message_id": message_id,
            "file": str(target),
            "index": index,
            "reason": reason or "飞书 API 调用失败",
            "attempts": 0,
            "created_at": timestamp,
            "next_attempt_at": timestamp + pending_reply_delay(0),
        }
        candidate = [item for item in pending if item not in replaced] + [new_entry]
        local_items = [
            item
            for item in candidate
            if isinstance(item, dict)
            and item.get("operation") in {"audio_reply", "file_reply"}
        ]
        total = sum(
            Path(str(item.get("file") or "")).stat().st_size
            if Path(str(item.get("file") or "")).is_file()
            else 0
            for item in local_items
        )
        if total > MAX_PENDING_FILE_SPOOL_BYTES:
            remove_pending_resource_file(
                {"operation": operation, "file": str(target)}
            )
            return False
        for item in replaced:
            if str(item.get("file") or "") != str(target):
                remove_pending_resource_file(item)
        state["pending_replies"] = trim_pending_replies(candidate)
        save_state(state)
    log(
        f"{operation} queued index={index} "
        f"reason={reason or '飞书 API 调用失败'}"
    )
    return True


def queue_pending_audio(
    message_id: str,
    audio_path: str,
    index: int,
    reason: str,
    now: float | None = None,
) -> bool:
    return queue_pending_local_file(
        message_id,
        audio_path,
        index,
        reason,
        "audio_reply",
        now,
    )


def queue_pending_file(
    message_id: str,
    file_path: str,
    index: int,
    reason: str,
    now: float | None = None,
) -> bool:
    return queue_pending_local_file(
        message_id,
        file_path,
        index,
        reason,
        "file_reply",
        now,
    )


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


def complete_reply_chunks(text: str) -> list[str]:
    content = text.strip() or "Codex 没有返回文字结果。"
    chunk_size = max(1, MAX_REPLY_CHARS)
    return [
        content[offset : offset + chunk_size]
        for offset in range(0, len(content), chunk_size)
    ]


def reply_complete_result(
    message_id: str,
    text: str,
    kind_prefix: str,
) -> bool:
    for index, chunk in enumerate(complete_reply_chunks(text), start=1):
        kind = f"{kind_prefix}-{index}"
        if reply(message_id, chunk, kind):
            continue
        queue_pending_reply(
            message_id,
            chunk,
            kind,
            current_reply_failure_reason() or "飞书 API 调用失败",
        )
    return True


def pending_retry_identity(item: dict[str, Any]) -> tuple[str, ...]:
    operation = str(item.get("operation") or "text_reply")
    if operation == "queue_card":
        return (operation, str(item.get("queue_id") or ""))
    if operation in {"audio_reply", "image_reply", "file_reply"}:
        return (
            operation,
            str(item.get("message_id") or ""),
            str(item.get("index") or ""),
        )
    return (
        operation,
        str(item.get("message_id") or ""),
        str(item.get("kind") or "final"),
    )


def retry_pending_replies(now: float | None = None) -> bool:
    global _last_reply_failure_reason

    timestamp = time.time() if now is None else now
    menu_card_item: dict[str, Any] | None = None
    with _state_lock:
        state = load_state()
        pending = state.get("pending_replies")
        if isinstance(pending, list):
            for index, item in enumerate(pending):
                if not isinstance(item, dict) or item.get("operation") != "menu_card":
                    continue
                try:
                    next_attempt_at = float(item.get("next_attempt_at") or 0)
                except (TypeError, ValueError):
                    next_attempt_at = 0
                if next_attempt_at > timestamp:
                    continue
                if (
                    not str(item.get("user_id") or "")
                    or not str(item.get("kind") or "")
                    or not isinstance(item.get("card"), dict)
                ):
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    return True
                menu_card_item = dict(item)
                break
    if menu_card_item is not None:
        user_id = str(menu_card_item["user_id"])
        kind = str(menu_card_item["kind"])
        card = menu_card_item["card"]
        delivered, chat_id, message_id = send_card(user_id, card, kind)
        with _state_lock:
            state = load_state()
            pending = state.get("pending_replies")
            if not isinstance(pending, list):
                return True
            current = next(
                (
                    item
                    for item in pending
                    if isinstance(item, dict)
                    and item.get("operation") == "menu_card"
                    and item.get("kind") == kind
                ),
                None,
            )
            if current is None:
                return True
            if delivered:
                pending.remove(current)
                state["pending_replies"] = pending
                if chat_id:
                    authorize_chat(state, user_id, chat_id)
                if message_id:
                    remember_card_context(state, user_id, message_id, card)
                save_state(state)
                log("pending menu card delivered")
                return True
            try:
                attempts = int(current.get("attempts") or 0) + 1
            except (TypeError, ValueError):
                attempts = 1
            current["attempts"] = attempts
            current["next_attempt_at"] = timestamp + pending_card_patch_delay(attempts)
            state["pending_replies"] = pending
            save_state(state)
            log(
                "pending menu card retry failed "
                f"attempts={attempts} reason={current.get('reason') or '飞书 API 调用失败'}"
            )
            return True
    card_patch_item: dict[str, Any] | None = None
    with _state_lock:
        state = load_state()
        pending = state.get("pending_replies")
        if isinstance(pending, list):
            for index, item in enumerate(pending):
                if not isinstance(item, dict) or item.get("operation") != "card_patch":
                    continue
                try:
                    next_attempt_at = float(item.get("next_attempt_at") or 0)
                except (TypeError, ValueError):
                    next_attempt_at = 0
                if next_attempt_at > timestamp:
                    continue
                message_id = str(item.get("message_id") or "")
                card = item.get("card")
                if not message_id or not isinstance(card, dict):
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    return True
                card_patch_item = dict(item)
                break
    if card_patch_item is not None:
        message_id = str(card_patch_item["message_id"])
        card = card_patch_item["card"]
        delivered = patch_card(message_id, card, persist=False)
        with _state_lock:
            state = load_state()
            pending = state.get("pending_replies")
            if not isinstance(pending, list):
                return True
            current = next(
                (
                    item
                    for item in pending
                    if isinstance(item, dict)
                    and item.get("operation") == "card_patch"
                    and item.get("message_id") == message_id
                ),
                None,
            )
            if current is None:
                return True
            if delivered and current.get("card") == card:
                pending.remove(current)
                state["pending_replies"] = pending
                save_state(state)
                log("pending card patch delivered")
                return True
            if delivered:
                current["next_attempt_at"] = timestamp
                state["pending_replies"] = pending
                save_state(state)
                return True
            try:
                attempts = int(current.get("attempts") or 0) + 1
            except (TypeError, ValueError):
                attempts = 1
            current["attempts"] = attempts
            current["next_attempt_at"] = timestamp + pending_card_patch_delay(attempts)
            state["pending_replies"] = pending
            save_state(state)
            log(
                "pending card patch retry failed "
                f"attempts={attempts} reason={current.get('reason') or '飞书 API 调用失败'}"
            )
            return True
    retry_item: dict[str, Any] | None = None
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
            if operation in {"menu_card", "card_patch"}:
                continue
            message_id = str(item.get("message_id") or "")
            prepared = dict(item)
            if operation == "queue_card":
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
                prepared["prepared_card"] = build_queued_card(dict(queued_entry), position)
            elif operation == "image_reply":
                image = str(item.get("image") or "")
                try:
                    image_index = int(item.get("index") or 0)
                except (TypeError, ValueError):
                    image_index = 0
                if not message_id or not image or image_index <= 0 or (
                    item.get("remote") is not True and not Path(image).is_file()
                ):
                    remove_pending_resource_file(item)
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    return True
            elif operation in {"audio_reply", "file_reply"}:
                file_path = str(item.get("file") or "")
                try:
                    file_index = int(item.get("index") or 0)
                except (TypeError, ValueError):
                    file_index = 0
                if not message_id or not file_path or file_index <= 0 or not Path(file_path).is_file():
                    remove_pending_resource_file(item)
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    return True
            elif operation == "text_reply":
                text = str(item.get("text") or "")
                kind = str(item.get("kind") or "final")
                if (
                    not message_id
                    or not text
                    or not (
                        kind in {"final", "workflow-choice"}
                        or kind.startswith("final-")
                    )
                ):
                    pending.pop(index)
                    state["pending_replies"] = pending
                    save_state(state)
                    return True
            else:
                pending.pop(index)
                state["pending_replies"] = pending
                save_state(state)
                return True
            retry_item = prepared
            break
    if retry_item is None:
        return False

    operation = str(retry_item.get("operation") or "text_reply")
    message_id = str(retry_item.get("message_id") or "")
    progress_message_id: str | None = None
    try:
        if operation == "queue_card":
            delivered, progress_message_id = reply_card_message(
                message_id,
                retry_item["prepared_card"],
                f"queue-{retry_item.get('queue_id') or ''}",
            )
            delivered = bool(delivered and progress_message_id)
        elif operation == "image_reply":
            delivered = reply_image(
                message_id,
                str(retry_item.get("image") or ""),
                int(retry_item.get("index") or 0),
            )
        elif operation == "audio_reply":
            delivered = reply_result_audio(
                message_id,
                str(retry_item.get("file") or ""),
                int(retry_item.get("index") or 0),
            )
        elif operation == "file_reply":
            delivered = reply_file(
                message_id,
                str(retry_item.get("file") or ""),
                int(retry_item.get("index") or 0),
            )
        else:
            delivered = reply(
                message_id,
                str(retry_item.get("text") or ""),
                str(retry_item.get("kind") or "final"),
            )
    except (OSError, subprocess.TimeoutExpired):
        delivered = False

    retry_identity = pending_retry_identity(retry_item)
    reason = str(retry_item.get("reason") or "飞书 API 调用失败")
    kind = str(retry_item.get("kind") or "final")
    with _state_lock:
        state = load_state()
        pending = state.get("pending_replies")
        if not isinstance(pending, list):
            return True
        current = next(
            (
                item
                for item in pending
                if isinstance(item, dict)
                and pending_retry_identity(item) == retry_identity
            ),
            None,
        )
        if current is None:
            return True
        if delivered:
            if operation == "queue_card":
                queue_id = str(retry_item.get("queue_id") or "")
                queued_entry = next(
                    (
                        entry
                        for entry in pending_inputs(state)
                        if isinstance(entry, dict)
                        and str(entry.get("queue_id") or "") == queue_id
                    ),
                    None,
                )
                if queued_entry is not None:
                    queued_entry["progress_message_id"] = progress_message_id
                    queued_entry["ready"] = True
            if operation in {"audio_reply", "image_reply", "file_reply"}:
                remove_pending_resource_file(current)
            pending.remove(current)
            state["pending_replies"] = pending
            save_state(state)
        else:
            try:
                attempts = int(current.get("attempts") or 0) + 1
            except (TypeError, ValueError):
                attempts = 1
            current["attempts"] = attempts
            if operation in {
                "text_reply",
                "audio_reply",
                "image_reply",
                "file_reply",
            }:
                current["reason"] = _last_reply_failure_reason or current.get("reason")
            current["next_attempt_at"] = timestamp + pending_reply_delay(attempts)
            state["pending_replies"] = pending
            save_state(state)
    if delivered:
        log(f"pending {operation} delivered previous_reason={reason}")
        if operation == "text_reply" and kind == "final":
            reply(
                message_id,
                f"上一条结果曾因{reason}未能及时送达，连接恢复后已自动补发。",
                f"{kind}-recovered",
            )
    else:
        log(
            f"pending reply retry failed kind={kind} "
            f"reason={_last_reply_failure_reason or reason}"
        )
    return True


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


def result_roots_for_task(task: dict[str, Any]) -> tuple[Path, ...]:
    roots: list[Path] = []
    task_id = str(task.get("id") or "")
    if task_id:
        try:
            working_directory = task_working_directory(task_id)
        except (OSError, sqlite3.Error):
            working_directory = ""
        if working_directory:
            roots.append(Path(working_directory).resolve())
    controlled_output = STATE_PATH.parent / "result-attachments"
    if controlled_output.is_dir():
        roots.append(controlled_output.resolve())
    return tuple(dict.fromkeys(roots))


def local_result_path_allowed(path: Path, allowed_roots: tuple[Path, ...]) -> bool:
    resolved = path.resolve()
    for root in allowed_roots:
        try:
            resolved.relative_to(root.resolve())
            return True
        except ValueError:
            continue
    return False


def normalized_image_reference(
    reference: str,
    allowed_roots: tuple[Path, ...] = (),
    *,
    trusted_local: bool = False,
) -> str | None:
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
    resolved = path.resolve()
    if not trusted_local and not local_result_path_allowed(resolved, allowed_roots):
        return None
    return str(resolved)


def extract_result_images(
    text: str,
    allowed_roots: tuple[Path, ...] = (),
) -> tuple[str, list[str]]:
    images: list[str] = []

    def replace(match: re.Match[str]) -> str:
        image = normalized_image_reference(match.group(1), allowed_roots)
        if image is None:
            return "图片不可用"
        if image not in images:
            images.append(image)
        return "图片见下方"

    return MARKDOWN_IMAGE_PATTERN.sub(replace, text).strip(), images


def prepare_result_images(
    text: str,
    rollout_images: list[str],
    allowed_roots: tuple[Path, ...] = (),
) -> tuple[str, list[str]]:
    clean_text, linked_images = extract_result_images(text, allowed_roots)
    if MAX_RESULT_IMAGES == 0:
        return clean_text, []
    images: list[str] = []
    for reference in rollout_images:
        image = normalized_image_reference(reference, trusted_local=True)
        if image is not None and image not in images:
            images.append(image)
        if len(images) >= MAX_RESULT_IMAGES:
            break
    for reference in linked_images:
        image = normalized_image_reference(reference, allowed_roots)
        if image is not None and image not in images:
            images.append(image)
        if len(images) >= MAX_RESULT_IMAGES:
            break
    if images and "图片见下方" not in clean_text:
        clean_text = clean_text.rstrip() + "\n\n图片见下方。"
    return clean_text, images


def normalized_local_result_reference(
    reference: str,
    suffixes: set[str],
    allowed_roots: tuple[Path, ...] = (),
) -> str | None:
    value = reference.strip()
    if value.startswith("<") and value.endswith(">"):
        value = value[1:-1].strip()
    else:
        value = re.split(r"\s+(?=[\"'])", value, maxsplit=1)[0].strip()

    parsed = urlsplit(value)
    if parsed.scheme.lower() == "file":
        value = unquote(parsed.path)
    elif parsed.scheme:
        return None
    path = Path(value).expanduser()
    if (
        not path.is_absolute()
        or path.suffix.lower() not in suffixes
        or not path.is_file()
    ):
        return None
    try:
        size = path.stat().st_size
    except OSError:
        return None
    if size <= 0 or size > MAX_RESULT_FILE_BYTES:
        return None
    resolved = path.resolve()
    if not local_result_path_allowed(resolved, allowed_roots):
        return None
    return str(resolved)


def normalized_audio_reference(
    reference: str,
    allowed_roots: tuple[Path, ...] = (),
) -> str | None:
    return normalized_local_result_reference(reference, AUDIO_SUFFIXES, allowed_roots)


def normalized_file_reference(
    reference: str,
    allowed_roots: tuple[Path, ...] = (),
) -> str | None:
    return normalized_local_result_reference(reference, DOCUMENT_SUFFIXES, allowed_roots)


def is_native_opus(path: str | Path) -> bool:
    audio = Path(path)
    if audio.suffix.lower() not in NATIVE_AUDIO_SUFFIXES:
        return False
    try:
        with audio.open("rb") as handle:
            return b"OpusHead" in handle.read(64 * 1024)
    except OSError:
        return False


def extract_result_audio(
    text: str,
    limit: int | None = None,
    allowed_roots: tuple[Path, ...] = (),
) -> tuple[str, list[str]]:
    audio_files: list[str] = []

    def replace(match: re.Match[str]) -> str:
        audio_path = normalized_audio_reference(match.group(2), allowed_roots)
        if audio_path is None:
            return match.group(0)
        if (
            limit is not None
            and len(audio_files) >= limit
            and audio_path not in audio_files
        ):
            return match.group(0)
        if audio_path not in audio_files:
            audio_files.append(audio_path)
        label = match.group(1).strip() or Path(audio_path).name
        if is_native_opus(audio_path):
            return f"音频见下方：{label}"
        return f"音频附件见下方：{label}"

    return MARKDOWN_AUDIO_PATTERN.sub(replace, text).strip(), audio_files


def prepare_result_audio(
    text: str,
    allowed_roots: tuple[Path, ...] = (),
) -> tuple[str, list[str]]:
    if MAX_RESULT_AUDIO == 0:
        return text, []
    return extract_result_audio(text, MAX_RESULT_AUDIO, allowed_roots)


def extract_result_files(
    text: str,
    limit: int | None = None,
    allowed_roots: tuple[Path, ...] = (),
) -> tuple[str, list[str]]:
    files: list[str] = []

    def replace(match: re.Match[str]) -> str:
        file_path = normalized_file_reference(match.group(2), allowed_roots)
        if file_path is None:
            return match.group(0)
        if limit is not None and len(files) >= limit and file_path not in files:
            return match.group(0)
        if file_path not in files:
            files.append(file_path)
        label = match.group(1).strip() or Path(file_path).name
        return f"文件见下方：{label}"

    return MARKDOWN_FILE_PATTERN.sub(replace, text).strip(), files


def prepare_result_files(
    text: str,
    allowed_roots: tuple[Path, ...] = (),
) -> tuple[str, list[str]]:
    if MAX_RESULT_FILES == 0:
        return text, []
    return extract_result_files(text, MAX_RESULT_FILES, allowed_roots)


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


def reply_audio(message_id: str, audio_path: str, index: int) -> bool:
    set_reply_failure_reason("")
    path = Path(audio_path).resolve()
    if not is_native_opus(path):
        set_reply_failure_reason("该音频格式不支持飞书原生播放")
        return False
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-reply",
        "--message-id",
        message_id,
        "--audio",
        f"./{path.name}",
        "--as",
        "bot",
        "--idempotency-key",
        idempotency_key(message_id, f"audio-{index}"),
    ]
    for attempt in range(1, 3):
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=60,
                env=lark_environment(),
                cwd=path.parent,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            set_reply_failure_reason(lark_reply_failure_reason(error=exc))
            log(
                f"audio reply failed index={index} attempt={attempt} "
                f"reason={current_reply_failure_reason()}"
            )
            continue
        if lark_succeeded(result):
            set_reply_failure_reason("")
            return True
        set_reply_failure_reason(lark_reply_failure_reason(result=result))
        log(
            f"audio reply failed index={index} attempt={attempt} "
            f"{lark_reply_failure_metadata(result)} "
            f"reason={current_reply_failure_reason()}"
        )
    return False


def reply_file(
    message_id: str,
    file_path: str,
    index: int,
    *,
    kind_prefix: str = "file",
) -> bool:
    set_reply_failure_reason("")
    path = Path(file_path).resolve()
    command = [
        LARK_CLI,
        "--profile",
        LARK_PROFILE,
        "im",
        "+messages-reply",
        "--message-id",
        message_id,
        "--file",
        f"./{path.name}",
        "--as",
        "bot",
        "--idempotency-key",
        idempotency_key(message_id, f"{kind_prefix}-{index}"),
    ]
    for attempt in range(1, 3):
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=60,
                env=lark_environment(),
                cwd=path.parent,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            set_reply_failure_reason(lark_reply_failure_reason(error=exc))
            log(
                f"file reply failed index={index} attempt={attempt} "
                f"reason={current_reply_failure_reason()}"
            )
            continue
        if lark_succeeded(result):
            set_reply_failure_reason("")
            return True
        set_reply_failure_reason(lark_reply_failure_reason(result=result))
        log(
            f"file reply failed index={index} attempt={attempt} "
            f"{lark_reply_failure_metadata(result)} "
            f"reason={current_reply_failure_reason()}"
        )
    return False


def reply_result_audio(message_id: str, audio_path: str, index: int) -> bool:
    if is_native_opus(audio_path):
        return reply_audio(message_id, audio_path, index)
    return reply_file(
        message_id,
        audio_path,
        index,
        kind_prefix="audio-file",
    )


def deliver_result_resources(
    message_id: str,
    images: list[str],
    audio_files: list[str],
    files: list[str],
    *,
    notify_failures: bool = False,
) -> tuple[int, int, int]:
    resource_groups = (
        (
            "image",
            images,
            reply_image,
            queue_pending_image,
            "张图片",
        ),
        (
            "audio",
            audio_files,
            reply_result_audio,
            queue_pending_audio,
            "段音频",
        ),
        (
            "file",
            files,
            reply_file,
            queue_pending_file,
            "个文件",
        ),
    )
    failed_counts: list[int] = []
    for kind, resources, send_resource, queue_resource, unit in resource_groups:
        failed = 0
        queued = 0
        for index, resource in enumerate(resources, start=1):
            if send_resource(message_id, resource, index):
                continue
            failed += 1
            if queue_resource(
                message_id,
                resource,
                index,
                current_reply_failure_reason() or "飞书 API 调用失败",
            ):
                queued += 1
        failed_counts.append(failed)
        if notify_failures and failed:
            reply(
                message_id,
                (
                    f"有 {queued} {unit}暂未送达，连接恢复后会自动补发。"
                    if queued == failed
                    else f"有 {queued} {unit}等待自动补发，另有 "
                    f"{failed - queued} {unit}无法保存，请在 Codex Desktop 中查看。"
                ),
                f"{kind}-error",
            )
    return tuple(failed_counts)


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
        started = time.monotonic()
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=CARD_PATCH_TIMEOUT_SECONDS,
                env=lark_environment(),
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            duration_ms = round((time.monotonic() - started) * 1000)
            failure_reason = lark_reply_failure_reason(error=exc)
            log(
                f"card patch failed attempt={attempt} "
                f"duration_ms={duration_ms} reason={failure_reason}"
            )
        else:
            duration_ms = round((time.monotonic() - started) * 1000)
            if lark_succeeded(result):
                log(
                    "latency feishu_api operation=card_patch "
                    f"duration_ms={duration_ms} success=true"
                )
                if persist:
                    clear_pending_card_patch(message_id)
                return True
            failure_reason = lark_reply_failure_reason(result)
            log(
                f"card patch failed attempt={attempt} "
                f"duration_ms={duration_ms} "
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
    started = time.monotonic()
    try:
        result = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=CARD_SEND_TIMEOUT_SECONDS,
            env=lark_environment(),
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        duration_ms = round((time.monotonic() - started) * 1000)
        log(
            "card send failed "
            f"duration_ms={duration_ms} reason={lark_reply_failure_reason(error=exc)}"
        )
        return False, None, None
    duration_ms = round((time.monotonic() - started) * 1000)
    if not lark_succeeded(result):
        log(
            f"card send failed code={result.returncode} duration_ms={duration_ms}"
        )
        return False, None, None
    log(
        "latency feishu_api operation=card_send "
        f"duration_ms={duration_ms} success=true"
    )
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
    started = time.monotonic()
    try:
        result = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=CARD_PATCH_TIMEOUT_SECONDS,
            env=lark_environment(),
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        duration_ms = round((time.monotonic() - started) * 1000)
        log(
            "card update failed "
            f"duration_ms={duration_ms} reason={lark_reply_failure_reason(error=exc)}"
        )
        return False
    duration_ms = round((time.monotonic() - started) * 1000)
    if not lark_succeeded(result):
        log(
            f"card update failed code={result.returncode} duration_ms={duration_ms}"
        )
        return False
    log(
        "latency feishu_api operation=card_update "
        f"duration_ms={duration_ms} success=true"
    )
    return True


def workflow_task_label(
    task: dict[str, str] | None,
    fallback: str,
) -> str:
    if not task:
        return fallback
    return f"{task.get('project') or '无项目'} → {task.get('title') or '未命名 Task'}"


def workflow_task_route(
    user_id: str,
) -> tuple[dict[str, str] | None, dict[str, str] | None]:
    try:
        target_task = task_by_id(workflow_codex_task_id(), user_id)
        current_task = selected_task(user_id, load_state())
    except (OSError, sqlite3.Error):
        return None, None
    return target_task, current_task


def build_workflow_card(
    record: dict[str, Any],
    reminder: bool = False,
    completed: bool = False,
    user_id: str = "",
    route_status: str = "",
) -> dict[str, Any]:
    requires_action = record.get("status") == "user_action_required"
    selected = record.get("selected_action")
    title = str(record.get("task_id") or "自动化工作流")[:128]
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

    if requires_action and user_id:
        target_task, current_task = workflow_task_route(user_id)
        target_label = workflow_task_label(target_task, "工作流专用 Task")
        current_label = workflow_task_label(current_task, "尚未选择")
        same_task = bool(
            target_task
            and current_task
            and str(target_task.get("id") or "") == str(current_task.get("id") or "")
        )
        route_lines = [
            (
                "**结果提交到 Task**"
                if completed
                else "**本卡片的处理目标 Task**"
            ),
            card_markdown_escape(target_label),
            "**当前聊天 Task**",
            card_markdown_escape(current_label),
        ]
        if route_status:
            route_lines.append(f"✅ **{card_markdown_escape(route_status)}**")
        elif same_task:
            route_lines.append("当前聊天 Task 与处理目标一致。")
        else:
            route_lines.append(
                "<font color='orange'>两者不同。处理本卡片不会自动切换当前 Task。</font>"
            )
        if completed:
            route_lines.append("无需再发送“已点击”等确认文字。")
        elements.insert(
            0,
            {"tag": "markdown", "content": "\n".join(route_lines)},
        )
        if completed and target_task and not same_task:
            callback_base = {
                "workflow_id": str(record.get("workflow_id") or ""),
                "event_id": str(record.get("event_id") or ""),
                "task_id": str(target_task.get("id") or ""),
            }
            elements.extend(
                [
                    {
                        "tag": "button",
                        "text": {
                            "tag": "plain_text",
                            "content": "切换到目标 Task",
                        },
                        "type": "primary_filled",
                        "width": "fill",
                        "behaviors": [
                            {
                                "type": "callback",
                                "value": {
                                    **callback_base,
                                    "action": "workflow_switch_task",
                                },
                            }
                        ],
                    },
                    {
                        "tag": "button",
                        "text": {
                            "tag": "plain_text",
                            "content": "保持当前 Task",
                        },
                        "type": "default",
                        "width": "fill",
                        "behaviors": [
                            {
                                "type": "callback",
                                "value": {
                                    **callback_base,
                                    "action": "workflow_keep_current_task",
                                },
                            }
                        ],
                    },
                ]
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
            "summary": {"content": f"自动化工作流 · {tag_text}"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": title},
            "subtitle": {
                "tag": "plain_text",
                "content": "自动化工作流" + (" · 24 小时提醒" if reminder else ""),
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
            build_workflow_card(record, reminder=reminder, user_id=user_id),
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
            "这是自动化工作流的 TEST-ROUNDTRIP 往返测试。\n"
            f"{workflow_recovery_signature(recovery)}\n"
            f"选择：{action_label}\n"
            f"测试事项：{summary}\n\n"
            "只回报这次测试回执：专用 Codex Task 已收到一次飞书选择。"
            "不得调用外部业务系统、编排器或正式恢复命令；不得读取或修改仓库文件；"
            "不得租用、推进或改变任何正式研发任务。"
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
        "用户已通过飞书处理自动化工作流人工门。\n"
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
            "自动化工作流飞书桥往返测试响应\n"
            f"roundtrip_event_id: {recovery.get('event_id')}\n"
            f"selected_action_id: {recovery.get('selected_action_id')}\n"
            f"resolution: {recovery.get('resolution')}"
        )
    return (
        "自动化工作流飞书人工门响应\n"
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
    user_id = str(
        event.get("operator_id")
        or (
            event.get("sender_id")
            if event.get("type") not in {"card.action.trigger", "application.bot.menu_v6"}
            else ""
        )
        or ""
    )
    chat_id = str(event.get("chat_id") or "")
    expected_chat = configured_chat_id or str(record.get("chat_id") or "")
    return user_id == recipient and bool(chat_id) and chat_id == expected_chat


def workflow_completed_card(
    record: dict[str, Any],
    route_status: str = "",
) -> dict[str, Any]:
    recipient, _chat_id = workflow_recipient()
    return build_workflow_card(
        record,
        completed=True,
        user_id=recipient,
        route_status=route_status,
    )


def patch_workflow_completed_cards(
    record: dict[str, Any],
    route_status: str = "",
) -> None:
    card = workflow_completed_card(record, route_status=route_status)
    message_ids = dict.fromkeys(
        str(record.get(key) or "")
        for key in ("message_id", "reminder_message_id")
    )
    for message_id in message_ids:
        if message_id.startswith("om_"):
            patch_card(message_id, card)


def workflow_event_processed(key: str) -> bool:
    with _state_lock:
        return processed_event_seen(load_state(), key)


def handle_workflow_card_action(
    event: dict[str, Any],
    payload: dict[str, Any],
) -> bool:
    action = str(payload.get("action") or "")
    if action not in {
        "workflow_decision",
        "workflow_switch_task",
        "workflow_keep_current_task",
    }:
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
    if action in {"workflow_switch_task", "workflow_keep_current_task"}:
        processed_key = f"workflow-route:{bridge_event_id}"
        if not mark_processed(load_state(), processed_key):
            return True
        user_id = str(event.get("operator_id") or "")
        target_task_id = str(payload.get("task_id") or "")
        if target_task_id != workflow_codex_task_id():
            log("workflow route ignored reason=target-mismatch")
            return True
        if action == "workflow_switch_task":
            target_task = task_by_id(target_task_id, user_id)
            if target_task is None:
                patch_workflow_completed_cards(record, "目标 Task 当前不可用")
                return True
            with _state_lock:
                state = load_state()
                state.setdefault("selected", {})[user_id] = target_task_id
                state.setdefault("last_projects", {})[user_id] = target_task["project"]
                remember_recent_task(state, user_id, target_task_id)
                save_state(state)
            patch_workflow_completed_cards(record, "已切换到目标 Task")
            schedule_user_task_identity_refresh(
                user_id,
                "当前 Task 已切换",
                target_task,
            )
            log("workflow route switched to dedicated Codex Task")
            return True
        patch_workflow_completed_cards(record, "已保持当前 Task")
        log("workflow route kept current Codex Task")
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


def task_status_prefix(
    task: dict[str, str],
    status: str,
    is_current: bool = True,
) -> str:
    identity = (
        current_task_text(task)
        if is_current
        else f"🔵 结果所属 Task\n{task_project_text(task)}\n{task_title_text(task)}"
    )
    return f"{identity}\n状态：{status}\n\n"


def current_task_tag() -> dict[str, Any]:
    return {
        "tag": "text_tag",
        "text": {"tag": "plain_text", "content": "当前 Task"},
        "color": "green",
    }


def task_role_tag(is_current: bool, other_label: str) -> dict[str, Any]:
    if is_current:
        return current_task_tag()
    return {
        "tag": "text_tag",
        "text": {"tag": "plain_text", "content": other_label},
        "color": "blue",
    }


def build_desktop_sync_card(
    task: dict[str, str],
    status: str,
) -> dict[str, Any]:
    running = status == "running"
    completed = status == "completed"
    template = "blue" if running else "green" if completed else "grey"
    tag_text = "运行中" if running else "已找到结果" if completed else "暂无结果"
    tag_color = "blue" if running else "green" if completed else "neutral"
    if running:
        title = "等待桌面端完成"
        message = (
            "已接续 Codex Desktop 中的当前 Task。\n\n"
            "Codex 正在运行，完成后结果会自动推送到这里。你不需要重复点击。"
        )
    elif completed:
        title = "桌面结果已接到飞书"
        message = (
            "已读取 Codex Desktop 中当前 Task 的最新结果，并推送到飞书。\n\n"
            "后续消息会继续进入这个 Task。"
        )
    else:
        title = "已接续，暂无新结果"
        message = (
            "已检查 Codex Desktop 中的当前 Task，暂时没有可推送的新结果。\n\n"
            "当前 Task 保持不变，你可以直接在飞书继续发送消息。"
        )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": f"从 Codex Desktop 回到飞书 · {task['title']} · {tag_text}"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": title},
            "subtitle": {
                "tag": "plain_text",
                "content": f"{task['project']} · {task['title']}",
            },
            "template": template,
            "text_tag_list": [
                current_task_tag(),
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": tag_text},
                    "color": tag_color,
                },
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "elements": [{"tag": "markdown", "content": message}],
        },
    }


def build_desktop_sync_confirmation_card(
    task: dict[str, str],
    status: str,
    current_changed: bool = False,
    selected_from_list: bool = False,
) -> dict[str, Any]:
    status_text = (
        "运行中"
        if status == "running"
        else "已有完成结果"
        if status == "completed"
        else "暂无完成结果"
    )
    status_color = (
        "blue"
        if status == "running"
        else "green"
        if status == "completed"
        else "neutral"
    )
    message = (
        "⚠️ **当前 Task 已变化，请重新核对后再接续。**"
        if current_changed
        else "**这是你在桥接中选择的当前 Task。**"
    )
    message += (
        "\n\n接续后，可以查看桌面端的最新结果，并在飞书继续沟通。"
        f"\n\n项目：**{card_markdown_escape(task['project'])}**"
        f"\nTask：**{card_markdown_escape(task['title'])}**"
        f"\n桌面状态：**{status_text}**"
        "\n\n如果不是你要接续的 Task，请先选择「接续其他 Task」。"
    )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": f"从 Codex Desktop 回到飞书 · {task['title']}"},
        },
        "header": {
            "title": {
                "tag": "plain_text",
                "content": "从 Codex Desktop 回到飞书",
            },
            "subtitle": {
                "tag": "plain_text",
                "content": f"{task['project']} · {task['title']}",
            },
            "template": "blue",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                current_task_tag(),
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": status_text},
                    "color": status_color,
                },
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {"tag": "markdown", "content": message},
                {
                    "tag": "button",
                    "text": {
                        "tag": "plain_text",
                        "content": (
                            "接续选定的 Task"
                            if selected_from_list
                            else "接续当前 Task"
                        ),
                    },
                    "type": "primary_filled",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "confirm_desktop_sync",
                                "task_id": str(task["id"]),
                            },
                        }
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "接续其他 Task"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {"action": "show_desktop_sync_task_selector"},
                        }
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "暂不接续"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "cancel_desktop_sync",
                                "task_id": str(task["id"]),
                            },
                        }
                    ],
                },
            ],
        },
    }


def build_desktop_sync_canceled_card(task: dict[str, str] | None) -> dict[str, Any]:
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": "已取消从桌面接续"},
        },
        "header": {
            "title": {
                "tag": "plain_text",
                "content": "已取消从桌面接续",
            },
            "subtitle": {
                "tag": "plain_text",
                "content": (
                    f"{task['project']} · {task['title']}"
                    if task
                    else "没有有效的当前 Task"
                ),
            },
            "template": "grey",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": "已取消"},
                    "color": "neutral",
                }
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": "本次没有接续或订阅 Codex Desktop。当前 Task 保持不变。",
                }
            ],
        },
    }


def reasoning_effort_label(effort: str) -> str:
    labels = {
        "minimal": "极低",
        "low": "低",
        "medium": "中",
        "high": "高",
        "xhigh": "超高",
        "max": "最高",
        "ultra": "极致",
    }
    return f"{labels.get(effort, effort)} · {effort}"


def service_tier_label(service_tier: str) -> str:
    if service_tier == "default":
        return "标准"
    return "快速" if service_tier in {"fast", "priority"} else service_tier


def build_task_settings_card(
    task: dict[str, str],
    settings: dict[str, Any] | None = None,
    status: str = "",
    loading: bool = False,
) -> dict[str, Any]:
    snapshot = settings if isinstance(settings, dict) else {}
    current_model = str(snapshot.get("model") or "").strip()
    current_effort = str(snapshot.get("effort") or "").strip()
    current_service_tier = str(snapshot.get("service_tier") or "default").strip()
    models = [item for item in snapshot.get("models", []) if isinstance(item, dict)]
    current_entry = next(
        (item for item in models if str(item.get("model") or "") == current_model),
        None,
    )
    efforts = list(current_entry.get("efforts", [])) if current_entry else []
    if current_effort and current_effort not in efforts:
        efforts.insert(0, current_effort)
    service_tiers = (
        [item for item in current_entry.get("service_tiers", []) if isinstance(item, dict)]
        if current_entry
        else []
    )
    service_tier_ids = {
        str(item.get("id") or "") for item in service_tiers if item.get("id")
    }
    if current_service_tier not in service_tier_ids | {"default"}:
        current_service_tier = "default"

    lines = [
        "模型、分析强度和速度仅作用于这个 Task，从下一条尚未开始的消息生效。",
        "正在运行的这一轮不会改变；不会改变项目权限、沙箱或授权方式。",
    ]
    if current_model:
        lines.append(
            f"**当前设置**\n模型：`{card_markdown_escape(current_model)}`"
            + (
                f"\n推理强度：`{card_markdown_escape(current_effort)}`"
                if current_effort
                else ""
            )
            + f"\n速度：`{service_tier_label(current_service_tier)}`"
        )
    if status:
        failed_status = any(
            marker in status
            for marker in ("失败", "变化", "运行", "失效", "未接受", "没有")
        )
        lines.insert(
            0,
            f"{'⏳' if loading else '⚠️' if failed_status else '✅'} "
            f"**{card_markdown_escape(status)}**",
        )
    elif not models:
        lines.insert(0, "⚠️ **暂时无法读取 Codex 模型列表，请刷新重试。**")

    elements: list[dict[str, Any]] = [
        {"tag": "markdown", "content": "\n\n".join(lines)}
    ]
    if models:
        model_selector: dict[str, Any] = {
            "tag": "select_static",
            "name": "task_model_selector",
            "placeholder": {"tag": "plain_text", "content": "选择模型"},
            "options": [
                {
                    "text": {
                        "tag": "plain_text",
                        "content": str(item.get("display_name") or item.get("model") or "")[:80],
                    },
                    "value": str(item.get("model") or ""),
                }
                for item in models[:50]
                if str(item.get("model") or "")
            ],
            "width": "fill",
        }
        if any(option["value"] == current_model for option in model_selector["options"]):
            model_selector["initial_option"] = current_model
        elements.append(model_selector)
    if efforts:
        effort_selector: dict[str, Any] = {
            "tag": "select_static",
            "name": "task_effort_selector",
            "placeholder": {"tag": "plain_text", "content": "选择推理强度"},
            "options": [
                {
                    "text": {
                        "tag": "plain_text",
                        "content": reasoning_effort_label(str(effort))[:80],
                    },
                    "value": str(effort),
                }
                for effort in efforts
                if str(effort)
            ],
            "width": "fill",
        }
        if current_effort in efforts:
            effort_selector["initial_option"] = current_effort
        elements.append(effort_selector)
    if current_entry:
        speed_options = [
            {
                "text": {"tag": "plain_text", "content": "标准"},
                "value": "default",
            }
        ] + [
            {
                "text": {
                    "tag": "plain_text",
                    "content": (
                        "快速 · 约 1.5 倍速度，额度消耗更高"
                        if str(item.get("name") or "").lower() == "fast"
                        else str(item.get("name") or item.get("id") or "")[:80]
                    ),
                },
                "value": str(item.get("id") or ""),
            }
            for item in service_tiers
            if str(item.get("id") or "")
        ]
        speed_selector: dict[str, Any] = {
            "tag": "select_static",
            "name": "task_speed_selector",
            "placeholder": {"tag": "plain_text", "content": "选择速度"},
            "options": speed_options,
            "initial_option": current_service_tier,
            "width": "fill",
        }
        elements.append(speed_selector)
        if not service_tiers:
            elements.append(
                {
                    "tag": "markdown",
                    "content": "<font color='grey'>当前模型不支持快速模式。</font>",
                }
            )
    elements.append(
        {
            "tag": "button",
            "text": {"tag": "plain_text", "content": "刷新当前设置"},
            "type": "default",
            "behaviors": [
                {
                    "type": "callback",
                    "value": {
                        "action": "refresh_task_settings",
                        "task_id": str(task["id"]),
                    },
                }
            ],
        }
    )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": f"修改当前 Task 模型：{task['title']}"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": task_title_text(task)},
            "subtitle": {"tag": "plain_text", "content": task_project_text(task)},
            "template": "blue",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                current_task_tag(),
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": "模型设置"},
                    "color": "blue",
                },
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def build_compact_task_context_card(
    task: dict[str, str],
    status: str = "",
    loading: bool = False,
) -> dict[str, Any]:
    lines = [
        "Codex 会总结较早内容并保留关键上下文，让当前 Task 可以继续交流。",
        "此操作不能撤销；Task 正在运行时不能执行。",
    ]
    if status:
        failed_status = any(
            marker in status for marker in ("失败", "变化", "运行", "未接受", "没有")
        )
        lines.insert(
            0,
            f"{'⏳' if loading else '⚠️' if failed_status else '✅'} "
            f"**{card_markdown_escape(status)}**",
        )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": f"压缩当前 Task 上下文：{task['title']}"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": task_title_text(task)},
            "subtitle": {"tag": "plain_text", "content": task_project_text(task)},
            "template": "blue",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                current_task_tag(),
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": "上下文压缩"},
                    "color": "blue",
                },
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {"tag": "markdown", "content": "\n\n".join(lines)},
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "确认压缩当前 Task 上下文…"},
                    "type": "default",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "compact_current_task",
                                "task_id": str(task["id"]),
                            },
                        }
                    ],
                    "confirm": {
                        "title": {"tag": "plain_text", "content": "确认压缩当前 Task 上下文？"},
                        "text": {
                            "tag": "plain_text",
                            "content": "Codex 会总结较早内容。只有确认后才会执行，取消不会改变当前 Task。",
                        },
                    },
                },
            ],
        },
    }


def task_settings_card_task_id(card: dict[str, Any] | None) -> str:
    if not isinstance(card, dict):
        return ""
    for element in card.get("body", {}).get("elements", []):
        if not isinstance(element, dict) or element.get("tag") != "button":
            continue
        for behavior in element.get("behaviors", []):
            value = behavior.get("value") if isinstance(behavior, dict) else None
            if not isinstance(value, dict):
                continue
            if value.get("action") == "refresh_task_settings":
                return str(value.get("task_id") or "")
    return ""


def task_settings_for_current_user(
    user_id: str,
    task_id: str,
    *,
    require_idle: bool = False,
) -> tuple[dict[str, str] | None, str]:
    with _state_lock:
        task = selected_task(user_id, load_state())
    if task is None:
        return None, "当前没有选中的 Task，请先切换 Task"
    if str(task["id"]) != task_id:
        return None, "当前 Task 已变化，请从菜单重新打开对应设置"
    if require_idle and active_run_for_task(task_id) is not None:
        return None, "当前 Task 正在运行，完成或停止后才能压缩上下文"
    return task, ""


def refresh_task_settings_card(
    user_id: str,
    message_id: str,
    task_id: str,
    success_status: str = "",
) -> None:
    task, error = task_settings_for_current_user(user_id, task_id)
    if task is None:
        if message_id:
            fallback = task_by_id(task_id, user_id)
            if fallback is not None:
                patch_card(
                    message_id,
                    build_task_settings_card(fallback, status=error),
                )
        return
    try:
        settings = codex_task_settings(task_id)
    except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
        log(f"task settings read failed error={type(exc).__name__}")
        card = build_task_settings_card(
            task,
            status="读取设置失败，请确认 Codex Desktop 正在运行后重试",
        )
    else:
        card = build_task_settings_card(task, settings, success_status)
    if message_id:
        with _state_lock:
            state = load_state()
            remember_card_context(state, user_id, message_id, card, "task_settings")
        patch_card(message_id, card)


def complete_task_settings_operation(
    user_id: str,
    message_id: str,
    task_id: str,
    *,
    model: str | None = None,
    effort: str | None = None,
    service_tier: str | None = None,
) -> None:
    task, error = task_settings_for_current_user(user_id, task_id)
    if task is None:
        fallback = task_by_id(task_id, user_id)
        if fallback is not None and message_id:
            patch_card(message_id, build_task_settings_card(fallback, status=error))
        return
    try:
        current = codex_task_settings(task_id)
        models = {
            str(item.get("model") or ""): item
            for item in current.get("models", [])
            if isinstance(item, dict)
        }
        if model is not None and model not in models:
            raise ValueError("unknown model")
        current_model = str(current.get("model") or "")
        if effort is not None:
            supported = list(models.get(current_model, {}).get("efforts", []))
            if effort not in supported:
                raise ValueError("unsupported reasoning effort")
        if service_tier is not None:
            supported_tiers = {
                str(item.get("id") or "")
                for item in models.get(current_model, {}).get("service_tiers", [])
                if isinstance(item, dict)
            }
            if service_tier not in supported_tiers | {"default"}:
                raise ValueError("unsupported service tier")
        update_desktop_task_settings(
            task_id,
            model=model,
            effort=effort,
            service_tier=service_tier,
        )
        setting_name = (
            "模型"
            if model is not None
            else "分析强度"
            if effort is not None
            else "速度"
        )
        status = f"{setting_name}已保存，将从下一条尚未开始的消息生效"
    except ValueError:
        status = "所选设置已失效，请刷新后重新选择"
    except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
        log(f"task settings update failed error={type(exc).__name__}")
        status = "Codex Desktop 未接受操作，请稍后重试"
    refresh_task_settings_card(user_id, message_id, task_id, status)


def complete_task_context_compaction(
    user_id: str,
    message_id: str,
    task_id: str,
) -> None:
    task, error = task_settings_for_current_user(user_id, task_id, require_idle=True)
    if task is None:
        fallback = task_by_id(task_id, user_id)
        if fallback is not None and message_id:
            patch_card(message_id, build_compact_task_context_card(fallback, error))
        return
    try:
        compact_desktop_task(task_id)
        status = "当前 Task 上下文压缩已启动"
    except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
        log(f"task context compaction failed error={type(exc).__name__}")
        status = "Codex Desktop 未接受压缩操作，请稍后重试"
    if message_id:
        patch_card(message_id, build_compact_task_context_card(task, status))


def build_run_card(run: dict[str, Any]) -> dict[str, Any]:
    status = str(run.get("status") or "正在准备")
    outcome = str(run.get("outcome") or "running")
    templates = {
        "running": ("blue", "运行中", "blue"),
        "approval": ("yellow", "等待授权", "yellow"),
        "recovering": ("blue", "恢复中", "blue"),
        "desktop_retrying": ("blue", "正在重试", "blue"),
        "desktop_unavailable": ("yellow", "等待选择", "yellow"),
        "completed": ("green", "已完成", "green"),
        "stopped": ("grey", "已停止", "neutral"),
        "failed": ("red", "未完成", "red"),
    }
    template, tag_text, tag_color = templates.get(outcome, templates["running"])
    is_current = run.get("is_current_task") is not False
    role_label = "结果所属 Task" if outcome in {"completed", "stopped", "failed"} else "运行 Task"
    attachment_count = int(run.get("attachment_count") or 0)
    timeline = [
        item
        for item in run.get("timeline", [])
        if isinstance(item, dict) and str(item.get("label") or "").strip()
    ][-4:]
    details: list[str] = []
    if timeline:
        timeline_lines = [
            f"{'●' if item.get('state') == 'active' else '✓'} "
            f"{card_markdown_escape(str(item.get('label') or ''))}"
            for item in timeline
        ]
        details.append("**执行进度**\n" + "\n".join(timeline_lines))
        if status != str(timeline[-1].get("label") or ""):
            details.append(f"**当前阶段**\n{card_markdown_escape(status)}")
    else:
        details.append(f"**当前阶段**\n{card_markdown_escape(status)}")
    details.append(
        f"<font color='grey'>运行时间：{elapsed_text(float(run['started_at']))}</font>"
    )
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
    elif outcome == "desktop_retrying":
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "正在重试 Desktop…"},
                "type": "default",
                "width": "fill",
                "disabled": True,
            }
        )
    elif outcome == "desktop_unavailable" and run.get("fallback_id"):
        fallback_id = str(run["fallback_id"])
        elements.extend(
            [
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "重试 Desktop"},
                    "type": "primary_filled",
                    "width": "fill",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "retry_desktop",
                                "fallback_id": fallback_id,
                            },
                        }
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "使用备用 CLI…"},
                    "type": "default",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "use_cli_fallback",
                                "fallback_id": fallback_id,
                            },
                        }
                    ],
                    "confirm": {
                        "title": {"tag": "plain_text", "content": "使用备用 Codex CLI？"},
                        "text": {
                            "tag": "plain_text",
                            "content": (
                                "运行期间 Codex Desktop 不能实时显示这轮内容，"
                                "完成后重新打开 Task 才能看到。"
                            ),
                        },
                    },
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "取消本条消息"},
                    "type": "default",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "cancel_cli_fallback",
                                "fallback_id": fallback_id,
                            },
                        }
                    ],
                },
            ]
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
                task_role_tag(is_current, role_label),
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
    status: str = "",
    canceled: bool = False,
) -> dict[str, Any]:
    task = entry["task"]
    is_current = entry.get("is_current_task") is not False
    reason = str(entry.get("queue_reason") or "same_task")
    if not status:
        if reason == "global_limit":
            active = int(entry.get("active_run_count") or MAX_CONCURRENT_RUNS)
            maximum = int(entry.get("max_concurrent_runs") or MAX_CONCURRENT_RUNS)
            status = f"全局并发已满（{active}/{maximum}），有空闲位置后自动执行"
        elif reason == "desktop_task_busy":
            status = "Codex Desktop 中的这个 Task 仍在运行，15 秒后自动重试"
        else:
            status = "同一 Task 正在运行，将按发送顺序自动执行"
    attachment_count = len(entry.get("image_keys") or []) + len(
        entry.get("file_keys") or []
    )
    details = [
        f"**当前状态**\n{'已取消排队' if canceled else status}",
    ]
    if not canceled:
        details.append(f"<font color='grey'>本 Task 队列：第 {position} 条</font>")
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
                task_role_tag(is_current, "排队 Task"),
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
    is_current = run.get("is_current_task") is not False
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
                task_role_tag(is_current, "授权所属 Task"),
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
        task_role_tag(
            run.get("is_current_task") is not False,
            "授权所属 Task",
        ),
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
        "机器人菜单 Task 管理 →“当前 Task” —— 查看当前状态卡\n"
        "机器人菜单 Task 管理 →“切换 Task” —— 切换当前 Task 或恢复已归档 Task\n"
        "机器人菜单 Task 管理 →“新建 Task” —— 选择项目并新建 Task\n"
        "机器人菜单 Task 管理 →“归档当前 Task” —— 可取消或二次确认归档当前 Task\n"
        "机器人菜单 桌面task →“订阅桌面 Task” —— 多选需要自动接收 Desktop 新结果的 Task\n"
        "机器人菜单 桌面task →“接续当前 Task” —— 接续桥接中的当前 Task\n"
        "机器人菜单 桌面task →“接续其他 Task” —— 先切换 Task 再接续\n"
        "机器人菜单 模型设置 →“修改当前 Task 模型” —— 设置模型、分析强度和速度\n"
        "机器人菜单 模型设置 →“压缩当前 Task 上下文” —— 确认后总结较早内容\n"
        "机器人菜单 模型设置 →“Codex 额度用量” —— 查看账户额度和 Task 用量分析\n"
        "对话 —— 用文字打开 Task 选择卡片（备用）\n"
        "选择 N —— 文字选择 task（备用）\n"
        "搜索 关键词 —— 按标题搜索当前项目的 Task\n"
        "当前 —— 查看当前 task\n"
        "帮助 —— 显示本说明\n\n"
        "Task 卡可查看全部、最近使用或收藏，并可收藏当前 Task。"
        "选择后，文字、图片、文件和音频会发送到该 Codex task。"
        "Task 运行中继续发送的消息会自动排队。"
        "Codex 明确返回的受支持本机文件会作为飞书附件发送。"
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
    favorite_ids: set[str] | None = None,
    recent_ids: list[str] | None = None,
    task_scope: str = "all",
) -> dict[str, Any]:
    favorites = favorite_ids or set()
    recent = recent_ids or []
    active_scope = task_scope if task_scope in {"all", "recent", "favorites"} else "all"
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
    if not archived and active_scope == "favorites":
        project_tasks = [task for task in project_tasks if task["id"] in favorites]
    elif not archived and active_scope == "recent":
        recent_order = {task_id: index for index, task_id in enumerate(recent)}
        project_tasks = [task for task in project_tasks if task["id"] in recent_order]
        project_tasks.sort(key=lambda task: recent_order[task["id"]])
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
    card_title = "恢复已归档 Task" if archived else "切换 Codex Task"
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
                else "切换后，后续文字会发送到该 Task"
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
            },
            *(
                [
                    {
                        "tag": "text_tag",
                        "text": {"tag": "plain_text", "content": "已收藏"},
                        "color": "yellow",
                    }
                ]
                if not archived and str(selected["id"]) in favorites
                else []
            ),
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
                        else "**切换 Codex Task**\n先选项目，再选 Task；当前 Task 会持续保留，直到你选择新的 Task。"
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
            "content": "选择一个已归档 Task" if archived else "切换到一个 Task",
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
        if not archived:
            elements.append(
                {
                    "tag": "select_static",
                    "name": "task_scope_selector",
                    "placeholder": {"tag": "plain_text", "content": "显示范围"},
                    "options": [
                        {
                            "text": {"tag": "plain_text", "content": label},
                            "value": value,
                        }
                        for value, label in (
                            ("all", "全部 Task"),
                            ("recent", "最近使用"),
                            ("favorites", "我的收藏"),
                        )
                    ],
                    "initial_option": active_scope,
                    "width": "fill",
                }
            )
        if visible_tasks:
            elements.append(selector)
        else:
            elements.append(
                {
                    "tag": "markdown",
                    "content": (
                        "当前项目还没有收藏的 Task。"
                        if active_scope == "favorites"
                        else "当前项目还没有最近使用的 Task。"
                        if active_scope == "recent"
                        else "当前项目没有匹配的 Task。请清除搜索或切换项目。"
                    ),
                }
            )
        page_label = f"第 {active_page + 1}/{page_count} 页 · {len(project_tasks)} 个 Task"
        elements.append({"tag": "markdown", "content": f"<font color='grey'>{page_label}</font>"})
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "刷新 Task 列表"},
                "type": "default",
                "behaviors": [
                    {
                        "type": "callback",
                        "value": {
                            "action": (
                                "refresh_archived_tasks"
                                if archived
                                else "refresh_task_list"
                            )
                        },
                    }
                ],
            }
        )
        elements.append(
            {
                "tag": "markdown",
                "content": (
                    "<font color='grey'>"
                    f"最后刷新：{time.strftime('%H:%M:%S', time.localtime())}"
                    "</font>"
                ),
            }
        )
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
    if selected and not archived:
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "取消切换"},
                "type": "default",
                "width": "fill",
                "behaviors": [
                    {
                        "type": "callback",
                        "value": {
                            "action": "cancel_task_switch",
                            "task_id": str(selected["id"]),
                        },
                    }
                ],
            }
        )
        elements.append(
            {
                "tag": "button",
                "text": {
                    "tag": "plain_text",
                    "content": (
                        "取消收藏当前 Task"
                        if str(selected["id"]) in favorites
                        else "收藏当前 Task"
                    ),
                },
                "type": "default",
                "width": "fill",
                "behaviors": [
                    {
                        "type": "callback",
                        "value": {
                            "action": "toggle_task_favorite",
                            "task_id": str(selected["id"]),
                        },
                    }
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


def build_task_subscriptions_card(
    tasks: list[dict[str, str]],
    subscriptions: dict[str, dict[str, Any]],
    selected_id: str | None = None,
    project_filter: str | None = None,
    page: int = 0,
    change: str = "",
) -> dict[str, Any]:
    subscribed_ids = set(subscriptions)
    selected = next((task for task in tasks if task["id"] == selected_id), None)
    projects = list(dict.fromkeys(task["project"] for task in tasks))
    active_project = (
        project_filter
        if project_filter in projects
        else selected["project"] if selected else projects[0] if projects else None
    )
    project_tasks = [task for task in tasks if task["project"] == active_project]
    page_count = max(1, (len(project_tasks) + TASKS_PER_PAGE - 1) // TASKS_PER_PAGE)
    active_page = min(max(page, 0), page_count - 1)
    start = active_page * TASKS_PER_PAGE
    visible_tasks = project_tasks[start : start + TASKS_PER_PAGE]
    if selected not in visible_tasks:
        selected = visible_tasks[0] if visible_tasks else None
    subscribed_tasks = [task for task in tasks if task["id"] in subscribed_ids]
    elements: list[dict[str, Any]] = [
        {
            "tag": "markdown",
            "content": (
                (f"✅ **{card_markdown_escape(change)}**\n\n" if change else "")
                + "订阅后，这个 Task 在 Codex Desktop 完成新的运行时，结果会自动推送到你的飞书。"
                "订阅不会改变当前 Task，也不会补发订阅前已经完成的结果。"
            ),
        }
    ]
    if projects:
        elements.append(
            {
                "tag": "select_static",
                "name": "subscription_project_selector",
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
        )
    if visible_tasks:
        selector: dict[str, Any] = {
            "tag": "select_static",
            "name": "subscription_task_selector",
            "placeholder": {"tag": "plain_text", "content": "选择一个 Task"},
            "options": [
                {
                    "text": {"tag": "plain_text", "content": option_text(task)},
                    "value": task["id"],
                }
                for task in visible_tasks
            ],
            "initial_option": selected["id"] if selected else visible_tasks[0]["id"],
            "width": "fill",
        }
        elements.append(selector)
        elements.append(
            {
                "tag": "button",
                "text": {
                    "tag": "plain_text",
                    "content": (
                        "取消订阅这个 Task"
                        if selected and selected["id"] in subscribed_ids
                        else "订阅这个 Task"
                    ),
                },
                "type": (
                    "default"
                    if selected and selected["id"] in subscribed_ids
                    else "primary_filled"
                ),
                "width": "fill",
                "behaviors": [
                    {
                        "type": "callback",
                        "value": {
                            "action": "toggle_task_subscription",
                            "task_id": selected["id"] if selected else "",
                        },
                    }
                ],
            }
        )
    else:
        elements.append(
            {"tag": "markdown", "content": "当前项目没有可订阅的 Task。"}
        )
    elements.append(
        {
            "tag": "markdown",
            "content": f"<font color='grey'>第 {active_page + 1}/{page_count} 页 · {len(project_tasks)} 个 Task</font>",
        }
    )
    if active_page > 0:
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "上一页"},
                "type": "default",
                "behaviors": [
                    {
                        "type": "callback",
                        "value": {"action": "task_subscription_page", "page": active_page - 1},
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
                        "value": {"action": "task_subscription_page", "page": active_page + 1},
                    }
                ],
            }
        )
    elements.append(
        {
            "tag": "button",
            "text": {"tag": "plain_text", "content": "刷新订阅列表"},
            "type": "default",
            "behaviors": [
                {"type": "callback", "value": {"action": "show_task_subscriptions"}}
            ],
        }
    )
    subscribed_text = (
        "\n".join(
            f"- ✅ {card_markdown_escape(option_text(task))}"
            for task in subscribed_tasks
        )
        if subscribed_tasks
        else "尚未订阅任何 Task。"
    )
    elements.append(
        {
            "tag": "markdown",
            "content": f"**已订阅 {len(subscribed_tasks)} 个 Task**\n{subscribed_text}",
        }
    )
    if subscribed_tasks:
        elements.append(
            {
                "tag": "button",
                "text": {"tag": "plain_text", "content": "取消全部订阅…"},
                "type": "default",
                "width": "fill",
                "behaviors": [
                    {"type": "callback", "value": {"action": "clear_task_subscriptions"}}
                ],
                "confirm": {
                    "title": {"tag": "plain_text", "content": "取消全部 Task 订阅？"},
                    "text": {"tag": "plain_text", "content": "之后不会再自动推送这些 Task 的桌面结果。"},
                },
            }
        )
    return {
        "schema": "2.0",
        "config": {"update_multi": True, "width_mode": "default", "enable_forward": False},
        "header": {
            "title": {"tag": "plain_text", "content": "订阅桌面 Task"},
            "subtitle": {
                "tag": "plain_text",
                "content": f"已订阅 {len(subscribed_tasks)}/{MAX_TASK_SUBSCRIPTIONS_PER_USER} 个",
            },
            "template": "blue",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": "自动推送"},
                    "color": "blue",
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


def promlight_button(
    text: str,
    action: str,
    *,
    lamp_id: str = "",
    task_id: str = "",
    style: str = "default",
    confirm: tuple[str, str] | None = None,
) -> dict[str, Any]:
    value = {"action": action}
    if lamp_id:
        value["lamp_id"] = lamp_id
    if task_id:
        value["task_id"] = task_id
    button: dict[str, Any] = {
        "tag": "button",
        "text": {"tag": "plain_text", "content": text},
        "type": style,
        "behaviors": [{"type": "callback", "value": value}],
    }
    if confirm is not None:
        button["confirm"] = {
            "title": {"tag": "plain_text", "content": confirm[0]},
            "text": {"tag": "plain_text", "content": confirm[1]},
        }
    return button


def promlight_action_processing_card(
    card: dict[str, Any],
    action: str,
) -> dict[str, Any]:
    processing_card = json.loads(json.dumps(card, ensure_ascii=False))
    elements = processing_card.get("body", {}).get("elements", [])
    if not isinstance(elements, list):
        return processing_card
    for element in elements:
        if not isinstance(element, dict) or element.get("tag") != "button":
            continue
        callbacks = element.get("behaviors")
        if not isinstance(callbacks, list):
            continue
        if not any(
            isinstance(callback, dict)
            and callback.get("type") == "callback"
            and isinstance(callback.get("value"), dict)
            and callback["value"].get("action") == action
            for callback in callbacks
        ):
            continue
        element["text"] = {"tag": "plain_text", "content": "正在处理…"}
        element["type"] = "default"
        element["disabled"] = True
        element.pop("behaviors", None)
        element.pop("confirm", None)
        break
    return processing_card


def promlight_legend_element() -> dict[str, Any]:
    return {
        "tag": "markdown",
        "content": (
            "---\n**灯光对应的事件说明**\n"
            + PROMLIGHT_LEGEND_TEXT
            + "\n\n<font color='grey'>多 Task：红灯闪烁 > 黄灯闪烁 > 黄灯常亮 > 绿灯常亮。"
            "仅计算你为这盏灯显式关注的 Task。</font>"
        ),
    }


def build_promlight_legend_card() -> dict[str, Any]:
    return {
        "schema": "2.0",
        "config": {"update_multi": True, "width_mode": "default", "enable_forward": False},
        "header": {
            "title": {"tag": "plain_text", "content": "灯光状态说明"},
            "subtitle": {"tag": "plain_text", "content": "PromLight · 按关注 Task 聚合"},
            "template": "blue",
            "icon": {"tag": "standard_icon", "token": "lightbulb_colorful"},
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": (
                        "**完整说明**\n"
                        + PROMLIGHT_LEGEND_TEXT
                        + "\n\n**多 Task 聚合优先级**\n"
                        "红灯闪烁 > 黄灯闪烁 > 黄灯常亮 > 绿灯常亮\n\n"
                        "只有你显式关注的 Task 会影响提示灯。灯离线时，卡片会明确显示“提示灯离线”，"
                        "并保留最后逻辑状态；命令 ACK 不等于灯效已独立回读验证。"
                    ),
                },
                promlight_button("返回我的提示灯", "show_promlight", style="primary_filled"),
            ],
        },
    }


def promlight_status_label(status: str) -> str:
    return {
        "idle": "绿灯常亮 · 已完成/空闲",
        "running": "黄灯常亮 · 正在处理中",
        "human_gate": "黄灯闪烁 · 需要你处理",
        "error": "红灯闪烁 · 执行出错",
    }.get(status, "状态待确认")


def promlight_updated_text(value: Any) -> str:
    try:
        timestamp = float(value)
    except (TypeError, ValueError):
        return "尚未更新"
    return datetime.fromtimestamp(timestamp).astimezone().strftime("%m-%d %H:%M:%S")


def build_promlight_control_card(user_id: str, state: dict[str, Any]) -> dict[str, Any]:
    lamps = user_promlight_lamps(state, user_id)
    elements: list[dict[str, Any]] = [
        {
            "tag": "markdown",
            "content": (
                "在共享 Mac 上由 Bridge 驱动提示灯；飞书用于身份、配置和状态查看。"
                "每盏灯只受它自己显式关注的 Task 影响。"
            ),
        }
    ]
    if not lamps:
        elements.append(
            {
                "tag": "markdown",
                "content": "**我的提示灯**\n尚未绑定提示灯。请先选择一种连接方式。",
            }
        )
    for lamp in lamps:
        lamp_id = str(lamp.get("lamp_id") or "")
        online = lamp.get("online") is True
        status = str(lamp.get("last_logical_status") or "idle")
        task_names: list[str] = []
        for task_id in lamp.get("task_ids", []):
            task = task_by_id(str(task_id), user_id)
            if task is not None:
                task_names.append(option_text(task))
        delivery = str(lamp.get("last_delivery") or "not_sent")
        delivery_text = (
            "设备已 ACK，灯效未独立回读"
            if delivery == "acknowledged"
            else "投递结果未知"
            if delivery == "unknown"
            else "尚未下发"
        )
        pending_unbind = bool(lamp.get("pending_unbind"))
        elements.append(
            {
                "tag": "markdown",
                "content": (
                    f"**{card_markdown_escape(str(lamp.get('name') or 'PromLight'))}**"
                    + (" · 默认灯" if lamp.get("is_default") else "")
                    + f"\n{'在线' if online else '提示灯离线'} · 当前中继：本地 Bridge"
                    + f"\n最后逻辑状态：{promlight_status_label(status)}"
                    + f"\n状态更新时间：{promlight_updated_text(lamp.get('updated_at'))}"
                    + f"\n投递语义：{delivery_text}"
                    + ("\n解绑状态：解绑待收口；灯恢复在线并确认熄除任务灯效后自动完成" if pending_unbind else "")
                    + "\n关注 Task："
                    + ("、".join(card_markdown_escape(name) for name in task_names) if task_names else "未关注")
                ),
            }
        )
        if not pending_unbind:
            elements.append(
                promlight_button(
                    "管理关注 Task",
                    "promlight_manage_tasks",
                    lamp_id=lamp_id,
                    style="primary_filled",
                )
            )
        if not lamp.get("is_default"):
            elements.append(promlight_button("设为默认灯", "promlight_set_default", lamp_id=lamp_id))
        elements.append(promlight_button("重命名", "promlight_start_rename", lamp_id=lamp_id))
        if not pending_unbind:
            elements.append(
                promlight_button(
                    "解绑…",
                    "promlight_unbind",
                    lamp_id=lamp_id,
                    style="danger",
                    confirm=("确认解绑这盏提示灯？", "解绑后会清除这盏灯的 Task 白名单，并停止后续驱动。"),
                )
            )
    elements.extend(
        [
            promlight_button("在本地 Bridge 连接新灯", "promlight_local_pairing"),
            promlight_button("连接附近提示灯（手机/Pad）", "promlight_mobile_pairing"),
            promlight_button("刷新状态", "promlight_refresh"),
            promlight_legend_element(),
        ]
    )
    return {
        "schema": "2.0",
        "config": {"update_multi": True, "width_mode": "default", "enable_forward": False},
        "header": {
            "title": {"tag": "plain_text", "content": "提示灯控制中心"},
            "subtitle": {"tag": "plain_text", "content": f"我的提示灯 · {len(lamps)} 盏"},
            "template": "blue",
            "icon": {"tag": "standard_icon", "token": "lightbulb_colorful"},
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def build_promlight_local_pairing_card() -> dict[str, Any]:
    return {
        "schema": "2.0",
        "config": {"update_multi": True, "width_mode": "default", "enable_forward": False},
        "header": {
            "title": {"tag": "plain_text", "content": "在本地 Bridge 连接新灯"},
            "template": "blue",
        },
        "body": {
            "direction": "vertical",
            "vertical_spacing": "12px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": (
                        "1. 确保 PromLight 已与身边的 Mac/Windows 建立 BLE 连接。\n"
                        "2. 在运行 Codex 的 Mac 上打开 DeepOri Codex Feishu Bridge。\n"
                        "3. 进入“提示灯”区域，刷新设备并把灯归属给一个已授权飞书用户。\n\n"
                        "设备 reference 只保存在该 Mac 的私有 state 中，不会显示在飞书或提交到仓库。"
                    ),
                },
                promlight_button("返回我的提示灯", "show_promlight", style="primary_filled"),
                promlight_legend_element(),
            ],
        },
    }


def build_promlight_mobile_pairing_card() -> dict[str, Any]:
    return {
        "schema": "2.0",
        "config": {"update_multi": True, "width_mode": "default", "enable_forward": False},
        "header": {
            "title": {"tag": "plain_text", "content": "连接附近提示灯（手机/Pad）"},
            "template": "yellow",
        },
        "body": {
            "direction": "vertical",
            "vertical_spacing": "12px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": (
                        "**移动配对暂未开放。**\n\n"
                        "飞书小程序具备前台 BLE 扫描、连接和 GATT 读写能力，但后台约 5 分钟后可能被销毁，"
                        "不能承担持续提醒。PromLight v2 的真实 GATT service、characteristic、分包和回读协议"
                        "也尚未完成授权真机验证。\n\n"
                        "当前请使用本地 Bridge 路径。这里没有执行扫描或伪装成已连接。"
                    ),
                },
                promlight_button("查看本地连接方法", "promlight_local_pairing"),
                promlight_button("返回我的提示灯", "show_promlight", style="primary_filled"),
                promlight_legend_element(),
            ],
        },
    }


def build_promlight_task_card(
    user_id: str,
    lamp_id: str,
    state: dict[str, Any],
    change: str = "",
) -> dict[str, Any]:
    namespace = promlight_state(state)
    lamp = owned_promlight_lamp(state, user_id, lamp_id)
    tasks = recent_tasks(user_id)
    valid_ids = {task["id"] for task in tasks}
    lamp["task_ids"] = [task_id for task_id in lamp.get("task_ids", []) if task_id in valid_ids]
    projects = list(dict.fromkeys(task["project"] for task in tasks))
    active_project = str(namespace["selected_projects"].get(user_id) or "")
    if active_project not in projects:
        active_project = projects[0] if projects else ""
    project_tasks = [task for task in tasks if task["project"] == active_project]
    selected_id = str(namespace["selected_tasks"].get(user_id) or "")
    if selected_id not in {task["id"] for task in project_tasks}:
        selected_id = project_tasks[0]["id"] if project_tasks else ""
    namespace["selected_lamps"][user_id] = lamp_id
    if active_project:
        namespace["selected_projects"][user_id] = active_project
    if selected_id:
        namespace["selected_tasks"][user_id] = selected_id
    save_state(state)
    selected = next((task for task in project_tasks if task["id"] == selected_id), None)
    elements: list[dict[str, Any]] = [
        {
            "tag": "markdown",
            "content": (
                (f"✅ **{card_markdown_escape(change)}**\n\n" if change else "")
                + f"正在管理：**{card_markdown_escape(str(lamp.get('name') or 'PromLight'))}**\n"
                "只有这里显式关注的 Task 会影响这盏灯。保存时会再次校验项目权限和归档状态。"
            ),
        }
    ]
    if projects:
        elements.append(
            {
                "tag": "select_static",
                "name": "promlight_project_selector",
                "placeholder": {"tag": "plain_text", "content": "选择项目"},
                "options": [
                    {"text": {"tag": "plain_text", "content": project}, "value": project}
                    for project in projects
                ],
                "initial_option": active_project,
                "width": "fill",
            }
        )
    if project_tasks:
        elements.append(
            {
                "tag": "select_static",
                "name": "promlight_task_selector",
                "placeholder": {"tag": "plain_text", "content": "选择 Task"},
                "options": [
                    {
                        "text": {"tag": "plain_text", "content": option_text(task)},
                        "value": task["id"],
                    }
                    for task in project_tasks[:TASKS_PER_PAGE]
                ],
                "initial_option": selected_id,
                "width": "fill",
            }
        )
        elements.append(
            promlight_button(
                "取消关注这个 Task"
                if selected_id in lamp.get("task_ids", [])
                else "关注这个 Task",
                "promlight_toggle_task",
                lamp_id=lamp_id,
                task_id=selected_id,
                style="default" if selected_id in lamp.get("task_ids", []) else "primary_filled",
            )
        )
    else:
        elements.append({"tag": "markdown", "content": "当前项目没有可关注的 Task。"})
    subscribed = [task for task in tasks if task["id"] in lamp.get("task_ids", [])]
    elements.append(
        {
            "tag": "markdown",
            "content": (
                f"**已关注 {len(subscribed)} 个 Task**\n"
                + (
                    "\n".join(f"- {card_markdown_escape(option_text(task))}" for task in subscribed)
                    if subscribed
                    else "尚未关注任何 Task。"
                )
            ),
        }
    )
    elements.extend(
        [
            promlight_button("返回我的提示灯", "show_promlight"),
            promlight_legend_element(),
        ]
    )
    return {
        "schema": "2.0",
        "config": {"update_multi": True, "width_mode": "default", "enable_forward": False},
        "header": {
            "title": {"tag": "plain_text", "content": "关注 Task"},
            "subtitle": {"tag": "plain_text", "content": str(lamp.get("name") or "PromLight")},
            "template": "blue",
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": elements,
        },
    }


def build_promlight_rename_card(lamp: dict[str, Any]) -> dict[str, Any]:
    return {
        "schema": "2.0",
        "config": {"update_multi": True, "width_mode": "default", "enable_forward": False},
        "header": {
            "title": {"tag": "plain_text", "content": "重命名提示灯"},
            "template": "blue",
        },
        "body": {
            "direction": "vertical",
            "vertical_spacing": "12px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": (
                        f"当前名称：**{card_markdown_escape(str(lamp.get('name') or 'PromLight'))}**\n\n"
                        "请直接发送一条纯文字作为新名称（最多 40 个字符）。这条消息只用于重命名，不会发送到 Codex Task。"
                    ),
                },
                promlight_button("取消重命名", "promlight_cancel_rename"),
                promlight_legend_element(),
            ],
        },
    }


def build_task_subscription_result_card(
    task: dict[str, str],
    status: str,
) -> dict[str, Any]:
    completed = status == "completed"
    return {
        "schema": "2.0",
        "config": {"update_multi": True, "width_mode": "default", "enable_forward": False},
        "header": {
            "title": {"tag": "plain_text", "content": task_title_text(task)},
            "subtitle": {"tag": "plain_text", "content": task_project_text(task)},
            "template": "green" if completed else "grey",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": "订阅结果"},
                    "color": "blue",
                },
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": "已完成" if completed else "未完成"},
                    "color": "green" if completed else "neutral",
                },
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px",
            "vertical_spacing": "4px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": (
                        "<font color='grey'>完整结果见下方，可直接继续对话。</font>"
                        if completed
                        else "<font color='grey'>运行详情见下方。</font>"
                    ),
                },
            ],
        },
    }


def build_task_switch_canceled_card(task: dict[str, str]) -> dict[str, Any]:
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": f"已取消切换 · {task['title']}"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": task_title_text(task)},
            "subtitle": {"tag": "plain_text", "content": task_project_text(task)},
            "template": "green",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [current_task_tag()],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {
                    "tag": "markdown",
                    "content": (
                        "✅ **已取消切换**\n\n"
                        "当前 Task 保持不变，后续消息仍会发送到这里。"
                    ),
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "重新切换 Task"},
                    "type": "default",
                    "width": "fill",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "show_task_selector"}}
                    ],
                },
            ],
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
    processing: str = "",
) -> dict[str, Any]:
    if task is None:
        status = "尚未选择 Task。请先点击机器人菜单中的“切换 Task”。"
    elif processing:
        verb = "恢复" if processing == "restore" else "归档"
        status = f"正在{verb}：**{card_markdown_escape(option_text(task))}**\n\n请稍候，完成后会自动更新。"
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
    if task is not None and not busy and not archived and not canceled and not processing:
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
                    "text": {"tag": "plain_text", "content": "切换到其他 Task"},
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
                    "text": {"tag": "plain_text", "content": "切换到其他 Task"},
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
    if archived or canceled or restored or busy or processing:
        header_tags.append(
            {
                "tag": "text_tag",
                "text": {
                    "tag": "plain_text",
                    "content": (
                        "处理中"
                        if processing
                        else "已恢复"
                        if restored
                        else "已归档"
                        if archived
                        else "已取消"
                        if canceled
                        else "运行中"
                    ),
                },
                "color": (
                    "blue"
                    if processing
                    else "green"
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
                "blue"
                if processing
                else "green"
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
    if names & {"task_model_selector", "task_effort_selector", "task_speed_selector"}:
        return "task_settings"
    if names & {"subscription_project_selector", "subscription_task_selector"}:
        return "task_subscriptions"
    if names & {"promlight_project_selector", "promlight_task_selector"}:
        return "promlight_tasks"
    if names & {"archived_project_selector", "archived_task_selector"}:
        return "archived_tasks"
    if names & {"project_selector", "task_selector"}:
        return "tasks"
    title = str(card.get("header", {}).get("title", {}).get("content") or "")
    return "archive_task" if title == "归档 Codex Task" else ""


def card_selector_context(card: dict[str, Any]) -> dict[str, Any]:
    selectors: list[dict[str, Any]] = []
    for element in card.get("body", {}).get("elements", []):
        if not isinstance(element, dict) or element.get("tag") != "select_static":
            continue
        selectors.append(
            {
                "name": str(element.get("name") or ""),
                "initial_option": str(element.get("initial_option") or ""),
                "options": [
                    str(option.get("value") or "")
                    for option in element.get("options", [])
                    if isinstance(option, dict)
                ],
            }
        )
    return {"selectors": selectors}


def card_active_project(card: dict[str, Any] | None, archived: bool = False) -> str:
    if not isinstance(card, dict):
        return ""
    expected_name = "archived_project_selector" if archived else "project_selector"
    for selector in card_selector_context(card)["selectors"]:
        if selector["name"] == expected_name:
            return str(selector["initial_option"] or "")
    return ""


def subscription_card_active_project(card: dict[str, Any] | None) -> str:
    if not isinstance(card, dict):
        return ""
    for selector in card_selector_context(card)["selectors"]:
        if selector["name"] == "subscription_project_selector":
            return str(selector["initial_option"] or "")
    return ""


def promlight_card_active_project(card: dict[str, Any] | None) -> str:
    if not isinstance(card, dict):
        return ""
    for selector in card_selector_context(card)["selectors"]:
        if selector["name"] == "promlight_project_selector":
            return str(selector["initial_option"] or "")
    return ""


def promlight_card_lamp_id(card: dict[str, Any] | None) -> str:
    if not isinstance(card, dict):
        return ""
    for element in card.get("body", {}).get("elements", []):
        if not isinstance(element, dict) or element.get("tag") != "button":
            continue
        for behavior in element.get("behaviors", []):
            if not isinstance(behavior, dict):
                continue
            value = behavior.get("value")
            if isinstance(value, dict) and str(value.get("lamp_id") or ""):
                return str(value["lamp_id"])
    return ""


def task_card_with_notice(card: dict[str, Any], notice: str) -> dict[str, Any]:
    elements = card.get("body", {}).get("elements", [])
    if isinstance(elements, list):
        elements.insert(
            0,
            {
                "tag": "markdown",
                "content": f"⚠️ **{card_markdown_escape(notice)}**",
            },
        )
    return card


def remember_card_context(
    state: dict[str, Any],
    user_id: str,
    message_id: str,
    card: dict[str, Any],
    context_type_override: str = "",
) -> None:
    context_type = context_type_override or card_context_type(card)
    if not message_id.startswith("om_") or not context_type:
        return
    contexts = state.setdefault("card_contexts", {})
    if not isinstance(contexts, dict):
        contexts = {}
    previous = contexts.get(message_id)
    revision = (
        int(previous.get("revision") or 0) + 1
        if isinstance(previous, dict)
        else 1
    )
    contexts.pop(message_id, None)
    contexts[message_id] = {
        "user_id": user_id,
        "type": context_type,
        "revision": revision,
        "task_id": (
            task_settings_card_task_id(card)
            if context_type == "task_settings"
            else ""
        ),
        "lamp_id": promlight_card_lamp_id(card) if context_type == "promlight_tasks" else "",
        "project": (
            promlight_card_active_project(card)
            if context_type == "promlight_tasks"
            else subscription_card_active_project(card)
            if context_type == "task_subscriptions"
            else card_active_project(card, context_type == "archived_tasks")
        ),
        "selector_context": card_selector_context(card),
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


def card_context_details(
    state: dict[str, Any],
    user_id: str,
    message_id: str,
) -> dict[str, Any]:
    context = state.get("card_contexts", {}).get(message_id)
    if not isinstance(context, dict) or context.get("user_id") != user_id:
        return {}
    return dict(context)


def send_menu_card(
    user_id: str,
    state: dict[str, Any],
    card: dict[str, Any],
    kind: str,
    context_type_override: str = "",
) -> bool:
    success, chat_id, message_id = send_card(
        user_id,
        card,
        kind,
    )
    if not success:
        queue_pending_menu_card(
            user_id,
            card,
            kind,
            "飞书卡片发送超时或网络失败",
        )
        return False
    with _state_lock:
        state = load_state()
        if chat_id:
            authorize_chat(state, user_id, chat_id)
        if message_id:
            remember_card_context(
                state,
                user_id,
                message_id,
                card,
                context_type_override,
            )
    return True


def desktop_result_subscriptions(state: dict[str, Any]) -> dict[str, dict[str, Any]]:
    subscriptions = state.setdefault("desktop_result_subscriptions", {})
    if not isinstance(subscriptions, dict):
        subscriptions = {}
        state["desktop_result_subscriptions"] = subscriptions
    return subscriptions


def recoverable_runs(state: dict[str, Any]) -> dict[str, dict[str, Any]]:
    runs = state.setdefault("recoverable_runs", {})
    if not isinstance(runs, dict):
        runs = {}
        state["recoverable_runs"] = runs
    return runs


def task_subscriptions(state: dict[str, Any]) -> dict[str, dict[str, dict[str, Any]]]:
    subscriptions = state.setdefault("task_subscriptions", {})
    if not isinstance(subscriptions, dict):
        subscriptions = {}
        state["task_subscriptions"] = subscriptions
    return subscriptions


def user_task_subscriptions(
    state: dict[str, Any],
    user_id: str,
) -> dict[str, dict[str, Any]]:
    subscriptions = task_subscriptions(state)
    user_subscriptions = subscriptions.setdefault(user_id, {})
    if not isinstance(user_subscriptions, dict):
        user_subscriptions = {}
        subscriptions[user_id] = user_subscriptions
    return user_subscriptions


def new_task_subscription(task: dict[str, str]) -> dict[str, Any]:
    snapshot = latest_rollout_turn(rollout_path_for_task(str(task["id"])))
    running = snapshot.get("status") == "running"
    return {
        "task_id": str(task["id"]),
        "cursor_offset": int(snapshot.get("cursor_offset") or 0),
        "active_turn_id": str(snapshot.get("turn_id") or "") if running else "",
        "images": list(snapshot.get("images") or []) if running else [],
        "created_at": time.time(),
    }


def task_subscriptions_card_for_state(
    user_id: str,
    state: dict[str, Any],
    change: str = "",
) -> dict[str, Any]:
    with _state_lock:
        tasks = recent_tasks(user_id)
        subscriptions = user_task_subscriptions(state, user_id)
        valid_ids = {task["id"] for task in tasks}
        for task_id in list(subscriptions):
            if task_id not in valid_ids:
                subscriptions.pop(task_id, None)
        selected_map = state.setdefault("subscription_selected_tasks", {})
        selected_id = str(selected_map.get(user_id) or "")
        if selected_id not in valid_ids:
            selected_id = ""
        project_map = state.setdefault("subscription_last_projects", {})
        project_filter = str(project_map.get(user_id) or "")
        projects = list(dict.fromkeys(task["project"] for task in tasks))
        if project_filter not in projects:
            project_filter = (
                next(
                    (task["project"] for task in tasks if task["id"] == selected_id),
                    "",
                )
                or (projects[0] if projects else "")
            )
            if project_filter:
                project_map[user_id] = project_filter
        project_tasks = [task for task in tasks if task["project"] == project_filter]
        if selected_id not in {task["id"] for task in project_tasks}:
            selected_id = project_tasks[0]["id"] if project_tasks else ""
            if selected_id:
                selected_map[user_id] = selected_id
        page = int(state.setdefault("subscription_task_pages", {}).get(user_id) or 0)
        save_state(state)
        return build_task_subscriptions_card(
            tasks,
            subscriptions,
            selected_id,
            project_filter,
            page,
            change,
        )


def toggle_task_subscription(
    user_id: str,
    task: dict[str, str],
) -> tuple[bool, str]:
    with _state_lock:
        state = load_state()
        subscriptions = user_task_subscriptions(state, user_id)
        task_id = str(task["id"])
        if task_id in subscriptions:
            subscriptions.pop(task_id, None)
            save_state(state)
            return False, f"已取消订阅：{option_text(task)}"
        if len(subscriptions) >= MAX_TASK_SUBSCRIPTIONS_PER_USER:
            return False, f"每位用户最多订阅 {MAX_TASK_SUBSCRIPTIONS_PER_USER} 个 Task。"
    entry = new_task_subscription(task)
    with _state_lock:
        state = load_state()
        subscriptions = user_task_subscriptions(state, user_id)
        if len(subscriptions) >= MAX_TASK_SUBSCRIPTIONS_PER_USER:
            return False, f"每位用户最多订阅 {MAX_TASK_SUBSCRIPTIONS_PER_USER} 个 Task。"
        subscriptions[str(task["id"])] = entry
        save_state(state)
    return True, f"已订阅：{option_text(task)}"


def deliver_task_subscription_result(
    user_id: str,
    task: dict[str, str],
    snapshot: dict[str, Any],
) -> bool:
    result = str(snapshot.get("message") or "").strip()
    allowed_roots = result_roots_for_task(task)
    clean_result, audio_files = prepare_result_audio(result, allowed_roots)
    clean_result, images = prepare_result_images(
        clean_result,
        [str(item) for item in snapshot.get("images", [])],
        allowed_roots,
    )
    clean_result, files = prepare_result_files(clean_result, allowed_roots)
    card = build_task_subscription_result_card(
        task,
        str(snapshot.get("status") or "failed"),
    )
    kind = (
        "task-subscription-result-"
        f"{user_id}-{task['id']}-{str(snapshot.get('turn_id') or '')}"
    )
    delivered, chat_id, message_id = send_card(user_id, card, kind)
    if not delivered or not message_id:
        return False
    with _state_lock:
        state = load_state()
        if chat_id:
            authorize_chat(state, user_id, chat_id)
    record_task_exchange(
        user_id,
        str(task["id"]),
        answer=clean_result,
        completed_at=time.time(),
    )
    with result_delivery_lock(user_id):
        followed = follow_result_task(user_id, task)
        reply_complete_result(
            message_id,
            task_status_prefix(
                task,
                "订阅结果已完成"
                if str(snapshot.get("status") or "") == "completed"
                else "订阅结果未完成",
            )
            + clean_result,
            f"final-task-subscription-{str(snapshot.get('turn_id') or 'unknown')}",
        )
    if followed:
        schedule_user_task_identity_refresh(
            user_id,
            "当前 Task 已跟随订阅结果",
            task,
        )
    deliver_result_resources(message_id, images, audio_files, files)
    update_current_status_card(user_id, task=task)
    log(
        "task subscription result delivered "
        f"status={snapshot.get('status')} images={len(images)} "
        f"audio={len(audio_files)} files={len(files)}"
    )
    return True


def update_task_subscription_checkpoint(
    user_id: str,
    task_id: str,
    cursor_offset: int,
    active_turn_id: str,
    images: list[str],
) -> None:
    with _state_lock:
        state = load_state()
        entry = user_task_subscriptions(state, user_id).get(task_id)
        if not isinstance(entry, dict):
            return
        next_cursor = max(0, int(cursor_offset))
        next_images = list(images)
        if (
            int(entry.get("cursor_offset") or 0) == next_cursor
            and str(entry.get("active_turn_id") or "") == active_turn_id
            and [str(item) for item in entry.get("images", [])] == next_images
        ):
            return
        entry["cursor_offset"] = next_cursor
        entry["active_turn_id"] = active_turn_id
        entry["images"] = next_images
        save_state(state)


def poll_task_subscriptions() -> bool:
    with _state_lock:
        state = load_state()
        subscriptions = task_subscriptions(state)
        removed_users = [
            user_id for user_id in subscriptions if not authorized_user(user_id)
        ]
        for user_id in removed_users:
            subscriptions.pop(user_id, None)
        if removed_users:
            save_state(state)
        pending = [
            (user_id, task_id, dict(entry))
            for user_id, values in subscriptions.items()
            if isinstance(values, dict)
            for task_id, entry in values.items()
            if isinstance(entry, dict)
        ]
        turn_owners = dict(state.get("bridge_turn_owners", {}))
    tasks_by_user: dict[str, dict[str, dict[str, str]]] = {}
    unavailable_users: set[str] = set()
    for user_id in dict.fromkeys(user_id for user_id, _task_id, _entry in pending):
        try:
            tasks_by_user[user_id] = {
                task["id"]: task for task in recent_tasks(user_id)
            }
        except (OSError, sqlite3.Error) as exc:
            unavailable_users.add(user_id)
            log(f"task subscription catalog unavailable error={type(exc).__name__}")
    rollout_paths: dict[str, Path | None] = {}
    scan_cache: dict[tuple[Any, ...], dict[str, Any]] = {}
    did_work = False
    for user_id, task_id, entry in pending:
        if user_id in unavailable_users:
            continue
        task = tasks_by_user.get(user_id, {}).get(task_id)
        if task is None:
            with _state_lock:
                state = load_state()
                user_task_subscriptions(state, user_id).pop(task_id, None)
                save_state(state)
            did_work = True
            continue
        if task_id not in rollout_paths:
            try:
                rollout_paths[task_id] = rollout_path_for_task(task_id)
            except sqlite3.Error as exc:
                log(f"task subscription rollout unavailable error={type(exc).__name__}")
                continue
        cursor_offset = int(entry.get("cursor_offset") or 0)
        active_turn_id = str(entry.get("active_turn_id") or "")
        existing_images = tuple(str(item) for item in entry.get("images", []))
        checkpoint = (cursor_offset, active_turn_id, existing_images)
        scan_key = (task_id, cursor_offset, active_turn_id, existing_images)
        if scan_key not in scan_cache:
            scan_cache[scan_key] = scan_task_subscription_rollout(
                rollout_paths[task_id],
                cursor_offset,
                active_turn_id,
                list(existing_images),
            )
        scan = scan_cache[scan_key]
        if not scan.get("available"):
            continue
        delivery_failed = False
        for delivery in scan.get("deliveries", []):
            turn_id = str(delivery.get("turn_id") or "")
            owner = str(turn_owners.get(turn_id) or "")
            if owner != user_id and not deliver_task_subscription_result(
                user_id,
                task,
                delivery,
            ):
                delivery_failed = True
                break
            update_task_subscription_checkpoint(
                user_id,
                task_id,
                int(delivery.get("cursor_offset") or 0),
                str(delivery.get("active_turn_id") or ""),
                [str(item) for item in delivery.get("remaining_images", [])],
            )
            checkpoint = (
                int(delivery.get("cursor_offset") or 0),
                str(delivery.get("active_turn_id") or ""),
                tuple(str(item) for item in delivery.get("remaining_images", [])),
            )
            did_work = True
        if delivery_failed:
            continue
        final_checkpoint = (
            int(scan.get("cursor_offset") or 0),
            str(scan.get("active_turn_id") or ""),
            tuple(str(item) for item in scan.get("images", [])),
        )
        if final_checkpoint != checkpoint:
            update_task_subscription_checkpoint(
                user_id,
                task_id,
                final_checkpoint[0],
                final_checkpoint[1],
                list(final_checkpoint[2]),
            )
    return did_work


def desktop_sync_current_snapshot(
    user_id: str,
    state: dict[str, Any],
) -> tuple[dict[str, str] | None, dict[str, Any]]:
    started = time.monotonic()
    task = selected_task(user_id, state)
    task_lookup_ms = round((time.monotonic() - started) * 1000)
    if task is None:
        log(
            "latency desktop_sync_snapshot "
            f"task_lookup_ms={task_lookup_ms} rollout_scan_ms=0"
        )
        return None, {"status": "none"}
    rollout_started = time.monotonic()
    snapshot = latest_rollout_turn(rollout_path_for_task(str(task["id"])))
    rollout_scan_ms = round((time.monotonic() - rollout_started) * 1000)
    log(
        "latency desktop_sync_snapshot "
        f"task_lookup_ms={task_lookup_ms} rollout_scan_ms={rollout_scan_ms}"
    )
    return task, snapshot


def deliver_desktop_sync_result(
    user_id: str,
    task: dict[str, str],
    message_id: str,
    snapshot: dict[str, Any],
    *,
    result_label: str = "桌面结果已同步",
    reply_message_id: str = "",
) -> None:
    status = str(snapshot.get("status") or "missing")
    reply_target = reply_message_id or message_id
    patch_card(message_id, build_desktop_sync_card(task, status))
    if status == "completed":
        result = str(snapshot.get("message") or "").strip()
        result = result or "Codex 已完成，但没有返回文字结果。"
        allowed_roots = result_roots_for_task(task)
        clean_result, audio_files = prepare_result_audio(result, allowed_roots)
        clean_result, images = prepare_result_images(
            clean_result,
            [str(item) for item in snapshot.get("images", [])],
            allowed_roots,
        )
        clean_result, files = prepare_result_files(clean_result, allowed_roots)
        record_task_exchange(
            user_id,
            str(task["id"]),
            answer=clean_result,
            completed_at=time.time(),
        )
        with result_delivery_lock(user_id):
            followed = follow_result_task(user_id, task)
            delivered = reply_or_queue(
                reply_target,
                task_status_prefix(task, result_label) + clean_result,
                "final",
            )
        if followed:
            schedule_user_task_identity_refresh(
                user_id,
                "当前 Task 已跟随桌面结果",
                task,
            )
        deliver_result_resources(reply_target, images, audio_files, files)
        log(
            "desktop sync result delivered "
            f"text={delivered} images={len(images)} "
            f"audio={len(audio_files)} files={len(files)}"
        )
    else:
        reply_or_queue(
            reply_target,
            task_status_prefix(task, "桌面运行未完成")
            + str(snapshot.get("message") or "没有读取到可同步的完成结果。"),
            "final",
        )
    update_current_status_card(user_id, task=task)


def start_desktop_sync(user_id: str, state: dict[str, Any], event_id: str) -> None:
    task, snapshot = desktop_sync_current_snapshot(user_id, state)
    if task is None:
        start_desktop_sync_switch(user_id, state, event_id)
        return
    send_menu_card(
        user_id,
        state,
        build_desktop_sync_confirmation_card(
            task,
            str(snapshot.get("status") or "none"),
        ),
        f"desktop-sync-confirm-{event_id}",
    )
    log("menu handled key=sync_desktop result=confirmation-card")


def start_desktop_sync_switch(
    user_id: str,
    state: dict[str, Any],
    event_id: str,
) -> None:
    send_menu_card(
        user_id,
        state,
        task_card_for_state(user_id, state),
        f"desktop-sync-switch-{event_id}",
        "desktop_sync_selection",
    )
    log("menu handled key=sync_desktop_switch result=task-selector")


def confirm_desktop_sync(
    user_id: str,
    task: dict[str, str],
    event_id: str,
    message_id: str,
    chat_id: str,
) -> None:
    snapshot = latest_rollout_turn(rollout_path_for_task(str(task["id"])))
    card = build_desktop_sync_card(
        task,
        str(snapshot.get("status") or "none"),
    )
    if not message_id or not patch_card(message_id, card):
        log("card handled action=confirm_desktop_sync result=patch-failed")
        return
    if chat_id:
        with _state_lock:
            state = load_state()
            authorize_chat(state, user_id, chat_id)
    if snapshot.get("status") == "running":
        active = active_run_for_task(str(task["id"]))
        if active is not None and str(active.get("user_id") or "") == user_id:
            log("card handled action=confirm_desktop_sync result=already-tracked")
            return
        with _state_lock:
            state = load_state()
            desktop_result_subscriptions(state)[user_id] = {
                "task_id": str(task["id"]),
                "turn_id": str(snapshot.get("turn_id") or ""),
                "message_id": message_id,
                "chat_id": chat_id or "",
                "cursor_offset": int(snapshot.get("cursor_offset") or 0),
                "images": list(snapshot.get("images") or []),
                "created_at": time.time(),
                "next_check_at": 0,
            }
            save_state(state)
        log("card handled action=confirm_desktop_sync result=subscribed")
        return
    if snapshot.get("status") == "completed":
        deliver_desktop_sync_result(user_id, task, message_id, snapshot)
        log("card handled action=confirm_desktop_sync result=completed")
        return
    log("card handled action=confirm_desktop_sync result=no-result")


def retry_desktop_result_subscriptions(now: float | None = None) -> bool:
    timestamp = time.time() if now is None else now
    with _state_lock:
        state = load_state()
        subscriptions = desktop_result_subscriptions(state)
        selected = next(
            (
                (user_id, dict(value))
                for user_id, value in subscriptions.items()
                if isinstance(value, dict)
                and float(value.get("next_check_at") or 0) <= timestamp
            ),
            None,
        )
    if selected is None:
        return False
    user_id, subscription = selected
    task_id = str(subscription.get("task_id") or "")
    turn_id = str(subscription.get("turn_id") or "")
    message_id = str(subscription.get("message_id") or "")
    task = task_by_id(task_id, user_id) if authorized_user(user_id) else None
    if (
        task is None
        or not turn_id
        or not message_id
        or timestamp - float(subscription.get("created_at") or 0) > 24 * 60 * 60
    ):
        snapshot = {
            "status": "missing",
            "message": "本次桌面接续已失效，请重新点击“接续当前 Task”。",
        }
    else:
        snapshot = advance_rollout_turn(
            rollout_path_for_task(task_id),
            turn_id,
            int(subscription.get("cursor_offset") or 0),
            [str(item) for item in subscription.get("images", [])],
        )
    if snapshot.get("status") == "running":
        with _state_lock:
            state = load_state()
            current = desktop_result_subscriptions(state).get(user_id)
            if isinstance(current, dict) and current.get("turn_id") == turn_id:
                current["cursor_offset"] = int(snapshot.get("cursor_offset") or 0)
                current["images"] = list(snapshot.get("images") or [])
                current["next_check_at"] = timestamp + 1
                save_state(state)
        return True
    if task is not None:
        deliver_desktop_sync_result(user_id, task, message_id, snapshot)
    elif message_id:
        reply_or_queue(
            message_id,
            "本次桌面接续已失效：该 Task 已归档、删除或不再属于你的授权项目。请重新点击“接续当前 Task”。",
            "final",
        )
    with _state_lock:
        state = load_state()
        current = desktop_result_subscriptions(state).get(user_id)
        if isinstance(current, dict) and current.get("turn_id") == turn_id:
            desktop_result_subscriptions(state).pop(user_id, None)
            save_state(state)
    log(f"desktop sync subscription finished status={snapshot.get('status')}")
    return True


def retry_recoverable_runs(now: float | None = None) -> bool:
    timestamp = time.time() if now is None else now
    with _state_lock:
        state = load_state()
        selected = next(
            (
                (turn_id, dict(value))
                for turn_id, value in recoverable_runs(state).items()
                if isinstance(value, dict)
                and float(value.get("next_check_at") or 0) <= timestamp
            ),
            None,
        )
    if selected is None:
        return False
    turn_id, recovery = selected
    if active_run_for_turn(turn_id) is not None:
        return False
    user_id = str(recovery.get("user_id") or "")
    task_id = str(recovery.get("task", {}).get("id") or "")
    source_message_id = str(recovery.get("source_message_id") or "")
    progress_message_id = str(recovery.get("progress_message_id") or "")
    task = task_by_id(task_id, user_id) if authorized_user(user_id) else None
    expired = timestamp - float(recovery.get("created_at") or 0) > 24 * 60 * 60
    terminal_status = str(recovery.get("terminal_status") or "")
    if task is None or expired:
        snapshot = {
            "status": "missing",
            "message": (
                "本次重启恢复已失效：Task 已归档、删除或不再属于你的授权项目。"
                if task is None
                else "本次重启恢复已超过 24 小时，请在 Codex Desktop 中查看结果。"
            ),
        }
    elif terminal_status in {"completed", "failed"}:
        snapshot = {
            "status": terminal_status,
            "message": str(recovery.get("terminal_message") or ""),
            "images": list(recovery.get("images") or []),
        }
    else:
        snapshot = advance_rollout_turn(
            rollout_path_for_task(task_id),
            turn_id,
            int(recovery.get("cursor_offset") or 0),
            [str(item) for item in recovery.get("images", [])],
        )
    if snapshot.get("status") == "running":
        if not recovery.get("recovery_announced") and progress_message_id:
            card_run = dict(recovery)
            card_run.update(
                {
                    "status": "桥接已恢复，正在继续等待 Codex 结果",
                    "outcome": "recovering",
                    "is_current_task": task_is_current(user_id, task_id),
                }
            )
            patch_card(progress_message_id, build_run_card(card_run))
        with _state_lock:
            state = load_state()
            current = recoverable_runs(state).get(turn_id)
            if isinstance(current, dict):
                current["cursor_offset"] = int(snapshot.get("cursor_offset") or 0)
                current["images"] = list(snapshot.get("images") or [])
                current["next_check_at"] = timestamp + 1
                current["recovery_announced"] = True
                save_state(state)
        return True
    if snapshot.get("status") == "missing" and task is not None:
        missing_since = float(recovery.get("missing_since") or timestamp)
        if timestamp - missing_since < 60:
            with _state_lock:
                state = load_state()
                current = recoverable_runs(state).get(turn_id)
                if isinstance(current, dict):
                    current["missing_since"] = missing_since
                    current["next_check_at"] = timestamp + 1
                    save_state(state)
            return True
    delivery_message_id = progress_message_id
    if task is not None and not delivery_message_id and source_message_id:
        delivered, _chat_id, delivery_message_id = reply_card_message(
            source_message_id,
            build_desktop_sync_card(task, str(snapshot.get("status") or "missing")),
            f"restart-recovery-{turn_id}",
        )
        if not delivered or not delivery_message_id:
            with _state_lock:
                state = load_state()
                current = recoverable_runs(state).get(turn_id)
                if isinstance(current, dict):
                    current["next_check_at"] = timestamp + 2
                    save_state(state)
            return True
    if task is not None and delivery_message_id:
        deliver_desktop_sync_result(
            user_id,
            task,
            delivery_message_id,
            snapshot,
            result_label="重启后结果已恢复",
            reply_message_id=source_message_id or delivery_message_id,
        )
    elif source_message_id:
        if progress_message_id:
            failed_run = dict(recovery)
            failed_run.update(
                {
                    "status": str(snapshot.get("message") or "重启后无法恢复本次运行"),
                    "outcome": "failed",
                }
            )
            patch_card(progress_message_id, build_run_card(failed_run))
        reply_or_queue(
            source_message_id,
            str(snapshot.get("message") or "重启后无法恢复本次运行。"),
            "final",
        )
    remove_recoverable_run(turn_id)
    log(f"restart recovery finished status={snapshot.get('status')}")
    return True


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
            "subtitle": {"tag": "plain_text", "content": "DeepOri Bridge"},
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


def remember_recent_task(state: dict[str, Any], user_id: str, task_id: str) -> None:
    recent = state.setdefault("recent_task_ids", {}).get(user_id, [])
    if not isinstance(recent, list):
        recent = []
    values = [task_id] + [str(value) for value in recent if str(value) != task_id]
    state.setdefault("recent_task_ids", {})[user_id] = values[:RECENT_TASK_LIMIT]


def task_preferences(
    state: dict[str, Any],
    user_id: str,
) -> tuple[set[str], list[str], str]:
    favorites = state.setdefault("favorite_task_ids", {}).get(user_id, [])
    recent = state.setdefault("recent_task_ids", {}).get(user_id, [])
    scope = str(state.setdefault("task_scopes", {}).get(user_id) or "all")
    if scope not in {"all", "recent", "favorites"}:
        scope = "all"
    return (
        {str(value) for value in favorites} if isinstance(favorites, list) else set(),
        [str(value) for value in recent] if isinstance(recent, list) else [],
        scope,
    )


def summary_fragment(value: str) -> str:
    return " ".join(str(value).split())[:TASK_SUMMARY_CHARS]


def record_task_exchange(
    user_id: str,
    task_id: str,
    question: str = "",
    answer: str = "",
    completed_at: float | None = None,
) -> None:
    try:
        with _state_lock:
            state = load_state()
            remember_recent_task(state, user_id, task_id)
            summaries = state.setdefault("task_summaries", {}).setdefault(user_id, {})
            entry = summaries.get(task_id, {})
            if not isinstance(entry, dict):
                entry = {}
            if question:
                entry["question"] = summary_fragment(question)
                entry["asked_at"] = time.time()
                entry.pop("answer", None)
                entry.pop("completed_at", None)
            if answer:
                entry["answer"] = summary_fragment(answer)
                entry["completed_at"] = time.time() if completed_at is None else completed_at
            summaries[task_id] = entry
            save_state(state)
    except OSError:
        return


def task_is_current(user_id: str, task_id: str) -> bool:
    with _state_lock:
        state = load_state()
        return str(state.get("selected", {}).get(user_id) or "") == task_id


def result_delivery_lock(user_id: str) -> Any:
    with _result_delivery_locks_lock:
        lock = _result_delivery_locks.get(user_id)
        if lock is None:
            lock = threading.Lock()
            _result_delivery_locks[user_id] = lock
        return lock


def follow_result_task(user_id: str, task: dict[str, str]) -> bool:
    current_task = task_by_id(str(task["id"]), user_id)
    if current_task is None:
        return False
    with _state_lock:
        state = load_state()
        selected = state.setdefault("selected", {})
        if str(selected.get(user_id) or "") == str(current_task["id"]):
            return False
        selected[user_id] = current_task["id"]
        state.setdefault("last_projects", {})[user_id] = current_task["project"]
        remember_recent_task(state, user_id, str(current_task["id"]))
        save_state(state)
    return True


def build_current_status_card(
    task: dict[str, str],
    status: str,
    active_runs: int,
    queued_inputs: int,
    change: str = "",
    now: float | None = None,
    recent_exchange: dict[str, Any] | None = None,
    is_favorite: bool = False,
) -> dict[str, Any]:
    timestamp = time.time() if now is None else now
    status_tag = (
        ("运行中", "blue")
        if active_runs
        else ("有排队", "yellow")
        if queued_inputs
        else ("空闲", "neutral")
    )
    lines = []
    if change:
        lines.append(f"✅ **{card_markdown_escape(change)}**")
    lines.extend(
        [
            f"**当前状态**\n{card_markdown_escape(status)}",
            (
                "<font color='grey'>"
                f"运行中：{active_runs} · 排队：{queued_inputs} · "
                f"更新：{time.strftime('%H:%M:%S', time.localtime(timestamp))}"
                "</font>"
            ),
        ]
    )
    exchange = recent_exchange if isinstance(recent_exchange, dict) else {}
    question = summary_fragment(str(exchange.get("question") or ""))
    answer = summary_fragment(str(exchange.get("answer") or ""))
    if question:
        lines.append(f"**最近提问**\n{card_markdown_escape(question)}")
    if answer:
        lines.append(f"**最近回复**\n{card_markdown_escape(answer)}")
        try:
            completed_at = float(exchange.get("completed_at") or 0)
        except (TypeError, ValueError):
            completed_at = 0
        if completed_at > 0:
            lines.append(
                "<font color='grey'>最近完成："
                f"{time.strftime('%m-%d %H:%M', time.localtime(completed_at))}"
                "</font>"
            )
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": f"当前 Task：{task['title']}"},
        },
        "header": {
            "title": {"tag": "plain_text", "content": task_title_text(task)},
            "subtitle": {"tag": "plain_text", "content": task_project_text(task)},
            "template": "green",
            "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
            "text_tag_list": [
                current_task_tag(),
                *(
                    [
                        {
                            "tag": "text_tag",
                            "text": {"tag": "plain_text", "content": "已收藏"},
                            "color": "yellow",
                        }
                    ]
                    if is_favorite
                    else []
                ),
                {
                    "tag": "text_tag",
                    "text": {"tag": "plain_text", "content": status_tag[0]},
                    "color": status_tag[1],
                },
            ],
        },
        "body": {
            "direction": "vertical",
            "padding": "12px 12px 20px 12px",
            "vertical_spacing": "12px",
            "elements": [
                {"tag": "markdown", "content": "\n\n".join(lines)},
                {
                    "tag": "button",
                    "text": {
                        "tag": "plain_text",
                        "content": "取消收藏" if is_favorite else "收藏当前 Task",
                    },
                    "type": "default",
                    "behaviors": [
                        {
                            "type": "callback",
                            "value": {
                                "action": "toggle_task_favorite",
                                "task_id": str(task["id"]),
                                "return_to": "status",
                            },
                        }
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "刷新状态"},
                    "type": "default",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "refresh_current_status"}}
                    ],
                },
                {
                    "tag": "button",
                    "text": {"tag": "plain_text", "content": "切换 Task"},
                    "type": "default",
                    "behaviors": [
                        {"type": "callback", "value": {"action": "show_task_selector"}}
                    ],
                },
            ],
        },
    }


def current_status_card_for_user(
    user_id: str,
    task: dict[str, str],
    change: str = "",
) -> dict[str, Any]:
    task_id = str(task["id"])
    with _active_runs_lock:
        matching_runs = [
            run
            for run in _active_runs.values()
            if str(run.get("user_id") or "") == user_id
            and str(run.get("task", {}).get("id") or "") == task_id
            and run.get("outcome") in {"running", "approval"}
        ]
        status = str(matching_runs[0].get("status") or "正在运行") if matching_runs else "空闲"
    with _state_lock:
        state = load_state()
        queued = sum(
            isinstance(entry, dict)
            and str(entry.get("user_id") or "") == user_id
            and str(entry.get("task", {}).get("id") or "") == task_id
            for entry in pending_inputs(state)
        )
        summaries = state.get("task_summaries", {}).get(user_id, {})
        recent_exchange = summaries.get(task_id, {}) if isinstance(summaries, dict) else {}
        favorites, _recent, _scope = task_preferences(state, user_id)
    if not matching_runs and queued:
        status = "等待执行"
    return build_current_status_card(
        task,
        status,
        len(matching_runs),
        queued,
        change,
        recent_exchange=recent_exchange,
        is_favorite=task_id in favorites,
    )


def update_current_status_card(
    user_id: str,
    change: str = "",
    task: dict[str, str] | None = None,
    ensure: bool = False,
    force_new: bool = False,
) -> bool:
    with _state_lock:
        state = load_state()
        current_id = str(state.get("selected", {}).get(user_id) or "")
        record = state.get("current_status_cards", {}).get(user_id, {})
        message_id = str(record.get("message_id") or "") if isinstance(record, dict) else ""
    if force_new:
        message_id = ""
    if task is None and current_id:
        task = next(
            (
                candidate
                for candidate in recent_tasks(user_id)
                if str(candidate.get("id") or "") == current_id
            ),
            None,
        )
    if task is None:
        return False
    if not message_id and not ensure:
        return False
    card = current_status_card_for_user(user_id, task, change)
    if message_id and patch_card(message_id, card):
        return True
    success, chat_id, new_message_id = send_card(
        user_id,
        card,
        f"current-status-{uuid.uuid4().hex}",
    )
    if not success or not new_message_id:
        return False
    if message_id:
        clear_pending_card_patch(message_id)
    with _state_lock:
        state = load_state()
        state.setdefault("current_status_cards", {})[user_id] = {
            "message_id": new_message_id,
            "chat_id": chat_id or "",
        }
        if chat_id:
            authorize_chat(state, user_id, chat_id)
        save_state(state)
    return True


def refresh_user_task_identity_cards(
    user_id: str,
    change: str = "",
    task: dict[str, str] | None = None,
) -> None:
    with _state_lock:
        state = load_state()
        current_id = str(state.get("selected", {}).get(user_id) or "")
        entries = pending_inputs(state)
        queued_cards: list[tuple[str, dict[str, Any], int]] = []
        changed = False
        for entry in entries:
            if not isinstance(entry, dict) or str(entry.get("user_id") or "") != user_id:
                continue
            is_current = str(entry.get("task", {}).get("id") or "") == current_id
            if entry.get("is_current_task") is not is_current:
                entry["is_current_task"] = is_current
                changed = True
            message_id = str(entry.get("progress_message_id") or "")
            if message_id:
                position = queued_position(
                    entries,
                    str(entry.get("task", {}).get("id") or ""),
                    str(entry.get("queue_id") or ""),
                )
                queued_cards.append((message_id, dict(entry), position))
        if changed:
            save_state(state)
    with _active_runs_lock:
        run_cards: list[tuple[str, dict[str, Any]]] = []
        approval_cards: list[tuple[str, dict[str, Any]]] = []
        for run in _active_runs.values():
            if str(run.get("user_id") or "") != user_id:
                continue
            run["is_current_task"] = str(run.get("task", {}).get("id") or "") == current_id
            message_id = str(run.get("progress_message_id") or "")
            if message_id:
                run_cards.append((message_id, build_run_card(run)))
            for approval in run.get("approvals", {}).values():
                approval_message_id = str(approval.get("message_id") or "")
                if approval_message_id and not approval.get("resolved"):
                    approval_cards.append(
                        (approval_message_id, build_approval_card(run, approval))
                    )
    for message_id, card in run_cards + approval_cards:
        patch_card(message_id, card)
    for message_id, entry, position in queued_cards:
        patch_card(message_id, build_queued_card(entry, position))
    update_current_status_card(user_id, change, task=task, ensure=True)


def schedule_user_task_identity_refresh(
    user_id: str,
    change: str = "",
    task: dict[str, str] | None = None,
) -> None:
    with _identity_refresh_condition:
        _identity_refresh_pending[user_id] = (
            change,
            dict(task) if task is not None else None,
            time.monotonic(),
        )
        _identity_refresh_condition.notify()


def schedule_queued_card_refresh(task_id: str) -> None:
    with _identity_refresh_condition:
        _queued_card_refresh_pending[task_id] = time.monotonic()
        _identity_refresh_condition.notify()


def identity_refresh_loop() -> None:
    while not _shutdown_event.is_set():
        with _identity_refresh_condition:
            if not _identity_refresh_pending and not _queued_card_refresh_pending:
                _identity_refresh_condition.wait(timeout=0.5)
            if _shutdown_event.is_set():
                return
            if not _identity_refresh_pending and not _queued_card_refresh_pending:
                continue
            if _identity_refresh_pending:
                user_id, (change, task, queued_at) = _identity_refresh_pending.popitem()
                operation = "identity_refresh"
            else:
                task_id, queued_at = _queued_card_refresh_pending.popitem()
                operation = "queued_card_refresh"
        started = time.monotonic()
        try:
            if operation == "identity_refresh":
                refresh_user_task_identity_cards(user_id, change, task)
            else:
                refresh_queued_cards(task_id)
        except Exception as exc:
            log(f"background refresh failed operation={operation} error={type(exc).__name__}")
        else:
            log(
                f"latency background operation={operation} "
                f"queue_ms={round((started - queued_at) * 1000)} "
                f"duration_ms={round((time.monotonic() - started) * 1000)}"
            )


def task_card_for_state(
    user_id: str,
    state: dict[str, Any],
    selection_changed: bool = False,
    selected_id_override: str | None = None,
) -> dict[str, Any]:
    with _state_lock:
        tasks = recent_tasks(user_id)
        selected = (
            next(
                (
                    task
                    for task in tasks
                    if str(task.get("id") or "") == selected_id_override
                ),
                None,
            )
            if selected_id_override is not None
            else selected_task(user_id, state)
        )
        state.setdefault("last_lists", {})[user_id] = [task["id"] for task in tasks]
        project_filter = state.setdefault("last_projects", {}).get(user_id)
        page = int(state.setdefault("task_pages", {}).get(user_id) or 0)
        query = str(state.setdefault("task_queries", {}).get(user_id) or "")
        favorites, recent, scope = task_preferences(state, user_id)
        save_state(state)
        return build_task_card(
            tasks,
            selected["id"] if selected else None,
            str(project_filter) if project_filter else None,
            page,
            query,
            selection_changed=selection_changed,
            favorite_ids=favorites,
            recent_ids=recent,
            task_scope=scope,
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
        remember_recent_task(state, user_id, str(selected["id"]))
        save_state(state)
        return current_task_changed_text(selected)


def current_task(user_id: str, state: dict[str, Any]) -> str:
    task = selected_task(user_id, state)
    if not task:
        return "尚未选择 Codex task。请点击机器人菜单中的“切换 Task”。"
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
        if response.get("type") == "client-discovery-request":
            send_ipc_message(
                connection,
                {
                    "type": "client-discovery-response",
                    "requestId": response.get("requestId"),
                    "response": {"canHandle": False},
                },
            )
            continue
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


def desktop_task_state_once(thread_id: str, timeout: float = 8) -> dict[str, Any]:
    if not DESKTOP_IPC_SOCKET.exists():
        raise RuntimeError("Codex Desktop 当前不可用。")
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(timeout)
        connection.connect(str(DESKTOP_IPC_SOCKET))
        client_id = initialize_desktop_connection(connection)
        begin_desktop_following(connection, client_id, thread_id)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            frame = receive_ipc_message(connection)
            if frame.get("type") == "client-discovery-request":
                send_ipc_message(
                    connection,
                    {
                        "type": "client-discovery-response",
                        "requestId": frame.get("requestId"),
                        "response": {"canHandle": False},
                    },
                )
                continue
            params = frame.get("params")
            if (
                frame.get("type") != "broadcast"
                or frame.get("method") != "thread-stream-state-changed"
                or not isinstance(params, dict)
                or str(params.get("conversationId") or "") != thread_id
            ):
                continue
            change = params.get("change")
            state = (
                change.get("conversationState")
                if isinstance(change, dict) and change.get("type") == "snapshot"
                else None
            )
            if isinstance(state, dict):
                return state
    raise RuntimeError("Codex Desktop 没有返回当前 Task 设置。")


def desktop_task_state(thread_id: str) -> dict[str, Any]:
    try:
        return desktop_task_state_once(thread_id)
    except (ConnectionError, FileNotFoundError, OSError, socket.timeout, RuntimeError):
        if not activate_desktop_task(thread_id):
            raise RuntimeError("Codex Desktop 尚未加载这个 Task。")
    time.sleep(DESKTOP_UNAVAILABLE_RETRY_DELAYS[-1])
    try:
        return desktop_task_state_once(thread_id)
    except (ConnectionError, FileNotFoundError, OSError, socket.timeout, RuntimeError) as exc:
        raise RuntimeError("Codex Desktop 尚未加载这个 Task。") from exc


def desktop_task_request(
    thread_id: str,
    method: str,
    params: dict[str, Any],
    version: int = 1,
    timeout_ms: int = 30000,
) -> dict[str, Any]:
    if not DESKTOP_IPC_SOCKET.exists():
        raise RuntimeError("Codex Desktop 当前不可用。")
    request_params = {"conversationId": thread_id, **params}
    activation_attempted = False
    for attempt in range(2):
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                connection.settimeout(max(5, timeout_ms / 1000))
                connection.connect(str(DESKTOP_IPC_SOCKET))
                client_id = initialize_desktop_connection(connection)
                request_id = str(uuid.uuid4())
                send_ipc_message(
                    connection,
                    {
                        "type": "request",
                        "requestId": request_id,
                        "sourceClientId": client_id,
                        "version": version,
                        "method": method,
                        "params": request_params,
                        "timeoutMs": timeout_ms,
                    },
                )
                response = wait_for_ipc_response(connection, request_id)
        except (ConnectionError, FileNotFoundError, OSError, socket.timeout) as exc:
            raise RuntimeError("无法连接 Codex Desktop。") from exc
        if response.get("resultType") == "success":
            return response
        error = " ".join(str(response.get("error") or "").split())
        if (
            error == "no-client-found"
            and attempt == 0
            and not activation_attempted
            and activate_desktop_task(thread_id)
        ):
            activation_attempted = True
            time.sleep(DESKTOP_UNAVAILABLE_RETRY_DELAYS[-1])
            continue
        raise RuntimeError(
            "Codex Desktop 尚未加载这个 Task，请稍后重试。"
            if error == "no-client-found"
            else "Codex Desktop 没有接受本次设置操作。"
        )
    raise RuntimeError("Codex Desktop 尚未加载这个 Task，请稍后重试。")


def update_desktop_task_settings(
    thread_id: str,
    *,
    model: str | None = None,
    effort: str | None = None,
    service_tier: str | None = None,
) -> None:
    settings: dict[str, Any] = {}
    if model is not None:
        settings["model"] = model
    if effort is not None:
        settings["effort"] = effort
    if service_tier is not None:
        settings["serviceTier"] = service_tier
    if not settings:
        raise ValueError("missing task settings")
    desktop_task_request(
        thread_id,
        "thread-follower-update-thread-settings",
        {"threadSettings": settings},
        version=1,
    )


def compact_desktop_task(thread_id: str) -> None:
    desktop_task_request(
        thread_id,
        "thread-follower-compact-thread",
        {},
        version=1,
        timeout_ms=60000,
    )


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


def patch_promlight_event_card(
    event: dict[str, Any],
    user_id: str,
    card: dict[str, Any],
    context_type: str = "",
) -> None:
    message_id = str(event.get("message_id") or "")
    if message_id and context_type:
        with _state_lock:
            state = load_state()
            remember_card_context(state, user_id, message_id, card, context_type)
    if message_id and patch_card(message_id, card, persist=False):
        return
    token = str(event.get("token") or "")
    if token and update_card(token, card):
        if message_id:
            clear_pending_card_patch(message_id)
        return
    if message_id:
        queue_pending_card_patch(message_id, card, "飞书卡片刷新失败")


def handle_promlight_button_action(
    event: dict[str, Any],
    payload: dict[str, Any],
) -> bool:
    action = str(payload.get("action") or "")
    recognized = {
        "show_promlight",
        "promlight_refresh",
        "promlight_manage_tasks",
        "promlight_toggle_task",
        "promlight_set_default",
        "promlight_start_rename",
        "promlight_cancel_rename",
        "promlight_unbind",
        "promlight_local_pairing",
        "promlight_mobile_pairing",
    }
    if action not in recognized:
        return False
    user_id = str(event.get("operator_id") or "")
    lamp_id = str(payload.get("lamp_id") or "")
    if action == "promlight_local_pairing":
        patch_promlight_event_card(event, user_id, build_promlight_local_pairing_card())
        return True
    if action == "promlight_mobile_pairing":
        patch_promlight_event_card(event, user_id, build_promlight_mobile_pairing_card())
        return True
    if action == "show_promlight":
        reconcile_promlight_state()
        with _state_lock:
            state = load_state()
            card = build_promlight_control_card(user_id, state)
        patch_promlight_event_card(event, user_id, card, "promlight")
        return True
    if action == "promlight_refresh":
        reconcile_promlight_state()
        with _state_lock:
            state = load_state()
            lamp_ids = [str(lamp["lamp_id"]) for lamp in user_promlight_lamps(state, user_id)]
        for current_id in lamp_ids:
            refresh_promlight_lamp(current_id, force=True)
        with _state_lock:
            state = load_state()
            card = build_promlight_control_card(user_id, state)
        patch_promlight_event_card(event, user_id, card, "promlight")
        return True
    try:
        with _state_lock:
            state = load_state()
            lamp = owned_promlight_lamp(state, user_id, lamp_id)
            lamp_copy = dict(lamp)
    except PermissionError:
        with _state_lock:
            state = load_state()
            card = build_promlight_control_card(user_id, state)
        patch_promlight_event_card(event, user_id, card, "promlight")
        return True
    if action == "promlight_manage_tasks":
        with _state_lock:
            state = load_state()
            card = build_promlight_task_card(user_id, lamp_id, state)
        patch_promlight_event_card(event, user_id, card, "promlight_tasks")
        return True
    if action == "promlight_toggle_task":
        task_id = str(payload.get("task_id") or "")
        enabled = task_id not in lamp_copy.get("task_ids", [])
        with _state_lock:
            state = load_state()
            processing_card = promlight_action_processing_card(
                build_promlight_task_card(user_id, lamp_id, state),
                action,
            )
        patch_promlight_event_card(
            event,
            user_id,
            processing_card,
            "promlight_tasks",
        )
        try:
            set_promlight_task_subscription(user_id, lamp_id, task_id, enabled)
            change = "已关注这个 Task" if enabled else "已取消关注这个 Task"
        except PermissionError as exc:
            change = str(exc)
        with _state_lock:
            state = load_state()
            card = build_promlight_task_card(user_id, lamp_id, state, change)
        patch_promlight_event_card(event, user_id, card, "promlight_tasks")
        return True
    if action == "promlight_set_default":
        set_default_promlight(user_id, lamp_id)
    elif action == "promlight_unbind":
        unbind_promlight(user_id, lamp_id)
    elif action == "promlight_start_rename":
        with _state_lock:
            state = load_state()
            promlight_state(state)["pending_renames"][user_id] = lamp_id
            save_state(state)
        patch_promlight_event_card(event, user_id, build_promlight_rename_card(lamp_copy))
        return True
    elif action == "promlight_cancel_rename":
        with _state_lock:
            state = load_state()
            promlight_state(state)["pending_renames"].pop(user_id, None)
            save_state(state)
    with _state_lock:
        state = load_state()
        card = build_promlight_control_card(user_id, state)
    patch_promlight_event_card(event, user_id, card, "promlight")
    return True


def handle_promlight_selector_action(event: dict[str, Any]) -> bool:
    action_name = str(event.get("action_name") or "")
    if action_name not in {"promlight_project_selector", "promlight_task_selector"}:
        return False
    user_id = str(event.get("operator_id") or "")
    selected_value = str(event.get("option") or "")
    stale_card: dict[str, Any] | None = None
    with _state_lock:
        state = load_state()
        namespace = promlight_state(state)
        context = card_context_details(
            state,
            user_id,
            str(event.get("message_id") or ""),
        )
        lamp_id = str(context.get("lamp_id") or "")
        if context.get("type") != "promlight_tasks" or not lamp_id:
            stale_card = task_card_with_notice(
                build_promlight_control_card(user_id, state),
                "这张提示灯卡片已失效，请从“我的提示灯”重新打开。",
            )
        if stale_card is not None:
            pass
        else:
            try:
                owned_promlight_lamp(state, user_id, lamp_id)
            except PermissionError:
                stale_card = task_card_with_notice(
                    build_promlight_control_card(user_id, state),
                    "这张提示灯卡片已失效，请从“我的提示灯”重新打开。",
                )
        if stale_card is not None:
            card = stale_card
        else:
            tasks = recent_tasks(user_id)
            if action_name == "promlight_project_selector":
                projects = {task["project"] for task in tasks}
                if selected_value not in projects:
                    return True
                namespace["selected_projects"][user_id] = selected_value
                first = next((task for task in tasks if task["project"] == selected_value), None)
                if first is not None:
                    namespace["selected_tasks"][user_id] = first["id"]
            else:
                task = next((item for item in tasks if item["id"] == selected_value), None)
                active_project = str(context.get("project") or "")
                if task is None or (active_project and task["project"] != active_project):
                    return True
                namespace["selected_tasks"][user_id] = task["id"]
                namespace["selected_projects"][user_id] = task["project"]
            save_state(state)
            card = build_promlight_task_card(user_id, lamp_id, state)
    patch_promlight_event_card(
        event,
        user_id,
        card,
        "promlight" if stale_card is not None else "promlight_tasks",
    )
    return True


def pending_inputs(state: dict[str, Any]) -> list[dict[str, Any]]:
    pending = state.setdefault("pending_inputs", [])
    if not isinstance(pending, list):
        pending = []
        state["pending_inputs"] = pending
    return pending


def pending_cli_fallbacks(state: dict[str, Any]) -> dict[str, dict[str, Any]]:
    pending = state.setdefault("pending_cli_fallbacks", {})
    if not isinstance(pending, dict):
        pending = {}
        state["pending_cli_fallbacks"] = pending
    return pending


def prune_cli_fallbacks(
    state: dict[str, Any],
    now: float | None = None,
) -> int:
    timestamp = time.time() if now is None else now
    pending = pending_cli_fallbacks(state)
    expired = []
    for fallback_id, value in pending.items():
        if not isinstance(value, dict):
            expired.append(fallback_id)
            continue
        try:
            created_at = float(value.get("created_at") or 0)
        except (TypeError, ValueError):
            created_at = 0
        if timestamp - created_at > CLI_FALLBACK_TTL_SECONDS:
            expired.append(fallback_id)
    for fallback_id in expired:
        pending.pop(fallback_id, None)
    return len(expired)


def expire_cli_fallbacks(now: float | None = None) -> int:
    with _state_lock:
        state = load_state()
        removed = prune_cli_fallbacks(state, now)
        if removed:
            save_state(state)
            log(f"cli fallback expired count={removed}")
        return removed


def invalidate_cli_fallbacks(user_id: str, task_id: str) -> int:
    with _state_lock:
        state = load_state()
        pending = pending_cli_fallbacks(state)
        stale = [
            fallback_id
            for fallback_id, entry in pending.items()
            if isinstance(entry, dict)
            and str(entry.get("user_id") or "") == user_id
            and isinstance(entry.get("task"), dict)
            and str(entry["task"].get("id") or "") == task_id
        ]
        for fallback_id in stale:
            pending.pop(fallback_id, None)
        if stale:
            save_state(state)
            log(f"cli fallback superseded count={len(stale)}")
        return len(stale)


def remember_cli_fallback(
    run: dict[str, Any],
    content: str,
    image_keys: list[str],
    file_keys: list[str],
    raw_content: str,
    message_type: str,
    reason: str,
) -> str:
    fallback_id = str(uuid.uuid4())
    now = time.time()
    entry = {
        "fallback_id": fallback_id,
        "user_id": str(run["user_id"]),
        "chat_id": str(run["chat_id"]),
        "source_message_id": str(run["source_message_id"]),
        "progress_message_id": str(run.get("progress_message_id") or ""),
        "task": run["task"],
        "content": content,
        "image_keys": image_keys,
        "file_keys": file_keys,
        "raw_content": raw_content,
        "message_type": message_type,
        "reason": reason,
        "created_at": now,
    }
    with _state_lock:
        state = load_state()
        pending = pending_cli_fallbacks(state)
        prune_cli_fallbacks(state, now)
        pending[fallback_id] = entry
        if len(pending) > MAX_PENDING_CLI_FALLBACKS:
            oldest = sorted(
                pending,
                key=lambda key: float(pending[key].get("created_at") or 0),
            )
            for key in oldest[:-MAX_PENDING_CLI_FALLBACKS]:
                pending.pop(key, None)
        save_state(state)
    return fallback_id


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
    write_runtime_status()


def claim_active_run(run: dict[str, Any]) -> bool:
    with _active_runs_lock:
        thread_id = str(run["task"]["id"])
        running = [
            item
            for item in _active_runs.values()
            if item.get("outcome") in {"running", "approval"}
        ]
        if any(
            str(item["task"]["id"]) == thread_id
            and item.get("outcome") in {"running", "approval"}
            for item in running
        ):
            run["queue_reason"] = "same_task"
            run["active_run_count"] = len(running)
            return False
        if len(running) >= MAX_CONCURRENT_RUNS:
            run["queue_reason"] = "global_limit"
            run["active_run_count"] = len(running)
            return False
        _active_runs[str(run["run_id"])] = run
    write_runtime_status()
    return True


def remove_active_run(run_id: str) -> None:
    with _active_runs_lock:
        _active_runs.pop(run_id, None)
    write_runtime_status()


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


def active_run_for_turn(turn_id: str) -> dict[str, Any] | None:
    with _active_runs_lock:
        return next(
            (
                run
                for run in _active_runs.values()
                if str(run.get("turn_id") or "") == turn_id
                and run.get("outcome") in {"running", "approval", "desktop_retrying"}
            ),
            None,
        )


def persist_recoverable_run(run: dict[str, Any]) -> bool:
    turn_id = str(run.get("turn_id") or "")
    task = run.get("task") if isinstance(run.get("task"), dict) else {}
    task_id = str(task.get("id") or "")
    user_id = str(run.get("user_id") or "")
    if not turn_id or not task_id or not user_id:
        return False
    snapshot: dict[str, Any] = {}
    try:
        snapshot = latest_rollout_turn(rollout_path_for_task(task_id))
    except (OSError, sqlite3.Error):
        snapshot = {}
    same_turn = str(snapshot.get("turn_id") or "") == turn_id
    entry = {
        "run_id": str(run.get("run_id") or ""),
        "turn_id": turn_id,
        "user_id": user_id,
        "chat_id": str(run.get("chat_id") or ""),
        "source_message_id": str(run.get("source_message_id") or ""),
        "progress_message_id": str(run.get("progress_message_id") or ""),
        "task": {
            "id": task_id,
            "title": str(task.get("title") or ""),
            "project": str(task.get("project") or ""),
        },
        "cursor_offset": int(snapshot.get("cursor_offset") or 0),
        "images": list(snapshot.get("images") or []) if same_turn else [],
        "created_at": time.time(),
        "started_at": float(run.get("started_at") or time.time()),
        "attachment_count": int(run.get("attachment_count") or 0),
        "timeline": [
            {
                "kind": str(item.get("kind") or "")[:40],
                "label": str(item.get("label") or "")[:80],
                "state": "done" if item.get("state") == "done" else "active",
                "at": float(item.get("at") or 0),
            }
            for item in run.get("timeline", [])[-6:]
            if isinstance(item, dict) and str(item.get("label") or "").strip()
        ],
        "next_check_at": 0,
    }
    if same_turn and snapshot.get("status") in {"completed", "failed"}:
        entry["terminal_status"] = str(snapshot.get("status") or "")
        entry["terminal_message"] = str(snapshot.get("message") or "")
    with _state_lock:
        state = load_state()
        existing = recoverable_runs(state).get(turn_id)
        if isinstance(existing, dict):
            entry["created_at"] = float(existing.get("created_at") or entry["created_at"])
            entry["cursor_offset"] = max(
                int(existing.get("cursor_offset") or 0),
                int(entry.get("cursor_offset") or 0),
            )
            entry["images"] = list(
                dict.fromkeys(
                    [str(item) for item in existing.get("images", [])]
                    + [str(item) for item in entry.get("images", [])]
                )
            )
            for key in (
                "recovery_announced",
                "missing_since",
                "terminal_status",
                "terminal_message",
            ):
                if key in existing and key not in entry:
                    entry[key] = existing[key]
        recoverable_runs(state)[turn_id] = entry
        save_state(state)
    return True


def remove_recoverable_run(turn_id: str) -> None:
    if not turn_id:
        return
    with _state_lock:
        state = load_state()
        if recoverable_runs(state).pop(turn_id, None) is not None:
            save_state(state)


def prepare_active_run_recovery() -> None:
    with _active_runs_lock:
        runs = [
            run
            for run in _active_runs.values()
            if run.get("outcome") in {"running", "approval", "desktop_retrying"}
        ]
    for run in runs:
        turn_id = str(run.get("turn_id") or "")
        if turn_id:
            persist_recoverable_run(run)
        message_id = str(run.get("progress_message_id") or "")
        if not message_id:
            continue
        card_run = dict(run)
        card_run.update(
            {
                "status": (
                    "桥接正在重启，已保留当前 Task；恢复后继续推送结果"
                    if turn_id
                    else "桥接在提交确认前重启；请先查看 Codex Desktop，避免重复发送"
                ),
                "outcome": "recovering" if turn_id else "failed",
            }
        )
        patch_card(message_id, build_run_card(card_run))


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
        "is_current_task": task_is_current(user_id, str(task["id"])),
        "status": "正在准备",
        "outcome": "running",
        "started_at": time.time(),
        "timeline": [
            {
                "kind": "preparing",
                "label": "准备消息",
                "state": "active",
                "at": time.time(),
            }
        ],
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
                task_status_prefix(
                    run["task"],
                    "正在准备",
                    run.get("is_current_task") is not False,
                )
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
                task_status_prefix(
                    run["task"],
                    "正在准备",
                    run.get("is_current_task") is not False,
                )
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
    update_current_status_card(str(run["user_id"]))


def cli_fallback_entry(
    fallback_id: str,
    user_id: str,
    chat_id: str,
) -> dict[str, Any] | None:
    with _state_lock:
        state = load_state()
        removed = prune_cli_fallbacks(state)
        if removed:
            save_state(state)
        entry = pending_cli_fallbacks(state).get(fallback_id)
        if (
            not isinstance(entry, dict)
            or str(entry.get("user_id") or "") != user_id
            or (chat_id and str(entry.get("chat_id") or "") != chat_id)
        ):
            return None
        return dict(entry)


def remove_cli_fallback(fallback_id: str) -> dict[str, Any] | None:
    with _state_lock:
        state = load_state()
        removed = pending_cli_fallbacks(state).pop(fallback_id, None)
        if removed is not None:
            save_state(state)
        return dict(removed) if isinstance(removed, dict) else None


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
        write_runtime_status()
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


def run_timeline_kind(status: str, outcome: str | None) -> tuple[str, str]:
    if outcome in {"completed", "stopped", "failed"}:
        labels = {
            "completed": "完成并返回结果",
            "stopped": "运行已停止",
            "failed": "运行未完成",
        }
        return outcome, labels[outcome]
    mappings = (
        ("附件", "input", "读取飞书附件"),
        ("已接收", "submitted", "已提交到 Codex Desktop"),
        ("分析任务", "reasoning", "分析任务"),
        ("使用工具", "tool", "执行工具"),
        ("协调 Agent", "agent", "协调 Agent"),
        ("压缩上下文", "compact", "压缩上下文"),
        ("整理回复", "response", "整理回复"),
        ("授权", "approval", "等待授权"),
    )
    return next(
        ((kind, label) for marker, kind, label in mappings if marker in status),
        ("", ""),
    )


def append_run_timeline(
    run: dict[str, Any],
    kind: str,
    label: str,
    *,
    terminal: bool = False,
) -> bool:
    if not kind or not label:
        return False
    timeline = run.setdefault("timeline", [])
    if not isinstance(timeline, list):
        timeline = []
        run["timeline"] = timeline
    if timeline:
        last = timeline[-1]
        if (
            isinstance(last, dict)
            and last.get("kind") == kind
            and last.get("label") == label
        ):
            if terminal and last.get("state") != "done":
                last["state"] = "done"
                return True
            return False
        if isinstance(last, dict) and last.get("state") == "active":
            last["state"] = "done"
    timeline.append(
        {
            "kind": kind,
            "label": label[:80],
            "state": "done" if terminal else "active",
            "at": time.time(),
        }
    )
    run["timeline"] = timeline[-6:]
    return True


def set_run_progress(
    run: dict[str, Any],
    status: str,
    outcome: str | None = None,
    force: bool = False,
) -> None:
    now = time.time()
    with _state_lock:
        selected_id = str(
            load_state().get("selected", {}).get(str(run["user_id"])) or ""
        )
    if selected_id:
        run["is_current_task"] = selected_id == str(run["task"]["id"])
    card: dict[str, Any] | None = None
    timeline_changed = False
    with _active_runs_lock:
        run["status"] = status
        if outcome is not None:
            run["outcome"] = outcome
        kind, label = run_timeline_kind(status, outcome)
        timeline_changed = append_run_timeline(
            run,
            kind,
            label,
            terminal=outcome in {"completed", "stopped", "failed"},
        )
        message_id = str(run.get("progress_message_id") or "")
        last_patch_at = float(run.get("last_patch_at") or 0)
        if message_id and (force or now - last_patch_at >= 2):
            run["last_patch_at"] = now
            card = build_run_card(run)
    if timeline_changed and run.get("turn_id"):
        persist_recoverable_run(run)
    if message_id and card is not None:
        patch_card(message_id, card)
    task_id = str(run.get("task", {}).get("id") or "")
    current_outcome = str(run.get("outcome") or "running")
    if task_id and promlight_task_is_watched(task_id):
        mapped = (
            "human_gate"
            if current_outcome == "approval"
            else "idle"
            if current_outcome in {"completed", "stopped"}
            else "error"
            if current_outcome == "failed"
            else "unknown"
            if current_outcome == "desktop_unavailable"
            else "running"
        )
        schedule_promlight_task_status(
            task_id,
            mapped,
            "bridge_run",
            mapped != "unknown",
            str(run.get("user_id") or "") if mapped == "human_gate" else "",
        )


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


def approval_requests_from_stream_change(
    change: dict[str, Any],
) -> list[dict[str, Any]]:
    approvals: list[dict[str, Any]] = []
    seen: set[str] = set()

    def visit(value: Any) -> None:
        if isinstance(value, dict):
            approval = approval_from_request(value)
            if approval is not None and approval["request_id"] not in seen:
                seen.add(approval["request_id"])
                approvals.append(approval)
            for child in value.values():
                visit(child)
        elif isinstance(value, list):
            for child in value:
                visit(child)

    visit(change)
    return approvals


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


def remember_bridge_turn(turn_id: str, user_id: str = "") -> None:
    with _state_lock:
        state = load_state()
        turns = [str(item) for item in state.get("bridge_turns", [])]
        if turn_id not in turns:
            turns.append(turn_id)
        state["bridge_turns"] = turns[-200:]
        owners = state.setdefault("bridge_turn_owners", {})
        if not isinstance(owners, dict):
            owners = {}
        if user_id:
            owners[turn_id] = user_id
        state["bridge_turn_owners"] = {
            key: value for key, value in owners.items() if key in state["bridge_turns"]
        }
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
    on_ipc_response: Callable[[dict[str, Any]], bool] | None = None,
) -> tuple[bool, str, list[str]]:
    deadline = time.monotonic() + 3600
    cancelled_at: float | None = None
    last_elapsed_update = 0.0
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
                            if isinstance(change, dict) and on_approval is not None:
                                for approval in approval_requests_from_stream_change(change):
                                    on_approval(approval)
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
                if outer_type == "response_item" and event_type == "reasoning":
                    on_progress("正在分析任务")
                elif outer_type == "response_item" and event_type in {
                    "function_call",
                    "custom_tool_call",
                    "computer_initialize_state",
                }:
                    tool_name = str(payload.get("name") or "")
                    on_progress(
                        "正在协调 Agent"
                        if tool_name in {
                            "send_message",
                            "spawn_agent",
                            "followup_task",
                            "wait_agent",
                        }
                        else "正在使用工具"
                    )
                elif event_type == "context_compacted":
                    on_progress("正在压缩上下文")
                elif outer_type == "event_msg" and event_type in {
                    "agent_message_delta",
                    "agent_message_content_delta",
                }:
                    on_progress("正在整理回复")
            if event_type == "image_generation_end":
                image = normalized_image_reference(
                    str(payload.get("saved_path") or ""),
                    trusted_local=True,
                )
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
            image = normalized_image_reference(
                str(payload.get("saved_path") or ""),
                trusted_local=True,
            )
            if image is not None and image not in images:
                images.append(image)
    return images


def frontmost_application_bundle_id() -> str:
    try:
        result = subprocess.run(
            [
                "/usr/bin/osascript",
                "-e",
                "tell application \"System Events\" to get bundle identifier "
                "of first application process whose frontmost is true",
            ],
            capture_output=True,
            text=True,
            timeout=3,
        )
    except (OSError, subprocess.TimeoutExpired):
        return ""
    bundle_id = result.stdout.strip() if result.returncode == 0 else ""
    return bundle_id if re.fullmatch(r"[A-Za-z0-9.-]+", bundle_id) else ""


def activate_desktop_task(thread_id: str) -> bool:
    try:
        normalized_thread_id = str(uuid.UUID(thread_id))
    except ValueError:
        log("desktop task activation failed reason=invalid-task-id")
        return False
    if normalized_thread_id != thread_id.lower():
        log("desktop task activation failed reason=noncanonical-task-id")
        return False

    previous_bundle_id = frontmost_application_bundle_id()
    try:
        opened = subprocess.run(
            [
                "/usr/bin/open",
                "-g",
                f"codex://threads/{normalized_thread_id}",
            ],
            capture_output=True,
            text=True,
            timeout=5,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        log(f"desktop task activation failed reason={type(exc).__name__}")
        return False
    if opened.returncode != 0:
        log("desktop task activation failed reason=open-command")
        return False

    time.sleep(DESKTOP_TASK_ACTIVATION_SETTLE_SECONDS)
    if previous_bundle_id and previous_bundle_id != "com.openai.codex":
        try:
            restored = subprocess.run(
                ["/usr/bin/open", "-b", previous_bundle_id],
                capture_output=True,
                text=True,
                timeout=5,
            )
            if restored.returncode != 0:
                log("desktop task activation focus restore failed")
        except (OSError, subprocess.TimeoutExpired) as exc:
            log(f"desktop task activation focus restore failed: {type(exc).__name__}")
    log("desktop task activation requested")
    return True


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
    if not DESKTOP_IPC_SOCKET.exists():
        log("desktop unavailable reason=ipc-socket-missing")
        return "unavailable", "ipc-socket-missing", []
    if not rollout_path or not rollout_path.exists():
        log("desktop unavailable reason=task-record-missing")
        return "unavailable", "task-record-missing", []
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
                    log("desktop unavailable reason=no-client-found")
                    return "unavailable", "no-client-found", []
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
        reason = "ipc-timeout" if isinstance(exc, socket.timeout) else "ipc-connect-failed"
        log(f"desktop unavailable reason={reason}")
        return "unavailable", reason, []
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
        log("desktop unavailable reason=ipc-initialize-failed")
        return "unavailable", "ipc-initialize-failed", []

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
    use_cli_fallback: bool = False,
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

    if not use_cli_fallback:
        activation_attempted = False
        for attempt in range(len(DESKTOP_UNAVAILABLE_RETRY_DELAYS) + 1):
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
            if (
                desktop_status != "unavailable"
                or desktop_result not in {"no-client-found", "ipc-connect-failed"}
                or attempt >= len(DESKTOP_UNAVAILABLE_RETRY_DELAYS)
            ):
                break
            if cancel_event is not None and cancel_event.is_set():
                if cancel_confirmed is not None:
                    cancel_confirmed.set()
                return False, "已按你的要求停止运行。", []
            if desktop_result == "no-client-found" and not activation_attempted:
                activation_attempted = True
                if not activate_desktop_task(thread_id):
                    break
            log(
                f"desktop unavailable retry reason={desktop_result} "
                f"attempt={attempt + 1}"
            )
            time.sleep(DESKTOP_UNAVAILABLE_RETRY_DELAYS[attempt])
        if desktop_status == "completed":
            return True, desktop_result, desktop_images
        if desktop_status == "failed":
            return False, desktop_result, []
        raise DesktopUnavailableError(desktop_result or "unknown")
    log("cli fallback explicitly confirmed by user")

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
        notify_started("正在通过备用 Codex CLI 启动")
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


def processed_event_seen(
    state: dict[str, Any],
    key: str,
    now: float | None = None,
) -> bool:
    timestamp = time.time() if now is None else now
    recent = state.get("processed_events")
    if isinstance(recent, dict):
        try:
            recorded_at = float(recent.get(key) or 0)
        except (TypeError, ValueError):
            recorded_at = 0
        if recorded_at and timestamp - recorded_at <= PROCESSED_EVENT_TTL_SECONDS:
            return True
    processed = state.get("processed")
    return isinstance(processed, list) and key in processed


def mark_processed(
    state: dict[str, Any],
    key: str,
    now: float | None = None,
) -> bool:
    with _state_lock:
        timestamp = time.time() if now is None else now
        current = load_state()
        if processed_event_seen(current, key, timestamp):
            return False
        recent = current.get("processed_events")
        if not isinstance(recent, dict):
            recent = {}
        recent = {
            str(event_key): float(recorded_at)
            for event_key, recorded_at in recent.items()
            if isinstance(event_key, str)
            and isinstance(recorded_at, (int, float))
            and timestamp - float(recorded_at) <= PROCESSED_EVENT_TTL_SECONDS
        }
        recent[key] = timestamp
        if len(recent) > MAX_PROCESSED_EVENTS:
            recent = dict(
                sorted(recent.items(), key=lambda item: item[1])[-MAX_PROCESSED_EVENTS:]
            )
        current["processed_events"] = recent
        current["processed"] = list(recent)[-200:]
        save_state(current)
        if state is not current:
            state.clear()
            state.update(current)
        return True


def set_run_turn_id(run: dict[str, Any], turn_id: str) -> None:
    with _active_runs_lock:
        run["turn_id"] = turn_id
    remember_bridge_turn(turn_id, str(run.get("user_id") or ""))
    persist_recoverable_run(run)
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
    entry["queue_reason"] = "desktop_task_busy"
    entry["is_current_task"] = run.get("is_current_task") is not False
    entry["max_concurrent_runs"] = MAX_CONCURRENT_RUNS
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
            build_queued_card(entry, position),
        )
    update_current_status_card(str(run["user_id"]))
    log("input queued reason=desktop-task-busy")
    return True


def message_run_started(run: dict[str, Any], status: str) -> None:
    invalidate_cli_fallbacks(
        str(run["user_id"]),
        str(run["task"]["id"]),
    )
    set_run_progress(run, status, "running", force=True)


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
                on_started=lambda status: message_run_started(run, status),
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
                use_cli_fallback=run.get("use_cli_fallback") is True,
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
    except DesktopUnavailableError as exc:
        success = False
        result = str(exc)
        rollout_images = []
        try:
            fallback_id = remember_cli_fallback(
                run,
                prompt,
                image_keys,
                file_keys,
                raw_content,
                message_type,
                exc.reason,
            )
        except (OSError, RuntimeError, ValueError) as state_exc:
            log(f"cli fallback state failed: {type(state_exc).__name__}")
        else:
            run["fallback_id"] = fallback_id
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
        waiting_for_choice = bool(run.get("fallback_id"))
        outcome = (
            "stopped"
            if stopped
            else "desktop_unavailable"
            if waiting_for_choice
            else "completed"
            if success
            else "failed"
        )
        status = (
            "已停止"
            if stopped
            else "等待你选择执行方式"
            if waiting_for_choice
            else "停止未确认"
            if stop_requested
            else "正在发送结果"
            if success
            else "运行未完成"
        )
        label = (
            "已停止"
            if stopped
            else "等待选择"
            if waiting_for_choice
            else "已完成"
            if success
            else "未完成"
        )
        allowed_roots = result_roots_for_task(task)
        clean_result, audio_files = prepare_result_audio(result, allowed_roots)
        clean_result, images = prepare_result_images(
            clean_result,
            rollout_images,
            allowed_roots,
        )
        clean_result, files = prepare_result_files(clean_result, allowed_roots)
        if not waiting_for_choice:
            record_task_exchange(
                str(run["user_id"]),
                str(task["id"]),
                answer=clean_result,
                completed_at=time.time(),
            )
        user_id = str(run["user_id"])
        followed_result = False
        with result_delivery_lock(user_id):
            if outcome in {"completed", "stopped", "failed"}:
                followed_result = follow_result_task(user_id, task)
                run["is_current_task"] = task_is_current(
                    user_id,
                    str(task["id"]),
                )
            set_run_progress(run, status, outcome, force=True)
            prefix = task_status_prefix(
                task,
                label,
                run["is_current_task"] is not False,
            )
            delivered = reply_or_queue(
                message_id,
                prefix + clean_result,
                "desktop-unavailable" if waiting_for_choice else "final",
            )
        if followed_result:
            schedule_user_task_identity_refresh(
                user_id,
                "当前 Task 已跟随最新结果",
                task,
            )
        failed_images, failed_audio, failed_files = deliver_result_resources(
            message_id,
            images,
            audio_files,
            files,
            notify_failures=True,
        )
        if success:
            delivery_status = (
                "已完成，结果已返回飞书"
                if delivered
                and not failed_images
                and not failed_audio
                and not failed_files
                else "已完成，部分结果等待自动补发"
            )
            set_run_progress(run, delivery_status, "completed", force=True)
    finally:
        task_id = str(run["task"]["id"])
        remove_recoverable_run(str(run.get("turn_id") or ""))
        remove_active_run(str(run["run_id"]))
        try:
            update_current_status_card(
                str(run["user_id"]),
                task=run["task"],
            )
        except Exception as exc:
            log(
                "current status refresh failed after run: "
                f"{type(exc).__name__}"
            )
        start_next_queued_input(task_id)


def _handle_message_event_once(event: dict[str, Any]) -> None:
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
        if processed_event_seen(state, message_id):
            return

    pending_rename = str(promlight_state(state)["pending_renames"].get(user_id) or "")
    if pending_rename and message_type == "text" and not image_keys and not file_keys:
        try:
            rename_promlight(user_id, pending_rename, content)
            change = "提示灯名称已更新。"
        except (PermissionError, ValueError) as exc:
            change = str(exc)
        with _state_lock:
            state = load_state()
            promlight_state(state)["pending_renames"].pop(user_id, None)
            save_state(state)
            card = build_promlight_control_card(user_id, state)
        reply_card(message_id, card, "promlight-renamed")
        log(f"promlight rename handled result={'updated' if change.startswith('提示灯') else 'rejected'}")
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
        update_current_status_card(user_id, ensure=True)
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
        selection_reply = select_task(user_id, match.group(1).strip(), state)
        reply(
            message_id,
            selection_reply,
            "select",
        )
        if selection_reply.startswith("✅"):
            schedule_user_task_identity_refresh(user_id, "当前 Task 已切换")
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
            "尚未选择 Codex task。请点击机器人菜单中的“切换 Task”。",
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
    record_task_exchange(
        user_id,
        str(task["id"]),
        question=content or "发送了附件",
    )
    run["is_current_task"] = True
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
            "queue_reason": str(run.get("queue_reason") or "same_task"),
            "active_run_count": int(run.get("active_run_count") or 1),
            "max_concurrent_runs": MAX_CONCURRENT_RUNS,
            "is_current_task": run.get("is_current_task") is not False,
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
                task_status_prefix(
                    task,
                    f"已排队（第 {position} 条）",
                    entry.get("is_current_task") is not False,
                )
                + "当前运行完成后会自动执行。",
                "task-queued",
            )
        log(f"input queued position={position} attachments={len(image_keys) + len(file_keys)}")
        update_current_status_card(user_id)
        start_next_queued_input(str(task["id"]))
        return
    start_claimed_run(run, content, image_keys, file_keys, raw_content, message_type)


def handle_message_event(event: dict[str, Any]) -> None:
    message_id = str(event.get("message_id") or "")
    if message_id:
        with _state_lock:
            if processed_event_seen(load_state(), message_id):
                return
    _handle_message_event_once(event)
    if message_id:
        mark_processed(load_state(), message_id)


def _handle_card_event_once(event: dict[str, Any]) -> None:
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

    if action_tag == "select_static" and handle_promlight_selector_action(event):
        return

    if action_tag == "button":
        payload = action_payload(event)
        action = str(payload.get("action") or "")
        if workflow_notifications_enabled() and handle_workflow_card_action(event, payload):
            return
        if handle_promlight_button_action(event, payload):
            return
        if action in {"refresh_task_settings", "compact_current_task"}:
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
            task_id = str(payload.get("task_id") or "")
            task, error = task_settings_for_current_user(
                user_id,
                task_id,
                require_idle=action == "compact_current_task",
            )
            if task is None:
                fallback = task_by_id(task_id, user_id)
                if fallback is not None and message_id:
                    card = (
                        build_compact_task_context_card(fallback, error)
                        if action == "compact_current_task"
                        else build_task_settings_card(fallback, status=error)
                    )
                    patch_card(message_id, card)
                return
            if not ui_intent_is_current(event):
                log(f"card intent skipped action={action} reason=superseded")
                return
            loading_status = (
                "正在启动上下文压缩"
                if action == "compact_current_task"
                else "正在读取 Codex Desktop 设置"
            )
            loading_card = (
                build_compact_task_context_card(task, loading_status, loading=True)
                if action == "compact_current_task"
                else build_task_settings_card(task, status=loading_status, loading=True)
            )
            token = str(event.get("token") or "")
            if not (token and update_card(token, loading_card)) and message_id:
                patch_card(message_id, loading_card)
            target = (
                complete_task_context_compaction
                if action == "compact_current_task"
                else refresh_task_settings_card
            )
            threading.Thread(
                target=target,
                args=(user_id, message_id, task_id),
                daemon=True,
                name=f"codex-feishu-task-settings-{task_id[:8]}",
            ).start()
            return
        if action in {
            "show_task_subscriptions",
            "toggle_task_subscription",
            "clear_task_subscriptions",
            "task_subscription_page",
        }:
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
            change = ""
            if action == "toggle_task_subscription":
                requested_task_id = str(payload.get("task_id") or "")
                task = task_by_id(requested_task_id, user_id)
                if task is None:
                    change = "该 Task 已归档、删除或不再属于你的授权项目"
                else:
                    with _state_lock:
                        state = load_state()
                        state.setdefault("subscription_selected_tasks", {})[
                            user_id
                        ] = requested_task_id
                        state.setdefault("subscription_last_projects", {})[
                            user_id
                        ] = task["project"]
                        save_state(state)
                    _subscribed, change = toggle_task_subscription(user_id, task)
            elif action == "clear_task_subscriptions":
                with _state_lock:
                    state = load_state()
                    user_task_subscriptions(state, user_id).clear()
                    save_state(state)
                change = "已取消全部 Task 订阅"
            elif action == "task_subscription_page":
                try:
                    page = max(0, int(payload.get("page") or 0))
                except (TypeError, ValueError):
                    page = 0
                with _state_lock:
                    state = load_state()
                    state.setdefault("subscription_task_pages", {})[user_id] = page
                    save_state(state)
            with _state_lock:
                state = load_state()
                card = task_subscriptions_card_for_state(user_id, state, change)
                if message_id:
                    remember_card_context(
                        state,
                        user_id,
                        message_id,
                        card,
                        "task_subscriptions",
                    )
            token = str(event.get("token") or "")
            if token and update_card(token, card):
                return
            if message_id:
                patch_card(message_id, card)
            return
        if action in {"confirm_desktop_sync", "cancel_desktop_sync"}:
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
                task = selected_task(user_id, state)
            if action == "cancel_desktop_sync":
                if message_id:
                    patch_card(message_id, build_desktop_sync_canceled_card(task))
                log("card handled action=cancel_desktop_sync result=canceled")
                return
            requested_task_id = str(payload.get("task_id") or "")
            if task is None:
                card = task_card_for_state(user_id, state)
                if message_id:
                    remember_card_context(state, user_id, message_id, card)
                    patch_card(message_id, card)
                log("card handled action=confirm_desktop_sync result=task-selector")
                return
            snapshot = latest_rollout_turn(
                rollout_path_for_task(str(task["id"]))
            )
            if requested_task_id != str(task["id"]):
                if message_id:
                    patch_card(
                        message_id,
                        build_desktop_sync_confirmation_card(
                            task,
                            str(snapshot.get("status") or "none"),
                            current_changed=True,
                        ),
                    )
                log("card handled action=confirm_desktop_sync result=current-changed")
                return
            confirm_desktop_sync(
                user_id,
                task,
                event_id,
                message_id,
                chat_id,
            )
            return
        if action in {"retry_desktop", "use_cli_fallback", "cancel_cli_fallback"}:
            fallback_id = str(payload.get("fallback_id") or "")
            entry = cli_fallback_entry(fallback_id, user_id, chat_id)
            if entry is None:
                reply(
                    message_id,
                    "这个执行选择已失效或已经处理，请重新发送消息。",
                    f"cli-fallback-stale-{event_id}",
                )
                return
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
            if action == "cancel_cli_fallback":
                if remove_cli_fallback(fallback_id) is None:
                    return
                canceled = new_run(
                    user_id,
                    chat_id,
                    str(entry["source_message_id"]),
                    entry["task"],
                    list(entry.get("image_keys") or []),
                    list(entry.get("file_keys") or []),
                    message_id or str(entry.get("progress_message_id") or ""),
                )
                canceled["status"] = "已取消，本条消息未提交"
                canceled["outcome"] = "stopped"
                if message_id:
                    patch_card(message_id, build_run_card(canceled))
                update_current_status_card(user_id)
                log("cli fallback canceled by user")
                return
            try:
                task = task_by_id(str(entry["task"]["id"]), user_id)
            except (OSError, sqlite3.Error):
                task = None
            if task is None:
                remove_cli_fallback(fallback_id)
                reply(
                    message_id,
                    "这个 Task 已归档、删除或当前用户已无权访问，本条消息没有提交。",
                    f"cli-fallback-task-missing-{event_id}",
                )
                return
            run = new_run(
                user_id,
                chat_id,
                str(entry["source_message_id"]),
                task,
                list(entry.get("image_keys") or []),
                list(entry.get("file_keys") or []),
                message_id or str(entry.get("progress_message_id") or ""),
            )
            run["status"] = (
                "已确认，准备使用备用 Codex CLI"
                if action == "use_cli_fallback"
                else "正在重新连接 Codex Desktop"
            )
            run["use_cli_fallback"] = action == "use_cli_fallback"
            if action == "retry_desktop":
                run["outcome"] = "desktop_retrying"
            if not claim_active_run(run):
                reply(
                    message_id,
                    "这个 Task 当前正在运行，请完成后再次点击。",
                    f"cli-fallback-busy-{event_id}",
                )
                return
            if remove_cli_fallback(fallback_id) is None:
                remove_active_run(str(run["run_id"]))
                return
            start_claimed_run(
                run,
                str(entry.get("content") or ""),
                list(entry.get("image_keys") or []),
                list(entry.get("file_keys") or []),
                str(entry.get("raw_content") or ""),
                str(entry.get("message_type") or "text"),
            )
            return
        if action == "cancel_queued_input":
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
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
            schedule_queued_card_refresh(task_id)
            schedule_user_task_identity_refresh(user_id)
            log("queued input canceled")
            return
        if action == "refresh_current_status":
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
            update_current_status_card(user_id, ensure=True)
            return
        if action == "refresh_codex_usage":
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
            if message_id:
                patch_card(
                    message_id,
                    build_codex_usage_card(
                        codex_usage_snapshot(),
                        "正在刷新用量",
                    ),
                )
            threading.Thread(
                target=refresh_codex_usage_card,
                args=(message_id,),
                daemon=True,
            ).start()
            return
        if action == "show_codex_usage":
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
            if message_id:
                patch_card(message_id, build_codex_usage_card(codex_usage_snapshot()))
            return
        if action in {
            "show_daily_task_usage_analysis",
            "show_period_task_usage_analysis",
        }:
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
            scope = "daily" if action == "show_daily_task_usage_analysis" else "period"
            if message_id:
                patch_card(message_id, build_task_usage_loading_card(scope))
            threading.Thread(
                target=refresh_task_usage_analysis_card,
                args=(user_id, message_id, scope),
                daemon=True,
            ).start()
            return
        if action == "cancel_task_switch":
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
                task = selected_task(user_id, state)
                context_type = card_context_for_event(state, user_id, message_id)
                requested_task_id = str(payload.get("task_id") or "")
                current_changed = bool(
                    task is not None
                    and requested_task_id
                    and requested_task_id != str(task["id"])
                )
                if task is not None:
                    state.setdefault("last_projects", {})[user_id] = task["project"]
                    state.setdefault("task_pages", {})[user_id] = 0
                    state.setdefault("task_queries", {}).pop(user_id, None)
                    save_state(state)
            if task is None:
                card = task_card_with_notice(
                    task_card_for_state(user_id, state),
                    "当前没有可保留的 Task，请先选择一个 Task",
                )
                context_type_override = ""
            elif context_type == "desktop_sync_selection":
                snapshot = latest_rollout_turn(
                    rollout_path_for_task(str(task["id"]))
                )
                card = build_desktop_sync_confirmation_card(
                    task,
                    str(snapshot.get("status") or "none"),
                    current_changed=current_changed,
                )
                context_type_override = "desktop_sync_confirmation"
            else:
                card = build_task_switch_canceled_card(task)
                if current_changed:
                    card = task_card_with_notice(
                        card,
                        "当前 Task 已变化；本次没有执行新的切换",
                    )
                context_type_override = ""
            if message_id:
                with _state_lock:
                    state = load_state()
                    if context_type_override:
                        remember_card_context(
                            state,
                            user_id,
                            message_id,
                            card,
                            context_type_override,
                        )
                    else:
                        contexts = state.get("card_contexts", {})
                        if isinstance(contexts, dict):
                            contexts.pop(message_id, None)
                            save_state(state)
            token = str(event.get("token") or "")
            if token and update_card(token, card):
                log("card handled action=cancel_task_switch")
                return
            if message_id:
                patch_card(message_id, card)
            log("card handled action=cancel_task_switch")
            return
        if action == "new_task":
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
                requested_project = str(payload.get("project") or "")
                projects = set(available_project_names(user_id))
                latest_project = str(
                    state.setdefault("last_projects", {}).get(user_id) or ""
                )
                project = latest_project if latest_project in projects else requested_project
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
        if action in {"archive_task", "restore_task"}:
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
                current_task = selected_task(user_id, state)
            requested_task_id = str(payload.get("task_id") or "")
            if action == "restore_task":
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
            else:
                task = current_task
                if task is None:
                    reply(
                        message_id,
                        "尚未选择 Task，请先点击机器人菜单中的“切换 Task”。",
                        f"archive-no-task-{event_id}",
                    )
                    return
                if requested_task_id and requested_task_id != str(task["id"]):
                    reply(
                        message_id,
                        "当前 Task 已经变化，请重新点击 TASK →“归档当前 Task”。",
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
            processing_card = build_archive_task_card(
                task,
                processing="restore" if action == "restore_task" else "archive",
            )
            token = str(event.get("token") or "")
            if not (token and update_card(token, processing_card)) and message_id:
                patch_card(message_id, processing_card)
            started = time.monotonic()
            try:
                if action == "restore_task":
                    restore_codex_task(user_id, task)
                else:
                    archive_codex_task(user_id, task)
            except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
                log(f"task mutation failed action={action} error={type(exc).__name__}")
                if message_id:
                    patch_card(
                        message_id,
                        task_card_with_notice(
                            build_archive_task_card(
                                task,
                                archived=action == "restore_task",
                            ),
                            "操作失败，当前状态没有改变，请稍后重试",
                        ),
                    )
                reply(
                    message_id,
                    (
                        "没有成功恢复 Task，请在 Codex Desktop 中重试。"
                        if action == "restore_task"
                        else "没有成功归档 Task，请在 Codex Desktop 中重试。"
                    ),
                    f"{action}-error-{event_id}",
                )
                return
            log(
                f"latency desktop operation={action} "
                f"duration_ms={round((time.monotonic() - started) * 1000)} success=true"
            )
            with _state_lock:
                state = load_state()
                if action == "restore_task":
                    state.setdefault("selected", {})[user_id] = task["id"]
                    state.setdefault("last_projects", {})[user_id] = task["project"]
                    remember_recent_task(state, user_id, str(task["id"]))
                    state.setdefault("task_pages", {})[user_id] = 0
                    final_card = build_archive_task_card(task, restored=True)
                else:
                    if str(state.setdefault("selected", {}).get(user_id) or "") == str(task["id"]):
                        state["selected"].pop(user_id, None)
                    final_card = build_archive_task_card(task, archived=True)
                save_state(state)
            if message_id:
                patch_card(message_id, final_card)
            elif token:
                update_card(token, final_card)
            reply(
                message_id,
                (
                    current_task_changed_text(task, "已恢复")
                    if action == "restore_task"
                    else f"已归档：{option_text(task)}"
                ),
                f"{action}-done-{event_id}",
            )
            if action == "restore_task":
                schedule_user_task_identity_refresh(user_id, "当前 Task 已恢复", task)
            return
        if action in {
            "task_page",
            "archived_task_page",
            "refresh_task_list",
            "refresh_archived_tasks",
            "toggle_task_favorite",
            "clear_task_search",
            "cancel_new_task",
            "cancel_archive",
            "show_task_selector",
            "show_desktop_sync_task_selector",
            "show_archived_tasks",
            "show_new_task",
        }:
            status_change = ""
            context_type_override = ""
            favorite_stale = False
            with _state_lock:
                state = load_state()
                if processed_event_seen(state, f"card:{event_id}"):
                    return
                if action == "toggle_task_favorite":
                    task = selected_task(user_id, state)
                    requested_task_id = str(payload.get("task_id") or "")
                    if task is None or requested_task_id != str(task["id"]):
                        favorite_stale = True
                    else:
                        favorites = state.setdefault("favorite_task_ids", {}).get(user_id, [])
                        if not isinstance(favorites, list):
                            favorites = []
                        task_id = str(task["id"])
                        if task_id in favorites:
                            favorites = [value for value in favorites if str(value) != task_id]
                            status_change = "已取消收藏当前 Task"
                        else:
                            favorites = [task_id] + [
                                str(value) for value in favorites if str(value) != task_id
                            ]
                            status_change = "已收藏当前 Task"
                        state.setdefault("favorite_task_ids", {})[user_id] = favorites
                        save_state(state)
                        card = (
                            current_status_card_for_user(user_id, task, status_change)
                            if payload.get("return_to") == "status"
                            else task_card_for_state(user_id, state)
                        )
                elif action == "refresh_task_list":
                    card = task_card_for_state(user_id, state)
                elif action == "refresh_archived_tasks":
                    card = archived_task_card_for_state(user_id, state)
                elif action == "task_page":
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
                elif action == "show_desktop_sync_task_selector":
                    save_state(state)
                    card = task_card_for_state(user_id, state)
                    context_type_override = "desktop_sync_selection"
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
                elif action == "cancel_archive":
                    task = selected_task(user_id, state)
                    save_state(state)
                    card = build_archive_task_card(task, canceled=True)
                    log("task archive canceled")
            token = str(event.get("token") or "")
            if favorite_stale:
                reply(
                    message_id,
                    "当前 Task 已变化，请刷新卡片后再操作收藏。",
                    f"favorite-stale-{event_id}",
                )
                return
            if message_id:
                with _state_lock:
                    current_state = load_state()
                    remember_card_context(
                        current_state,
                        user_id,
                        message_id,
                        card,
                        context_type_override,
                    )
            if not ui_intent_is_current(event):
                log(f"card intent skipped action={action} reason=superseded")
                return
            if token and update_card(token, card):
                if status_change:
                    schedule_user_task_identity_refresh(user_id, status_change, task)
                return
            if message_id:
                patch_card(message_id, card)
            if status_change:
                schedule_user_task_identity_refresh(user_id, status_change, task)
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
            if processed_event_seen(state, f"card:{event_id}"):
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
        context_state = load_state()
        context_details = card_context_details(context_state, user_id, message_id)
        context_type = str(context_details.get("type") or "")
    elements = (
        original_card.get("body", {}).get("elements", [])
        if isinstance(original_card, dict)
        else []
    )
    recognized_task_settings_card = any(
        isinstance(element, dict)
        and element.get("tag") == "select_static"
        and element.get("name")
        in {"task_model_selector", "task_effort_selector", "task_speed_selector"}
        for element in elements
    ) or context_type == "task_settings"
    if action_tag == "select_static" and (
        action_name
        in {"task_model_selector", "task_effort_selector", "task_speed_selector"}
        or (not action_name and recognized_task_settings_card)
    ):
        selectors = {
            str(selector.get("name") or ""): selector
            for selector in card_selector_context(original_card or {}).get("selectors", [])
            if isinstance(selector, dict)
        }
        if not action_name:
            matching = [
                name
                for name in (
                    "task_model_selector",
                    "task_effort_selector",
                    "task_speed_selector",
                )
                if selected_value in selectors.get(name, {}).get("options", [])
            ]
            if len(matching) != 1:
                log("card ignored reason=unknown-task-settings-selector")
                return
            action_name = matching[0]
        if selected_value not in selectors.get(action_name, {}).get("options", []):
            log("card ignored reason=invalid-task-setting-option")
            return
        task_id = task_settings_card_task_id(original_card) or str(
            context_details.get("task_id") or ""
        )
        task, error = task_settings_for_current_user(
            user_id,
            task_id,
        )
        if task is None:
            fallback = task_by_id(task_id, user_id)
            if fallback is not None and message_id:
                patch_card(message_id, build_task_settings_card(fallback, status=error))
            return
        if not ui_intent_is_current(event):
            log(f"card intent skipped action={action_name} reason=superseded")
            return
        loading_card = build_task_settings_card(
            task,
            status=(
                "正在更新模型"
                if action_name == "task_model_selector"
                else "正在更新分析强度"
                if action_name == "task_effort_selector"
                else "正在更新速度"
            ),
            loading=True,
        )
        token = str(event.get("token") or "")
        if not (token and update_card(token, loading_card)) and message_id:
            patch_card(message_id, loading_card)
        threading.Thread(
            target=complete_task_settings_operation,
            args=(user_id, message_id, task_id),
            kwargs=(
                {"model": selected_value}
                if action_name == "task_model_selector"
                else {"effort": selected_value}
                if action_name == "task_effort_selector"
                else {"service_tier": selected_value}
            ),
            daemon=True,
            name=f"codex-feishu-task-settings-{task_id[:8]}",
        ).start()
        return
    recognized_subscription_card = any(
        isinstance(element, dict)
        and element.get("tag") == "select_static"
        and element.get("name")
        in {"subscription_project_selector", "subscription_task_selector"}
        for element in elements
    ) or context_type == "task_subscriptions"
    if action_tag == "select_static" and (
        action_name in {"subscription_project_selector", "subscription_task_selector"}
        or (not action_name and recognized_subscription_card)
    ):
        tasks = recent_tasks(user_id)
        projects = {task["project"] for task in tasks}
        task_by_value = {task["id"]: task for task in tasks}
        if not action_name:
            if selected_value in projects:
                action_name = "subscription_project_selector"
            elif selected_value in task_by_value:
                action_name = "subscription_task_selector"
            else:
                log("card ignored reason=unknown-subscription-selector")
                return
        stale_selection = False
        with _state_lock:
            state = load_state()
            if chat_id and not is_authorized_chat(state, user_id, chat_id):
                if not recognized_subscription_card:
                    log("card ignored reason=unrecognized-chat-and-card")
                    return
                authorize_chat(state, user_id, chat_id)
            if processed_event_seen(state, f"card:{event_id}"):
                return
            if action_name == "subscription_project_selector":
                if selected_value not in projects:
                    return
                state.setdefault("subscription_last_projects", {})[
                    user_id
                ] = selected_value
                state.setdefault("subscription_task_pages", {})[user_id] = 0
                first = next(
                    (task for task in tasks if task["project"] == selected_value),
                    None,
                )
                if first:
                    state.setdefault("subscription_selected_tasks", {})[
                        user_id
                    ] = first["id"]
            else:
                task = task_by_value.get(selected_value)
                latest_project = str(
                    state.setdefault("subscription_last_projects", {}).get(user_id)
                    or ""
                )
                source_project = (
                    subscription_card_active_project(original_card)
                    or str(context_details.get("project") or "")
                )
                if (
                    task is None
                    or (
                        latest_project
                        and (
                            task["project"] != latest_project
                            or (source_project and source_project != latest_project)
                        )
                    )
                ):
                    stale_selection = True
                else:
                    state.setdefault("subscription_selected_tasks", {})[
                        user_id
                    ] = task["id"]
                    state.setdefault("subscription_last_projects", {})[
                        user_id
                    ] = task["project"]
            save_state(state)
            card = task_subscriptions_card_for_state(
                user_id,
                state,
                "项目已经切换，请在新列表中重新选择" if stale_selection else "",
            )
            if message_id:
                remember_card_context(
                    state,
                    user_id,
                    message_id,
                    card,
                    "task_subscriptions",
                )
        if not ui_intent_is_current(event):
            return
        token = str(event.get("token") or "")
        if token and update_card(token, card):
            return
        if message_id:
            patch_card(message_id, card)
        return

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
            if processed_event_seen(state, f"card:{event_id}"):
                return
            state.setdefault("last_projects", {})[user_id] = selected_value
            save_state(state)
            card = build_new_task_card(projects, selected_value)
            if message_id:
                remember_card_context(state, user_id, message_id, card)
        if not ui_intent_is_current(event):
            log("card intent skipped action=new_task_project_selector reason=superseded")
            return
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
        stale_archived_selection = False
        with _state_lock:
            state = load_state()
            if chat_id and not is_authorized_chat(state, user_id, chat_id):
                if not recognized_archived_card:
                    log("card ignored reason=unrecognized-chat-and-card")
                    return
                authorize_chat(state, user_id, chat_id)
            if processed_event_seen(state, f"card:{event_id}"):
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
                    stale_archived_selection = True
                else:
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
                if not stale_archived_selection:
                    remember_card_context(state, user_id, message_id, card)
        if stale_archived_selection:
            if message_id:
                reply(
                    message_id,
                    "该 Task 已经恢复或删除，请重新选择。",
                    f"archived-stale-{event_id}",
                )
            return
        if not ui_intent_is_current(event):
            log(f"card intent skipped action={action_name} reason=superseded")
            return
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
        and element.get("name")
        in {"project_selector", "task_selector", "task_scope_selector"}
        for element in elements
    ) or context_type in {"tasks", "desktop_sync_selection"}
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
    if action_name not in {"project_selector", "task_selector", "task_scope_selector"}:
        projects = {task["project"] for task in tasks}
        task_ids = {task["id"] for task in tasks}
        if recognized_card and selected_value in projects:
            action_name = "project_selector"
        elif recognized_card and selected_value in task_ids:
            action_name = "task_selector"
        elif recognized_card and selected_value in {"all", "recent", "favorites"}:
            action_name = "task_scope_selector"
        else:
            log(
                "card ignored reason=unknown-selector "
                f"name={action_name or 'missing'} card_recognized={recognized_card}"
            )
            return
        log(f"card selector inferred name={action_name}")
    stale_selection = False
    missing_task_selection = False
    with _state_lock:
        state = load_state()
        context_type_override = (
            "desktop_sync_selection"
            if context_type == "desktop_sync_selection"
            else ""
        )
        if chat_id and not is_authorized_chat(state, user_id, chat_id):
            if not recognized_card:
                log("card ignored reason=unrecognized-chat-and-card")
                return
            authorize_chat(state, user_id, chat_id)
        if processed_event_seen(state, f"card:{event_id}"):
            return
        if action_name == "task_scope_selector":
            if selected_value not in {"all", "recent", "favorites"}:
                log("card ignored reason=unknown-task-scope")
                return
            state.setdefault("task_scopes", {})[user_id] = selected_value
            state.setdefault("task_pages", {})[user_id] = 0
            selected = selected_task(user_id, state)
            save_state(state)
            card = task_card_for_state(user_id, state)
        elif action_name == "project_selector":
            projects = {task["project"] for task in tasks}
            if selected_value not in projects:
                log("card ignored reason=unknown-project")
                return
            state.setdefault("last_projects", {})[user_id] = selected_value
            state.setdefault("task_pages", {})[user_id] = 0
            state.setdefault("task_queries", {}).pop(user_id, None)
            selected = selected_task(user_id, state)
            save_state(state)
            card = task_card_for_state(user_id, state)
        else:
            selected = next((task for task in tasks if task["id"] == selected_value), None)
            if selected is None:
                missing_task_selection = True
            else:
                latest_project = str(
                    state.setdefault("last_projects", {}).get(user_id) or ""
                )
                source_project = (
                    card_active_project(original_card)
                    or str(context_details.get("project") or "")
                )
                if (
                    latest_project
                    and (
                        str(selected["project"]) != latest_project
                        or (source_project and source_project != latest_project)
                    )
                ):
                    card = task_card_with_notice(
                        task_card_for_state(user_id, state),
                        "项目已经切换，Task 列表已刷新，请在新列表中重新选择",
                    )
                    stale_selection = True
                else:
                    state.setdefault("selected", {})[user_id] = selected["id"]
                    state.setdefault("last_projects", {})[user_id] = selected["project"]
                    remember_recent_task(state, user_id, str(selected["id"]))
                    save_state(state)
                    if context_type == "desktop_sync_selection":
                        snapshot = latest_rollout_turn(
                            rollout_path_for_task(str(selected["id"]))
                        )
                        card = build_desktop_sync_confirmation_card(
                            selected,
                            str(snapshot.get("status") or "none"),
                            selected_from_list=True,
                        )
                        context_type_override = "desktop_sync_confirmation"
                    else:
                        card = task_card_for_state(
                            user_id,
                            state,
                            selection_changed=True,
                            selected_id_override=str(selected["id"]),
                        )
        if message_id:
            if not missing_task_selection:
                remember_card_context(
                    state,
                    user_id,
                    message_id,
                    card,
                    context_type_override,
                )
    if missing_task_selection:
        if message_id:
            reply(message_id, "该 Task 已归档或删除，请重新选择。", f"stale-{event_id}")
        return
    visible_count = (
        len([task for task in tasks if task["project"] == selected_value])
        if action_name == "project_selector"
        else len(tasks)
    )
    log(
        f"card selection saved name={action_name} "
        f"visible_tasks={visible_count} stale={str(stale_selection).lower()} "
        f"update_token={bool(event.get('token'))}"
    )
    if not ui_intent_is_current(event):
        log(f"card intent skipped action={action_name} reason=superseded")
        return
    token = str(event.get("token") or "")
    if token and update_card(token, card):
        log(f"card selection updated name={action_name}")
        if action_name == "task_selector" and not stale_selection:
            schedule_user_task_identity_refresh(user_id, "当前 Task 已切换", selected)
        return
    if message_id:
        patch_card(message_id, card)
    if action_name == "task_selector" and not stale_selection:
        schedule_user_task_identity_refresh(user_id, "当前 Task 已切换", selected)


def handle_card_event(event: dict[str, Any]) -> None:
    event_id = str(event.get("event_id") or "")
    processed_key = f"card:{event_id}" if event_id else ""
    if processed_key:
        with _state_lock:
            if processed_event_seen(load_state(), processed_key):
                return
    _handle_card_event_once(event)
    if processed_key:
        mark_processed(load_state(), processed_key)


def _handle_menu_event_once(event: dict[str, Any]) -> None:
    user_id = str(event.get("operator_id") or "")
    event_key = str(event.get("event_key") or "")
    if event_key not in {
        CURRENT_TASK_MENU_EVENT_KEY,
        TASK_MENU_EVENT_KEY,
        NEW_TASK_MENU_EVENT_KEY,
        ARCHIVE_TASK_MENU_EVENT_KEY,
        USAGE_MENU_EVENT_KEY,
        DESKTOP_SYNC_MENU_EVENT_KEY,
        DESKTOP_SYNC_SWITCH_MENU_EVENT_KEY,
        TASK_SUBSCRIPTIONS_MENU_EVENT_KEY,
        TASK_SETTINGS_MENU_EVENT_KEY,
        COMPACT_CONTEXT_MENU_EVENT_KEY,
        PROMLIGHT_MENU_EVENT_KEY,
        PROMLIGHT_LEGEND_MENU_EVENT_KEY,
    } or not authorized_user(user_id):
        return
    event_id = str(event.get("event_id") or "")
    if not event_id:
        return
    with _state_lock:
        state = load_state()
        if processed_event_seen(state, f"menu:{event_id}"):
            return
    if event_key == CURRENT_TASK_MENU_EVENT_KEY:
        task = selected_task(user_id, state)
        if task is None:
            send_task_card(user_id, state, event_id)
            log(f"menu handled key={event_key} result=task-selector")
            return
        update_current_status_card(
            user_id,
            task=task,
            ensure=True,
            force_new=True,
        )
        log(f"menu handled key={event_key} result=status-card")
        return
    if event_key == TASK_MENU_EVENT_KEY:
        send_task_card(user_id, state, event_id)
        log(f"menu handled key={event_key} result=task-selector")
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
        log(f"menu handled key={event_key} result=new-task-card")
        return
    if event_key == USAGE_MENU_EVENT_KEY:
        send_menu_card(
            user_id,
            state,
            build_codex_usage_card(codex_usage_snapshot()),
            f"codex-usage-{event_id}",
        )
        log(f"menu handled key={event_key} result=usage-card")
        return
    if event_key == TASK_SUBSCRIPTIONS_MENU_EVENT_KEY:
        send_menu_card(
            user_id,
            state,
            task_subscriptions_card_for_state(user_id, state),
            f"task-subscriptions-{event_id}",
            "task_subscriptions",
        )
        log(f"menu handled key={event_key} result=task-subscriptions-card")
        return
    if event_key == PROMLIGHT_MENU_EVENT_KEY:
        reconcile_promlight_state()
        with _state_lock:
            state = load_state()
            card = build_promlight_control_card(user_id, state)
        send_menu_card(
            user_id,
            state,
            card,
            f"promlight-{event_id}",
            "promlight",
        )
        log(f"menu handled key={event_key} result=promlight-card")
        return
    if event_key == PROMLIGHT_LEGEND_MENU_EVENT_KEY:
        send_menu_card(
            user_id,
            state,
            build_promlight_legend_card(),
            f"promlight-legend-{event_id}",
        )
        log(f"menu handled key={event_key} result=promlight-legend-card")
        return
    if event_key == TASK_SETTINGS_MENU_EVENT_KEY:
        task = selected_task(user_id, state)
        if task is None:
            send_task_card(user_id, state, event_id)
            log(f"menu handled key={event_key} result=task-selector")
            return
        loading_card = build_task_settings_card(
            task,
            status="正在读取 Codex Desktop 设置",
            loading=True,
        )
        success, chat_id, message_id = send_card(
            user_id,
            loading_card,
            f"task-settings-{event_id}",
        )
        if not success:
            queue_pending_menu_card(
                user_id,
                build_task_settings_card(task),
                f"task-settings-{event_id}",
                "飞书卡片发送超时或网络失败",
            )
            return
        with _state_lock:
            state = load_state()
            if chat_id:
                authorize_chat(state, user_id, chat_id)
            if message_id:
                remember_card_context(
                    state,
                    user_id,
                    message_id,
                    loading_card,
                    "task_settings",
                )
        if message_id:
            threading.Thread(
                target=refresh_task_settings_card,
                args=(user_id, message_id, str(task["id"])),
                daemon=True,
                name=f"codex-feishu-task-settings-{str(task['id'])[:8]}",
            ).start()
        log(f"menu handled key={event_key} result=task-settings-card")
        return
    if event_key == COMPACT_CONTEXT_MENU_EVENT_KEY:
        task = selected_task(user_id, state)
        if task is None:
            send_task_card(user_id, state, event_id)
            log(f"menu handled key={event_key} result=task-selector")
            return
        send_menu_card(
            user_id,
            state,
            build_compact_task_context_card(task),
            f"compact-task-context-{event_id}",
            "compact_task_context",
        )
        log(f"menu handled key={event_key} result=compact-task-context-card")
        return
    if event_key == DESKTOP_SYNC_MENU_EVENT_KEY:
        start_desktop_sync(user_id, state, event_id)
        return
    if event_key == DESKTOP_SYNC_SWITCH_MENU_EVENT_KEY:
        start_desktop_sync_switch(user_id, state, event_id)
        return
    task = selected_task(user_id, state)
    busy = bool(task and active_run_for_task(str(task["id"])) is not None)
    send_menu_card(
        user_id,
        state,
        build_archive_task_card(task, busy=busy),
        f"archive-task-{event_id}",
    )
    log(f"menu handled key={event_key} result=archive-card")


def handle_menu_event(event: dict[str, Any]) -> None:
    event_id = str(event.get("event_id") or "")
    processed_key = f"menu:{event_id}" if event_id else ""
    if processed_key:
        with _state_lock:
            if processed_event_seen(load_state(), processed_key):
                return
    _handle_menu_event_once(event)
    if processed_key:
        mark_processed(load_state(), processed_key)


def dispatch_event(event: dict[str, Any]) -> None:
    global _last_feishu_event_at

    _last_feishu_event_at = time.time()
    write_runtime_status()
    if event.get("type") == "card.action.trigger":
        handle_card_event(event)
    elif event.get("type") == "application.bot.menu_v6":
        handle_menu_event(event)
    else:
        handle_message_event(event)


def event_lane_key(event: dict[str, Any]) -> str:
    identity = (
        event.get("operator_id")
        or (
            event.get("sender_id")
            if event.get("type") not in {"card.action.trigger", "application.bot.menu_v6"}
            else ""
        )
    )
    return str(
        identity
        or event.get("chat_id")
        or event.get("type")
        or "unknown"
    )


def event_latency_label(event: dict[str, Any]) -> str:
    event_type = str(event.get("type") or "unknown")
    if event_type == "card.action.trigger":
        return str(
            event.get("action_name")
            or action_payload(event).get("action")
            or event.get("action_tag")
            or "unknown"
        )
    if event_type == "application.bot.menu_v6":
        return str(event.get("event_key") or "unknown")
    return str(event.get("message_type") or "message")


def ui_intent_key(event: dict[str, Any]) -> str:
    if event.get("type") != "card.action.trigger":
        return ""
    user_id = str(event.get("operator_id") or "")
    message_id = str(event.get("message_id") or "")
    action_tag = str(event.get("action_tag") or "")
    action_name = str(event.get("action_name") or "")
    if action_tag == "select_static" and action_name in {
        "project_selector",
        "task_selector",
        "task_scope_selector",
        "new_task_project_selector",
        "archived_project_selector",
        "archived_task_selector",
        "subscription_project_selector",
        "subscription_task_selector",
        "task_model_selector",
        "task_effort_selector",
        "promlight_project_selector",
        "promlight_task_selector",
    }:
        return f"{user_id}:{message_id}:{action_name}"
    if action_tag != "button":
        return ""
    action = str(action_payload(event).get("action") or "")
    if action in {
        "task_page",
        "archived_task_page",
        "refresh_task_list",
        "refresh_archived_tasks",
        "clear_task_search",
        "show_task_selector",
        "show_desktop_sync_task_selector",
        "show_archived_tasks",
        "show_new_task",
        "refresh_current_status",
        "refresh_codex_usage",
        "show_codex_usage",
        "show_daily_task_usage_analysis",
        "show_period_task_usage_analysis",
        "show_task_subscriptions",
        "task_subscription_page",
        "refresh_task_settings",
        "compact_current_task",
        "show_promlight",
        "promlight_refresh",
        "promlight_manage_tasks",
    }:
        return f"{user_id}:{message_id}:{action}"
    return ""


def register_ui_intent(event: dict[str, Any]) -> None:
    key = ui_intent_key(event)
    if not key:
        return
    with _ui_intent_lock:
        sequence = _ui_intent_sequences.get(key, 0) + 1
        _ui_intent_sequences[key] = sequence
    event["_ui_intent_key"] = key
    event["_ui_intent_sequence"] = sequence


def ui_intent_is_current(event: dict[str, Any]) -> bool:
    key = str(event.get("_ui_intent_key") or "")
    if not key:
        return True
    sequence = int(event.get("_ui_intent_sequence") or 0)
    with _ui_intent_lock:
        return _ui_intent_sequences.get(key) == sequence


def drain_event_lane(
    lane_key: str,
    lane: queue.Queue[dict[str, Any]],
) -> None:
    while True:
        try:
            event = lane.get_nowait()
        except queue.Empty:
            with _event_lanes_lock:
                if lane.empty() and _event_lanes.get(lane_key) is lane:
                    _event_lanes.pop(lane_key, None)
                    return
            continue
        event_type = str(event.get("type") or "unknown")
        event_action = event_latency_label(event)
        received_at = float(event.get("_bridge_received_monotonic") or time.monotonic())
        started = time.monotonic()
        queue_ms = round((started - received_at) * 1000)
        succeeded = False
        try:
            if ui_intent_is_current(event):
                dispatch_event(event)
            else:
                log("card intent skipped reason=superseded")
            acknowledge_workflow_decision_inbox(event)
            succeeded = True
        except Exception as exc:
            log(f"event failed: {type(exc).__name__}: {exc}")
        finally:
            total_ms = round((time.monotonic() - received_at) * 1000)
            log(
                f"latency event type={event_type} action={event_action} "
                f"queue_ms={queue_ms} "
                f"total_ms={total_ms} success={str(succeeded).lower()}"
            )
            lane.task_done()


def submit_event(event: dict[str, Any]) -> None:
    event.setdefault("_bridge_received_monotonic", time.monotonic())
    register_ui_intent(event)
    lane_key = event_lane_key(event)
    with _event_lanes_lock:
        lane = _event_lanes.get(lane_key)
        should_start = lane is None
        if lane is None:
            lane = queue.Queue()
            _event_lanes[lane_key] = lane
        lane.put(event)
    if should_start:
        threading.Thread(
            target=drain_event_lane,
            args=(lane_key, lane),
            daemon=True,
            name="codex-feishu-event-lane",
        ).start()


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
            event["_bridge_received_monotonic"] = time.monotonic()
            tag_workflow_decision_inbox_event(event)
            events.put(event)
        except json.JSONDecodeError as exc:
            log(f"invalid event JSON: {exc}")


def stop(_signum: int, _frame: Any) -> None:
    try:
        prepare_active_run_recovery()
    except Exception as exc:
        log(f"active run recovery preparation failed: {type(exc).__name__}: {exc}")
    _shutdown_event.set()
    with _identity_refresh_condition:
        _identity_refresh_condition.notify_all()
    stop_workflow_socket_server()
    for consumer in _consumers:
        if consumer.poll() is None:
            consumer.terminate()
    write_runtime_status(active_runs=0)


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


def maintenance_loop(events: queue.Queue[dict[str, Any]]) -> None:
    next_pending_retry = 0.0
    next_input_retry = 0.0
    next_workflow_retry = 0.0
    next_runtime_status = 0.0
    next_usage_refresh = 0.0
    next_cli_fallback_expiry = 0.0
    next_desktop_sync_retry = 0.0
    next_restart_recovery_retry = 0.0
    next_task_subscription_poll = 0.0
    while not _shutdown_event.is_set():
        now = time.time()
        if now >= next_cli_fallback_expiry:
            try:
                expire_cli_fallbacks(now)
            except Exception as exc:
                log(f"cli fallback expiry loop failed: {type(exc).__name__}: {exc}")
            next_cli_fallback_expiry = now + 60
        if now >= next_usage_refresh:
            threading.Thread(target=refresh_codex_usage, daemon=True).start()
            next_usage_refresh = now + 60
        if now >= next_runtime_status:
            write_runtime_status()
            next_runtime_status = now + 5
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
        if now >= next_desktop_sync_retry:
            try:
                retry_desktop_result_subscriptions(now)
            except Exception as exc:
                log(f"desktop sync loop failed: {type(exc).__name__}: {exc}")
            next_desktop_sync_retry = now + 1
        if now >= next_restart_recovery_retry:
            try:
                retry_recoverable_runs(now)
            except Exception as exc:
                log(f"restart recovery loop failed: {type(exc).__name__}: {exc}")
            next_restart_recovery_retry = now + 1
        if now >= next_task_subscription_poll:
            try:
                poll_task_subscriptions()
                poll_promlight_task_statuses()
            except Exception as exc:
                log(f"task observation loop failed: {type(exc).__name__}: {exc}")
            next_task_subscription_poll = now + TASK_SUBSCRIPTION_POLL_SECONDS
        if now >= next_workflow_retry:
            try:
                if workflow_notifications_enabled():
                    enqueue_workflow_decision_inbox(events)
                retry_workflow_notifications(now)
                retry_workflow_recoveries(now)
            except Exception as exc:
                log(f"workflow loop failed: {type(exc).__name__}: {exc}")
            next_workflow_retry = now + 1
        _shutdown_event.wait(0.2)


def main() -> int:
    if sys.argv[1:] == ["--promlight-status-json"]:
        with _state_lock:
            state = load_state()
            lamps = [
                {
                    "lamp_id": str(lamp.get("lamp_id") or ""),
                    "owner_open_id": str(lamp.get("owner_open_id") or ""),
                    "name": str(lamp.get("name") or "PromLight"),
                    "is_default": bool(lamp.get("is_default")),
                    "relay_ref": str(lamp.get("relay_ref") or ""),
                }
                for lamp in promlight_state(state)["lamps"].values()
                if isinstance(lamp, dict)
            ]
        print(
            json.dumps(
                {"devices": discover_promlight_devices(), "bindings": lamps},
                ensure_ascii=False,
            )
        )
        return 0
    if sys.argv[1:] == ["--promlight-bind-json"]:
        try:
            payload = json.load(sys.stdin)
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            return 2
        if not isinstance(payload, dict):
            return 2
        user_id = str(payload.get("open_id") or "").strip()
        relay_ref = str(payload.get("relay_ref") or "").strip()
        name = str(payload.get("name") or "").strip()
        if not user_id.startswith("ou_") or not relay_ref or len(relay_ref) > 256 or len(name) > 40:
            return 2
        try:
            lamp_id = bind_promlight(user_id, relay_ref, name)
        except (PermissionError, ValueError) as exc:
            print(str(exc))
            return 1
        print(json.dumps({"ok": True, "lamp_id": lamp_id}, ensure_ascii=False))
        return 0
    if sys.argv[1:] == ["--remove-access-requests"]:
        try:
            payload = json.load(sys.stdin)
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            return 2
        if not isinstance(payload, list) or not payload or any(
            not isinstance(open_id, str)
            or not open_id.startswith("ou_")
            or len(open_id) > 256
            for open_id in payload
        ):
            return 2
        print(remove_access_requests(set(payload)))
        return 0
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

    write_runtime_status()
    _shutdown_event.clear()
    threading.Thread(
        target=promlight_worker_loop,
        daemon=True,
        name="codex-feishu-promlight",
    ).start()
    threading.Thread(
        target=identity_refresh_loop,
        daemon=True,
        name="codex-feishu-identity-refresh",
    ).start()
    threading.Thread(
        target=maintenance_loop,
        args=(events,),
        daemon=True,
        name="codex-feishu-maintenance",
    ).start()
    while any(consumer.poll() is None for consumer in _consumers):
        try:
            event = events.get(timeout=0.5)
        except queue.Empty:
            continue
        submit_event(event)
    _shutdown_event.set()
    write_runtime_status(active_runs=0)
    return next(
        (consumer.returncode for consumer in _consumers if consumer.returncode),
        0,
    )


if __name__ == "__main__":
    raise SystemExit(main())
