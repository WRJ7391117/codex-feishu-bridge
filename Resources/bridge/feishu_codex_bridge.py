#!/usr/bin/env python3
"""Route authorized Feishu messages to a selected local Codex task."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shutil
import signal
import socket
import sqlite3
import struct
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any, Callable
from urllib.parse import unquote, urlsplit
import uuid


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


def find_executable(config_key: str, names: tuple[str, ...], paths: tuple[str, ...]) -> str:
    configured = str(CONFIG.get(config_key) or "").strip()
    if configured:
        candidate = Path(configured).expanduser()
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    for name in names:
        found = shutil.which(name)
        if found:
            return found
    for raw_path in paths:
        candidate = Path(raw_path).expanduser()
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    return configured


LARK_CLI = find_executable(
    "lark_cli_path",
    ("lark-cli",),
    ("/opt/homebrew/bin/lark-cli", "/usr/local/bin/lark-cli"),
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

IMAGE_SUFFIXES = {".gif", ".jpeg", ".jpg", ".png", ".webp"}
MARKDOWN_IMAGE_PATTERN = re.compile(r"!\[[^\]\n]*\]\(\s*(<[^>\n]+>|[^)\n]+)\s*\)")

EVENT_KEYS = (
    "im.message.receive_v1",
    "card.action.trigger",
    "application.bot.menu_v6",
)
TASK_MENU_EVENT_KEY = str(CONFIG.get("task_menu_event_key") or "select_task")

_consumers: list[subprocess.Popen[str]] = []


def authorized_user(open_id: str) -> bool:
    return open_id in ALLOWED_USERS


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
    with LOG_PATH.open("a", encoding="utf-8") as handle:
        handle.write(message.rstrip() + "\n")


def load_state() -> dict[str, Any]:
    try:
        return json.loads(STATE_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {
            "selected": {},
            "last_lists": {},
            "authorized_chats": {},
            "processed": [],
            "bridge_turns": [],
        }


def save_state(state: dict[str, Any]) -> None:
    STATE_PATH.parent.mkdir(parents=True, exist_ok=True)
    temporary = STATE_PATH.with_suffix(".tmp")
    temporary.write_text(
        json.dumps(state, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    temporary.replace(STATE_PATH)


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


def recent_tasks(user_id: str) -> list[dict[str, str]]:
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
                   catalog.display_title AS task_name
            FROM local_thread_catalog AS catalog
            JOIN state.threads ON state.threads.id = catalog.thread_id
            WHERE catalog.host_id = 'local'
              AND catalog.missing_candidate = 0
              AND state.threads.archived = 0
              AND state.threads.preview <> ''
            ORDER BY catalog.source_recency_at DESC, catalog.thread_id DESC
            """,
        ).fetchall()
    finally:
        connection.close()
    project_names = desktop_project_names()
    tasks = [
        {
            "id": str(row["id"]),
            "title": str(row["task_name"]),
            "project": project_names.get(str(row["id"])) or "无项目",
        }
        for row in rows
    ]
    return [task for task in tasks if user_can_access_task(user_id, task)]


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
                   catalog.display_title AS task_name
            FROM local_thread_catalog AS catalog
            JOIN state.threads ON state.threads.id = catalog.thread_id
            WHERE catalog.host_id = 'local'
              AND catalog.thread_id = ?
              AND catalog.missing_candidate = 0
              AND state.threads.archived = 0
              AND state.threads.preview <> ''
            """,
            (thread_id,),
        ).fetchone()
    finally:
        connection.close()
    if row is None:
        return None
    project_names = desktop_project_names()
    task = {
        "id": str(row["id"]),
        "title": str(row["task_name"]),
        "project": project_names.get(str(row["id"])) or "无项目",
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


def reply(message_id: str, text: str, kind: str) -> bool:
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
    for attempt in range(1, 3):
        try:
            result = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=30,
                env=lark_environment(),
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            log(
                f"reply failed kind={kind} attempt={attempt} "
                f"error={type(exc).__name__}"
            )
            continue
        if lark_succeeded(result):
            return True
        log(
            f"reply failed kind={kind} attempt={attempt} "
            f"code={result.returncode}"
        )
    return False


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
            log(
                f"image reply failed index={index} attempt={attempt} "
                f"error={type(exc).__name__}"
            )
            continue
        if lark_succeeded(result):
            return True
        log(
            f"image reply failed index={index} attempt={attempt} "
            f"code={result.returncode}"
        )
    return False


def reply_card(message_id: str, card: dict[str, Any], kind: str) -> bool:
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
        return False
    return True


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
) -> tuple[bool, str | None]:
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
        return False, None
    return True, sent_chat_id(result.stdout)


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


def normalized_content(content: str) -> str:
    return re.sub(r"^@\S+\s*", "", content.strip()).strip()


def help_text() -> str:
    return (
        "可用命令：\n"
        "对话 —— 打开 Codex task 选择卡片\n"
        "选择 N —— 文字选择 task（备用）\n"
        "当前 —— 查看当前 task\n"
        "帮助 —— 显示本说明\n\n"
        "选择后，其他文本会原样发送到该 Codex task。当前版本只接收文本。"
    )


def option_text(task: dict[str, str]) -> str:
    return f"{task['project']} · {task['title']}"


def build_task_card(
    tasks: list[dict[str, str]],
    selected_id: str | None,
) -> dict[str, Any]:
    selected = next((task for task in tasks if task["id"] == selected_id), None)
    header: dict[str, Any] = {
        "title": {"tag": "plain_text", "content": "选择 Codex task"},
        "subtitle": {
            "tag": "plain_text",
            "content": (
                f"当前：{option_text(selected)}"
                if selected
                else "选择后，后续文字会发送到该 task"
            ),
        },
        "template": "green" if selected else "blue",
        "icon": {"tag": "standard_icon", "token": "ai-common_colorful"},
    }
    if selected:
        header["text_tag_list"] = [
            {
                "tag": "text_tag",
                "text": {"tag": "plain_text", "content": "已选择"},
                "color": "green",
            }
        ]
    elements: list[dict[str, Any]] = [
        {
            "tag": "markdown",
            "content": (
                "**请选择一个 Codex task**\n选中后，直接发送文字即可继续该 task。"
                if tasks
                else "当前没有你有权访问的 Codex task。请联系这台 Mac 的管理员。"
            ),
        }
    ]
    selector: dict[str, Any] = {
        "tag": "select_static",
        "name": "task_selector",
        "placeholder": {"tag": "plain_text", "content": "点击选择一个 task"},
        "options": [
            {
                "text": {"tag": "plain_text", "content": option_text(task)},
                "value": task["id"],
            }
            for task in tasks
        ],
        "width": "fill",
    }
    if selected:
        selector["initial_option"] = selected["id"]
    if tasks:
        elements.append(selector)
    return {
        "schema": "2.0",
        "config": {
            "update_multi": True,
            "width_mode": "default",
            "enable_forward": False,
            "summary": {"content": "选择 Codex task"},
        },
        "header": header,
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
) -> dict[str, Any] | None:
    try:
        card = json.loads(card_content)
    except (TypeError, json.JSONDecodeError):
        return None
    if not isinstance(card, dict) or card.get("schema") != "2.0":
        return None
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
    header["subtitle"] = {
        "tag": "plain_text",
        "content": f"当前：{option_text(selected)}",
    }
    header["template"] = "green"
    header["text_tag_list"] = [
        {
            "tag": "text_tag",
            "text": {"tag": "plain_text", "content": "已选择"},
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
    success, chat_id = send_card(
        user_id,
        task_card_for_state(user_id, state),
        f"menu-{event_id}",
    )
    if success and chat_id:
        authorize_chat(state, user_id, chat_id)
    return success


def authorize_chat(state: dict[str, Any], user_id: str, chat_id: str) -> None:
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


def selected_task(user_id: str, state: dict[str, Any]) -> dict[str, str] | None:
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
    selected = selected_task(user_id, state)
    tasks = recent_tasks(user_id)
    state.setdefault("last_lists", {})[user_id] = [task["id"] for task in tasks]
    save_state(state)
    return build_task_card(tasks, selected["id"] if selected else None)


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
    return f"已选择：{selected['title']}（{selected['project']}）"


def current_task(user_id: str, state: dict[str, Any]) -> str:
    task = selected_task(user_id, state)
    if not task:
        return "尚未选择 Codex task。请点击机器人菜单中的“选择 Task”。"
    return f"当前：{task['title']}（{task['project']}）"


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


def remember_bridge_turn(turn_id: str) -> None:
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
) -> tuple[bool, str, list[str]]:
    deadline = time.monotonic() + 3600
    images: list[str] = []
    with rollout_path.open("r", encoding="utf-8") as handle:
        handle.seek(start_offset)
        while time.monotonic() < deadline:
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
                    "Codex 没有完成这条消息，请在桌面版中查看具体原因。",
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
) -> tuple[str, str, list[str]]:
    rollout_path = rollout_path_for_task(thread_id)
    if not DESKTOP_IPC_SOCKET.exists() or not rollout_path or not rollout_path.exists():
        return "unavailable", "", []
    start_offset = rollout_path.stat().st_size
    turn_request_attempted = False
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(30)
            connection.connect(str(DESKTOP_IPC_SOCKET))

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

            request_id = str(uuid.uuid4())
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
                            "request": {
                                "threadId": thread_id,
                                "input": [
                                    {
                                        "type": "text",
                                        "text": prompt,
                                        "text_elements": [],
                                    }
                                ],
                            },
                            "context": {"inheritThreadSettings": True},
                        },
                    },
                    "timeoutMs": 30000,
                },
            )
            response = wait_for_ipc_response(connection, request_id)
    except (ConnectionError, FileNotFoundError, socket.timeout) as exc:
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

    if response.get("resultType") != "success":
        error = " ".join(str(response.get("error") or "unknown error").split())[-2000:]
        if error == "no-client-found":
            return "unavailable", "", []
        log(f"desktop turn failed error={error}")
        if any(marker in error.lower() for marker in ("active turn", "already running", "busy")):
            return (
                "failed",
                "当前 task 正在运行；本条消息未发送、也未排队。"
                "请等待当前运行结束后重试。",
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
    if on_started is not None:
        on_started()
    try:
        remember_bridge_turn(turn_id)
    except OSError as exc:
        log(f"remember bridge turn failed: {type(exc).__name__}: {exc}")
    success, result, images = wait_for_desktop_turn(
        rollout_path,
        start_offset,
        turn_id,
    )
    return ("completed" if success else "failed"), result, images


def run_codex(
    thread_id: str,
    prompt: str,
    on_started: Callable[[str], None] | None = None,
) -> tuple[bool, str, list[str]]:
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
        lambda: notify_started("正在运行"),
    )
    if desktop_status == "completed":
        return True, desktop_result, desktop_images
    if desktop_status == "failed":
        return False, desktop_result, []

    environment = os.environ.copy()
    environment["CODEX_FEISHU_BRIDGE"] = "1"
    rollout_path = rollout_path_for_task(thread_id)
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
            thread_id,
            "-",
        ]
        notify_started("正在启动")
        try:
            result = subprocess.run(
                command,
                input=prompt,
                capture_output=True,
                text=True,
                timeout=3600,
                env=environment,
            )
        except subprocess.TimeoutExpired:
            return (
                False,
                "等待超过 60 分钟，尚未确认 task 完成；"
                "task 可能仍在 Codex Desktop 中运行，请在桌面版中查看。",
                [],
            )
        if result.returncode != 0:
            error = " ".join(result.stderr.strip().split())[-2000:] or "(empty)"
            log(f"codex resume failed code={result.returncode} stderr={error}")
            return (
                False,
                "没有成功发送到 Codex。详细原因已记录到 Mac 的桥接日志，请稍后重试。",
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
    processed = state.setdefault("processed", [])
    if key in processed:
        return False
    processed.append(key)
    state["processed"] = processed[-200:]
    save_state(state)
    return True


def handle_message_event(event: dict[str, Any]) -> None:
    chat_id = str(event.get("chat_id") or "")
    user_id = str(event.get("sender_id") or "")
    if (
        not authorized_user(user_id)
        or event.get("sender_type") != "user"
        or event.get("message_type") not in {"text", "post"}
    ):
        return
    message_id = str(event.get("message_id") or "")
    content = normalized_content(str(event.get("content") or ""))
    if not message_id or not content:
        return

    state = load_state()
    if event.get("chat_type") == "p2p":
        authorize_chat(state, user_id, chat_id)
    elif not is_authorized_chat(state, user_id, chat_id):
        return
    if not mark_processed(state, message_id):
        return

    if content in {"帮助", "/help", "help"}:
        reply(message_id, help_text(), "help")
        return
    if content in {"对话", "任务", "/list", "list"}:
        if not reply_task_card(message_id, user_id, state):
            reply(message_id, show_tasks(user_id, state), "list-fallback")
        return
    if content in {"当前", "/current", "current"}:
        reply(message_id, current_task(user_id, state), "current")
        return
    match = re.fullmatch(r"(?:/)?(?:选择|使用|use)\s+(.+)", content, re.IGNORECASE)
    if match:
        reply(
            message_id,
            select_task(user_id, match.group(1).strip(), state),
            "select",
        )
        return
    if len(content) > MAX_PROMPT_CHARS:
        reply(message_id, f"消息超过 {MAX_PROMPT_CHARS} 字，请缩短后重试。", "too-long")
        return

    task = selected_task(user_id, state)
    if not task:
        reply(
            message_id,
            "尚未选择 Codex task。请点击机器人菜单中的“选择 Task”。",
            "no-selection",
        )
        return

    def reply_task_status(status: str) -> None:
        reply(
            message_id,
            f"【Codex · {option_text(task)}】\n"
            f"状态：{status}\n"
            "完成后会自动回复结果。",
            "running",
        )

    try:
        success, result, rollout_images = run_codex(
            task["id"],
            content,
            reply_task_status,
        )
    except Exception as exc:
        log(f"Codex bridge run failed: {type(exc).__name__}: {exc}")
        success = False
        result = "桥接运行异常，详细原因已记录到 Mac 的桥接日志。"
        rollout_images = []
    status = "已完成" if success else "未完成"
    prefix = f"【Codex · {option_text(task)}】\n状态：{status}\n\n"
    clean_result, images = prepare_result_images(result, rollout_images)
    reply(message_id, prefix + clean_result, "final")
    failed_images = sum(
        not reply_image(message_id, image, index)
        for index, image in enumerate(images, start=1)
    )
    if failed_images:
        reply(
            message_id,
            f"有 {failed_images} 张图片未能发送，请在 Codex Desktop 中查看。",
            "image-error",
        )


def handle_card_event(event: dict[str, Any]) -> None:
    chat_id = str(event.get("chat_id") or "")
    user_id = str(event.get("operator_id") or "")
    action_name = str(event.get("action_name") or "")
    if (
        not authorized_user(user_id)
        or event.get("action_tag") != "select_static"
        or (action_name and action_name != "task_selector")
    ):
        return
    event_id = str(event.get("event_id") or "")
    message_id = str(event.get("message_id") or "")
    selected_id = str(event.get("option") or "")
    if not event_id or not selected_id:
        return
    selected = task_by_id(selected_id, user_id)
    if not selected:
        if message_id:
            reply(message_id, "该 task 已归档或删除，请重新选择。", f"stale-{event_id}")
        return
    state = load_state()
    card = updated_task_card(str(event.get("card_content") or ""), selected)
    if chat_id and not is_authorized_chat(state, user_id, chat_id):
        if card is None:
            log("card ignored reason=unrecognized-chat-and-card")
            return
        authorize_chat(state, user_id, chat_id)
    if not mark_processed(state, f"card:{event_id}"):
        return
    state.setdefault("selected", {})[user_id] = selected["id"]
    save_state(state)

    token = str(event.get("token") or "")
    if card is not None and token and update_card(token, card):
        return
    if message_id:
        reply(
            message_id,
            f"已选择：{selected['title']}（{selected['project']}）",
            f"selected-{event_id}",
        )


def handle_menu_event(event: dict[str, Any]) -> None:
    user_id = str(event.get("operator_id") or "")
    if (
        event.get("event_key") != TASK_MENU_EVENT_KEY
        or not authorized_user(user_id)
    ):
        return
    event_id = str(event.get("event_id") or "")
    if not event_id:
        return
    state = load_state()
    if not mark_processed(state, f"menu:{event_id}"):
        return
    send_task_card(user_id, state, event_id)


def dispatch_event(event: dict[str, Any]) -> None:
    if event.get("type") == "card.action.trigger":
        handle_card_event(event)
    elif event.get("type") == "application.bot.menu_v6":
        handle_menu_event(event)
    else:
        handle_message_event(event)


def log_consumer_stderr(stream: Any) -> None:
    for line in stream:
        log(line.rstrip())


def enqueue_events(stream: Any, events: queue.Queue[dict[str, Any]]) -> None:
    for line in stream:
        try:
            events.put(json.loads(line))
        except json.JSONDecodeError as exc:
            log(f"invalid event JSON: {exc}")


def stop(_signum: int, _frame: Any) -> None:
    for consumer in _consumers:
        if consumer.poll() is None:
            consumer.terminate()


def diagnostic_report() -> dict[str, Any]:
    checks = {
        "config_file": CONFIG_PATH.is_file(),
        "allowed_users": allowed_users_config_valid(),
        "lark_cli": bool(LARK_CLI and Path(LARK_CLI).is_file()),
        "codex_cli": bool(CODEX_CLI and Path(CODEX_CLI).is_file()),
        "codex_state_db": state_db_path().is_file(),
        "desktop_catalog_db": DESKTOP_CATALOG_DB.is_file(),
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
    assert card["body"]["elements"][1]["initial_option"] == tasks[0]["id"]
    assert [
        option["text"]["content"]
        for option in card["body"]["elements"][1]["options"]
    ] == [option_text(task) for task in tasks]
    assert card["header"]["subtitle"]["content"] == f"当前：{option_text(tasks[0])}"
    updated = updated_task_card(json.dumps(card), tasks[-1])
    assert updated is not None
    assert updated["header"]["template"] == "green"
    assert updated["body"]["elements"][1]["initial_option"] == tasks[-1]["id"]

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
        consumer = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
            env=lark_environment(),
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

    while any(consumer.poll() is None for consumer in _consumers):
        try:
            event = events.get(timeout=0.5)
        except queue.Empty:
            continue
        try:
            dispatch_event(event)
        except Exception as exc:
            log(f"event failed: {type(exc).__name__}: {exc}")
    return next(
        (consumer.returncode for consumer in _consumers if consumer.returncode),
        0,
    )


if __name__ == "__main__":
    raise SystemExit(main())
