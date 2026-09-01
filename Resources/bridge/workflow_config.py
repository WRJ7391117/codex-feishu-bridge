#!/usr/bin/env python3
"""Safely configure local workflow notifications without exposing identifiers."""

from __future__ import annotations

import json
import os
from pathlib import Path
import secrets
import stat
import sys
import uuid
from typing import Any


DEFAULT_CONFIG_PATH = (
    Path.home() / "Library/Application Support/Codex Feishu Bridge/config.json"
)
ORI_ONE_WORKFLOW_ID = "ori-one-mind"
AGENT_MESH_WORKFLOW_ID = "deepori-agent-mesh"
ALLOWED_WORKFLOW_IDS = frozenset(
    {ORI_ONE_WORKFLOW_ID, AGENT_MESH_WORKFLOW_ID}
)
MAX_TASK_ID_BYTES = 128


class WorkflowConfigError(RuntimeError):
    """The local workflow configuration cannot be trusted or updated."""


def _private_parent(path: Path) -> None:
    try:
        parent_stat = path.parent.lstat()
    except OSError as exc:
        raise WorkflowConfigError("invalid") from exc
    if (
        path.parent.is_symlink()
        or not stat.S_ISDIR(parent_stat.st_mode)
        or parent_stat.st_uid != os.getuid()
        or (parent_stat.st_mode & 0o777) != 0o700
    ):
        raise WorkflowConfigError("invalid")


def load_config(path: Path) -> dict[str, Any]:
    _private_parent(path)
    try:
        path_stat = path.lstat()
    except OSError as exc:
        raise WorkflowConfigError("invalid") from exc
    if (
        path.is_symlink()
        or not stat.S_ISREG(path_stat.st_mode)
        or path_stat.st_uid != os.getuid()
        or (path_stat.st_mode & 0o777) != 0o600
    ):
        raise WorkflowConfigError("invalid")

    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags)
        with os.fdopen(descriptor, "r", encoding="utf-8") as handle:
            opened_stat = os.fstat(handle.fileno())
            if (
                opened_stat.st_dev != path_stat.st_dev
                or opened_stat.st_ino != path_stat.st_ino
                or not stat.S_ISREG(opened_stat.st_mode)
                or opened_stat.st_uid != os.getuid()
                or (opened_stat.st_mode & 0o777) != 0o600
            ):
                raise WorkflowConfigError("invalid")
            config = json.load(handle)
    except WorkflowConfigError:
        raise
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise WorkflowConfigError("invalid") from exc
    if not isinstance(config, dict):
        raise WorkflowConfigError("invalid")
    return config


def _allowlisted_legacy_sender(config: dict[str, Any]) -> str:
    sender = config.get("allowed_sender_id")
    users = config.get("allowed_users")
    if not isinstance(sender, str) or not sender.startswith("ou_"):
        raise WorkflowConfigError("invalid")
    if not isinstance(users, list):
        raise WorkflowConfigError("invalid")
    for user in users:
        if not isinstance(user, dict) or user.get("open_id") != sender:
            continue
        projects = user.get("allowed_projects")
        if isinstance(projects, list) and any(
            isinstance(project, str) and project.strip() for project in projects
        ):
            return sender
    raise WorkflowConfigError("invalid")


def _task_id(raw: str) -> str:
    value = raw.strip()
    try:
        normalized = str(uuid.UUID(value))
    except (ValueError, AttributeError) as exc:
        raise WorkflowConfigError("invalid") from exc
    if normalized != value.lower():
        raise WorkflowConfigError("invalid")
    return normalized


def _write_config(path: Path, config: dict[str, Any]) -> None:
    _private_parent(path)
    try:
        original_stat = path.lstat()
    except OSError as exc:
        raise WorkflowConfigError("invalid") from exc
    if (
        path.is_symlink()
        or not stat.S_ISREG(original_stat.st_mode)
        or original_stat.st_uid != os.getuid()
        or (original_stat.st_mode & 0o777) != 0o600
    ):
        raise WorkflowConfigError("invalid")

    temporary = path.parent / (
        f".{path.name}.{os.getpid()}.{secrets.token_hex(8)}.tmp"
    )
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(temporary, flags, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(config, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        current_stat = path.lstat()
        if (
            current_stat.st_dev != original_stat.st_dev
            or current_stat.st_ino != original_stat.st_ino
            or path.is_symlink()
            or not stat.S_ISREG(current_stat.st_mode)
            or current_stat.st_uid != os.getuid()
        ):
            raise WorkflowConfigError("invalid")
        os.replace(temporary, path)
        directory_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0)
        directory_descriptor = os.open(path.parent, directory_flags)
        try:
            os.fsync(directory_descriptor)
        finally:
            os.close(directory_descriptor)
        if (path.stat().st_mode & 0o777) != 0o600:
            raise WorkflowConfigError("invalid")
    except WorkflowConfigError:
        temporary.unlink(missing_ok=True)
        raise
    except OSError as exc:
        temporary.unlink(missing_ok=True)
        raise WorkflowConfigError("invalid") from exc


def enable(path: Path, raw_task_id: str) -> None:
    config = load_config(path)
    recipient = _allowlisted_legacy_sender(config)
    task_id = _task_id(raw_task_id)
    existing = config.get("workflow_notifications")
    existing_workflow = existing if isinstance(existing, dict) else {}
    existing_recipient = existing_workflow.get("recipient_open_id")
    existing_chat = existing_workflow.get("recipient_chat_id")
    chat_id = (
        existing_chat
        if existing_recipient == recipient
        and isinstance(existing_chat, str)
        and (not existing_chat or existing_chat.startswith("oc_"))
        else ""
    )
    config["workflow_notifications"] = {
        "enabled": True,
        "allowed_workflow_id": ORI_ONE_WORKFLOW_ID,
        "recipient_open_id": recipient,
        "recipient_chat_id": chat_id,
        "codex_task_id": task_id,
        "workflows": {
            **(
                existing_workflow.get("workflows")
                if isinstance(existing_workflow.get("workflows"), dict)
                else {}
            ),
            ORI_ONE_WORKFLOW_ID: {"codex_task_id": task_id},
        },
    }
    _write_config(path, config)


def disable(path: Path) -> None:
    config = load_config(path)
    workflow = config.get("workflow_notifications")
    if not isinstance(workflow, dict):
        workflow = {
            "allowed_workflow_id": ORI_ONE_WORKFLOW_ID,
            "recipient_open_id": "",
            "recipient_chat_id": "",
            "codex_task_id": "",
        }
    workflow["enabled"] = False
    config["workflow_notifications"] = workflow
    _write_config(path, config)


def set_workflow(path: Path, workflow_id: str, raw_task_id: str) -> None:
    if workflow_id not in ALLOWED_WORKFLOW_IDS:
        raise WorkflowConfigError("invalid")
    config = load_config(path)
    workflow = config.get("workflow_notifications")
    if not isinstance(workflow, dict) or workflow.get("enabled") is not True:
        raise WorkflowConfigError("invalid")
    _allowlisted_legacy_sender(config)
    task_id = _task_id(raw_task_id)
    bindings = workflow.get("workflows")
    if not isinstance(bindings, dict):
        bindings = {}
    legacy_workflow_id = str(workflow.get("allowed_workflow_id") or "")
    legacy_task_id = str(workflow.get("codex_task_id") or "")
    if legacy_workflow_id in ALLOWED_WORKFLOW_IDS:
        bindings.setdefault(
            legacy_workflow_id,
            {"codex_task_id": _task_id(legacy_task_id)},
        )
    bindings[workflow_id] = {"codex_task_id": task_id}
    workflow["workflows"] = bindings
    config["workflow_notifications"] = workflow
    _write_config(path, config)


def status(path: Path) -> str:
    try:
        config = load_config(path)
    except WorkflowConfigError:
        return "invalid"
    workflow = config.get("workflow_notifications")
    if not isinstance(workflow, dict):
        return "invalid"
    if workflow.get("enabled") is False:
        return "disabled"
    if workflow.get("enabled") is not True:
        return "invalid"
    try:
        recipient = _allowlisted_legacy_sender(config)
        task_id = _task_id(str(workflow.get("codex_task_id") or ""))
    except WorkflowConfigError:
        return "invalid"
    chat_id = workflow.get("recipient_chat_id")
    if (
        workflow.get("allowed_workflow_id") != ORI_ONE_WORKFLOW_ID
        or workflow.get("recipient_open_id") != recipient
        or not isinstance(chat_id, str)
        or (chat_id and not chat_id.startswith("oc_"))
    ):
        return "invalid"
    workflows = workflow.get("workflows")
    if workflows is not None:
        if not isinstance(workflows, dict):
            return "invalid"
        if any(
            workflow_id not in ALLOWED_WORKFLOW_IDS
            or not isinstance(entry, dict)
            or set(entry) != {"codex_task_id"}
            for workflow_id, entry in workflows.items()
        ):
            return "invalid"
        try:
            for entry in workflows.values():
                _task_id(str(entry.get("codex_task_id") or ""))
        except WorkflowConfigError:
            return "invalid"
        ori_one = workflows.get(ORI_ONE_WORKFLOW_ID)
        if not isinstance(ori_one, dict) or set(ori_one) != {"codex_task_id"}:
            return "invalid"
        try:
            if _task_id(str(ori_one.get("codex_task_id") or "")) != task_id:
                return "invalid"
        except WorkflowConfigError:
            return "invalid"
    return "configured"


def main() -> int:
    arguments = sys.argv[1:]
    if arguments == ["--status"]:
        current = status(DEFAULT_CONFIG_PATH)
        print(current)
        return 0 if current != "invalid" else 1
    if arguments == ["--disable"]:
        try:
            disable(DEFAULT_CONFIG_PATH)
        except WorkflowConfigError:
            print("invalid")
            return 1
        print("disabled")
        return 0
    if arguments == ["--enable"]:
        raw = sys.stdin.buffer.read(MAX_TASK_ID_BYTES + 1)
        if not raw or len(raw) > MAX_TASK_ID_BYTES:
            print("invalid")
            return 1
        try:
            enable(DEFAULT_CONFIG_PATH, raw.decode("utf-8"))
        except (UnicodeDecodeError, WorkflowConfigError):
            print("invalid")
            return 1
        print("configured")
        return 0
    if (
        len(arguments) == 2
        and arguments[0] == "--set-workflow"
        and arguments[1] in ALLOWED_WORKFLOW_IDS
    ):
        raw = sys.stdin.buffer.read(MAX_TASK_ID_BYTES + 1)
        if not raw or len(raw) > MAX_TASK_ID_BYTES:
            print("invalid")
            return 1
        try:
            set_workflow(
                DEFAULT_CONFIG_PATH,
                arguments[1],
                raw.decode("utf-8"),
            )
        except (UnicodeDecodeError, WorkflowConfigError):
            print("invalid")
            return 1
        print("configured")
        return 0
    print("invalid")
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
