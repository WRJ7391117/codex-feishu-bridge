#!/usr/bin/env python3
"""Validation and durable state for local workflow notifications."""

from __future__ import annotations

import hashlib
import hmac
import json
import math
import os
from pathlib import Path
import re
import secrets
import stat
import threading
import time
from typing import Any
from urllib.parse import unquote, urlsplit


INPUT_FIELDS = frozenset(
    {
        "workflow_id",
        "event_id",
        "task_id",
        "status",
        "summary",
        "workbench_url",
        "actions",
    }
)
ALLOWED_STATUSES = frozenset({"milestone_completed", "user_action_required"})
ACTION_FIELDS = frozenset(
    {"id", "label", "description", "recommended", "resolution"}
)
ALLOWED_RESOLUTIONS = frozenset({"resume", "pause", "stop"})
ORI_ONE_WORKFLOW_ID = "ori-one-mind"
AGENT_MESH_WORKFLOW_ID = "deepori-agent-mesh"
WORKFLOW_WORKBENCH_PATHS = {
    ORI_ONE_WORKFLOW_ID: "/ori-one/workbench/automation/",
    AGENT_MESH_WORKFLOW_ID: "/bridge/agent-mesh",
}
WORKBENCH_HOST = "deepori.cn"
IDENTIFIER_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")
SAFE_PATH_SEGMENT_PATTERN = re.compile(r"^[A-Za-z0-9._~-]+$")
STATE_KEY_PATTERN = re.compile(r"^[0-9a-f]{64}$")
RESTRICTED_VISIBLE_TEXT_PATTERNS = (
    re.compile(r"\bgh[pousr]_[A-Za-z0-9_]{8,}", re.IGNORECASE),
    re.compile(r"\bgithub_pat_[A-Za-z0-9_]{8,}", re.IGNORECASE),
    re.compile(r"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", re.IGNORECASE),
    re.compile(r"\bsk-[A-Za-z0-9_-]{8,}", re.IGNORECASE),
    re.compile(r"\bAKSRV_[A-Za-z0-9_-]{8,}", re.IGNORECASE),
    re.compile(r"\b(?:ou|oc)_[A-Za-z0-9_-]{8,}", re.IGNORECASE),
    re.compile(
        r"\b(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis)://\S+",
        re.IGNORECASE,
    ),
    re.compile(r"\b(?:DATABASE_URL|NEON_DATABASE_URL)\s*[:=]", re.IGNORECASE),
    re.compile(r"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", re.IGNORECASE),
)
MAX_NOTIFICATIONS = 500
DELIVERY_DELAYS = (15, 30, 60, 120, 300, 600, 1800, 3600)


class WorkflowNotificationError(ValueError):
    """A workflow request is invalid or conflicts with an existing event."""


class WorkflowStateError(RuntimeError):
    """The durable workflow state cannot be trusted or safely updated."""


class WorkflowDecisionInbox:
    """Private per-event spool written before a workflow card callback is ACKed."""

    MAX_EVENT_BYTES = 1024 * 1024

    def __init__(self, directory: Path):
        self.directory = directory
        self._lock = threading.RLock()

    @staticmethod
    def _valid_event_id(event_id: Any) -> bool:
        return (
            isinstance(event_id, str)
            and 0 < len(event_id) <= 512
            and not any(ord(character) < 32 for character in event_id)
        )

    @staticmethod
    def _event_filename(event_id: str) -> str:
        return hashlib.sha256(event_id.encode("utf-8")).hexdigest() + ".json"

    def path_for_event(self, event_id: str) -> Path:
        if not self._valid_event_id(event_id):
            raise WorkflowStateError("workflow decision inbox event is invalid")
        return self.directory / self._event_filename(event_id)

    def _validate_directory(self) -> None:
        try:
            directory_stat = self.directory.lstat()
        except OSError as exc:
            raise WorkflowStateError("workflow decision inbox is unavailable") from exc
        if (
            self.directory.is_symlink()
            or not stat.S_ISDIR(directory_stat.st_mode)
            or directory_stat.st_uid != os.getuid()
            or (directory_stat.st_mode & 0o777) != 0o700
        ):
            raise WorkflowStateError("workflow decision inbox is unsafe")

    def _read(self, path: Path) -> tuple[str, dict[str, Any]]:
        try:
            path_stat = path.lstat()
        except OSError as exc:
            raise WorkflowStateError("workflow decision inbox entry is unavailable") from exc
        if (
            path.is_symlink()
            or not stat.S_ISREG(path_stat.st_mode)
            or path_stat.st_uid != os.getuid()
            or (path_stat.st_mode & 0o777) != 0o600
            or path_stat.st_size <= 0
            or path_stat.st_size > self.MAX_EVENT_BYTES
        ):
            raise WorkflowStateError("workflow decision inbox entry is unsafe")
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
        try:
            descriptor = os.open(path, flags)
            with os.fdopen(descriptor, "r", encoding="utf-8") as handle:
                opened_stat = os.fstat(handle.fileno())
                if (
                    opened_stat.st_dev != path_stat.st_dev
                    or opened_stat.st_ino != path_stat.st_ino
                    or opened_stat.st_size != path_stat.st_size
                ):
                    raise WorkflowStateError(
                        "workflow decision inbox entry changed unexpectedly"
                    )
                envelope = json.load(handle)
        except WorkflowStateError:
            raise
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise WorkflowStateError("workflow decision inbox entry is unreadable") from exc
        if not isinstance(envelope, dict):
            raise WorkflowStateError("workflow decision inbox entry is invalid")
        header = envelope.get("header")
        event = envelope.get("event")
        if not isinstance(header, dict) or not isinstance(event, dict):
            raise WorkflowStateError("workflow decision inbox entry is invalid")
        event_id = header.get("event_id")
        action = event.get("action")
        context = event.get("context")
        operator = event.get("operator")
        if (
            not self._valid_event_id(event_id)
            or header.get("event_type") != "card.action.trigger"
            or path.name != self._event_filename(event_id)
            or not isinstance(action, dict)
            or not isinstance(action.get("value"), dict)
            or action["value"].get("action") != "workflow_decision"
            or not isinstance(context, dict)
            or not isinstance(operator, dict)
        ):
            raise WorkflowStateError("workflow decision inbox entry is invalid")
        normalized = {
            "type": "card.action.trigger",
            "event_id": event_id,
            "timestamp": str(header.get("create_time") or ""),
            "operator_id": str(operator.get("open_id") or ""),
            "message_id": str(context.get("open_message_id") or ""),
            "chat_id": str(context.get("open_chat_id") or ""),
            "host": str(event.get("host") or ""),
            "token": str(event.get("token") or ""),
            "action_tag": str(action.get("tag") or ""),
            "action_value": json.dumps(
                action["value"],
                ensure_ascii=False,
                separators=(",", ":"),
            ),
            "action_name": str(action.get("name") or ""),
            "_workflow_inbox_event_id": event_id,
        }
        return event_id, normalized

    def pending(self) -> list[dict[str, Any]]:
        with self._lock:
            self._validate_directory()
            try:
                paths = sorted(self.directory.iterdir())
            except OSError as exc:
                raise WorkflowStateError(
                    "workflow decision inbox is unavailable"
                ) from exc
            events: list[dict[str, Any]] = []
            for path in paths:
                if path.name.startswith("."):
                    continue
                _event_id, normalized = self._read(path)
                events.append(normalized)
            return events

    def acknowledge(self, event_id: str) -> None:
        with self._lock:
            self._validate_directory()
            path = self.path_for_event(event_id)
            try:
                self._read(path)
            except WorkflowStateError:
                if not os.path.lexists(path):
                    return
                raise
            try:
                path.unlink()
                descriptor = os.open(
                    self.directory,
                    os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
                )
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
            except OSError as exc:
                raise WorkflowStateError(
                    "workflow decision inbox acknowledgement failed"
                ) from exc


def _identifier(payload: dict[str, Any], name: str) -> str:
    value = payload.get(name)
    if not isinstance(value, str) or not IDENTIFIER_PATTERN.fullmatch(value):
        raise WorkflowNotificationError(f"{name} is invalid")
    return value


def _text(payload: dict[str, Any], name: str, limit: int) -> str:
    value = payload.get(name)
    if not isinstance(value, str):
        raise WorkflowNotificationError(f"{name} must be a string")
    normalized = value.strip()
    if not normalized or len(normalized) > limit:
        raise WorkflowNotificationError(f"{name} is invalid")
    return normalized


def _visible_text(payload: dict[str, Any], name: str, limit: int) -> str:
    normalized = _text(payload, name, limit)
    if any(pattern.search(normalized) for pattern in RESTRICTED_VISIBLE_TEXT_PATTERNS):
        raise WorkflowNotificationError(f"{name} contains restricted data")
    return normalized


def _workbench_url(payload: dict[str, Any], workflow_id: str) -> str:
    value = _text(payload, "workbench_url", 1000)
    workbench_path = WORKFLOW_WORKBENCH_PATHS.get(workflow_id)
    if workbench_path is None:
        raise WorkflowNotificationError("workflow_id is not allowed")
    parsed = urlsplit(value)
    try:
        port = parsed.port
    except ValueError as exc:
        raise WorkflowNotificationError("workbench_url is not an allowed workbench URL") from exc
    decoded_path = unquote(parsed.path)
    path_matches = decoded_path == workbench_path or decoded_path.startswith(
        workbench_path.rstrip("/") + "/"
    )
    child_path = (
        decoded_path[len(workbench_path.rstrip("/")) :]
        if path_matches
        else decoded_path
    )
    child_segments = [segment for segment in child_path.split("/") if segment]
    if (
        parsed.scheme != "https"
        or (parsed.hostname or "").lower() != WORKBENCH_HOST
        or port is not None
        or parsed.username
        or parsed.password
        or parsed.query
        or parsed.fragment
        or not path_matches
        or "\\" in decoded_path
        or "//" in decoded_path
        or any(
            segment in {".", ".."}
            or not SAFE_PATH_SEGMENT_PATTERN.fullmatch(segment)
            for segment in child_segments
        )
    ):
        raise WorkflowNotificationError("workbench_url is not an allowed workbench URL")
    return value


def _actions(payload: dict[str, Any], status: str) -> list[dict[str, Any]]:
    raw_actions = payload.get("actions")
    if not isinstance(raw_actions, list):
        raise WorkflowNotificationError("actions must be a list")
    if status == "milestone_completed":
        if raw_actions:
            raise WorkflowNotificationError("milestone_completed cannot include actions")
        return []
    if not 2 <= len(raw_actions) <= 5:
        raise WorkflowNotificationError("user_action_required needs 2 to 5 actions")

    actions: list[dict[str, Any]] = []
    seen: set[str] = set()
    recommended_count = 0
    for raw_action in raw_actions:
        if not isinstance(raw_action, dict) or set(raw_action) != ACTION_FIELDS:
            raise WorkflowNotificationError("each action must use the documented fields")
        action_id = _identifier(raw_action, "id")
        if action_id in seen:
            raise WorkflowNotificationError("action ids must be unique")
        seen.add(action_id)
        label = _visible_text(raw_action, "label", 80)
        description = _visible_text(raw_action, "description", 500)
        recommended = raw_action.get("recommended")
        if not isinstance(recommended, bool):
            raise WorkflowNotificationError("recommended must be boolean")
        resolution = raw_action.get("resolution")
        if resolution not in ALLOWED_RESOLUTIONS:
            raise WorkflowNotificationError("resolution is not allowed")
        recommended_count += int(recommended)
        actions.append(
            {
                "id": action_id,
                "label": label,
                "description": description,
                "recommended": recommended,
                "resolution": resolution,
            }
        )
    if recommended_count != 1:
        raise WorkflowNotificationError("exactly one action must be recommended")
    return actions


def validate_payload(payload: Any) -> dict[str, Any]:
    if not isinstance(payload, dict) or set(payload) != INPUT_FIELDS:
        raise WorkflowNotificationError("request must contain exactly the documented fields")
    status = payload.get("status")
    if status not in ALLOWED_STATUSES:
        raise WorkflowNotificationError("status is not allowed")
    workflow_id = _identifier(payload, "workflow_id")
    if workflow_id not in WORKFLOW_WORKBENCH_PATHS:
        raise WorkflowNotificationError("workflow_id is not allowed")
    normalized = {
        "workflow_id": workflow_id,
        "event_id": _identifier(payload, "event_id"),
        "task_id": _identifier(payload, "task_id"),
        "status": status,
        "summary": _visible_text(payload, "summary", 2000),
        "workbench_url": _workbench_url(payload, workflow_id),
    }
    normalized["actions"] = _actions(payload, status)
    return normalized


def event_key(workflow_id: str, event_id: str) -> str:
    return hashlib.sha256(f"{workflow_id}:{event_id}".encode("utf-8")).hexdigest()


def payload_digest(payload: dict[str, Any]) -> str:
    encoded = json.dumps(
        payload,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def delivery_delay(attempts: int) -> int:
    return DELIVERY_DELAYS[min(max(attempts, 0), len(DELIVERY_DELAYS) - 1)]


class WorkflowStore:
    """Atomic 0600 store for notifications, decisions, and recovery delivery."""

    def __init__(self, path: Path):
        self.path = path
        self._lock = threading.RLock()

    def _empty(self) -> dict[str, Any]:
        return {"version": 1, "notifications": {}, "recoveries": {}}

    @staticmethod
    def _number(value: Any) -> bool:
        return (
            isinstance(value, (int, float))
            and not isinstance(value, bool)
            and math.isfinite(float(value))
        )

    def _validate_notification(self, key: str, record: Any) -> None:
        if not STATE_KEY_PATTERN.fullmatch(key) or not isinstance(record, dict):
            raise WorkflowStateError("workflow state schema is invalid")
        try:
            payload = validate_payload(
                {field: record[field] for field in INPUT_FIELDS}
            )
        except (KeyError, WorkflowNotificationError) as exc:
            raise WorkflowStateError("workflow state schema is invalid") from exc
        if (
            event_key(payload["workflow_id"], payload["event_id"]) != key
            or record.get("payload_digest") != payload_digest(payload)
            or record.get("delivery_status") not in {"pending", "sent"}
            or record.get("decision_status")
            not in {"pending", "not_applicable", "consumed"}
            or not self._number(record.get("created_at"))
            or not isinstance(record.get("delivery_attempts"), int)
            or isinstance(record.get("delivery_attempts"), bool)
            or int(record["delivery_attempts"]) < 0
            or not self._number(record.get("next_delivery_at"))
        ):
            raise WorkflowStateError("workflow state schema is invalid")
        requires_action = payload["status"] == "user_action_required"
        if requires_action != (record.get("decision_status") != "not_applicable"):
            raise WorkflowStateError("workflow state schema is invalid")
        if record.get("delivery_status") == "sent" and (
            not self._number(record.get("delivered_at"))
            or not isinstance(record.get("message_id"), str)
            or not record.get("message_id")
            or not isinstance(record.get("chat_id"), str)
            or not record.get("chat_id")
        ):
            raise WorkflowStateError("workflow state schema is invalid")
        if requires_action:
            decision_source = record.get("decision_source")
            if decision_source is not None and (
                not isinstance(decision_source, dict)
                or set(decision_source) != {"kind", "id"}
                or decision_source.get("kind") not in {"card", "text"}
                or not isinstance(decision_source.get("id"), str)
                or not decision_source["id"]
                or len(decision_source["id"]) > 512
                or any(character in decision_source["id"] for character in "\r\n")
            ):
                raise WorkflowStateError("workflow state schema is invalid")
            reminder_status = record.get("reminder_status")
            if reminder_status not in {
                "not_scheduled",
                "pending",
                "sent",
                "canceled",
            }:
                raise WorkflowStateError("workflow state schema is invalid")
            decision_status = record.get("decision_status")
            if decision_status == "pending" and (
                not isinstance(record.get("decision_token"), str)
                or not record.get("decision_token")
                or reminder_status == "canceled"
                or (
                    record.get("delivery_status") == "pending"
                    and reminder_status != "not_scheduled"
                )
                or (
                    reminder_status == "pending"
                    and not self._number(record.get("next_reminder_at"))
                )
                or (
                    reminder_status == "sent"
                    and (
                        not self._number(record.get("reminder_sent_at"))
                        or not isinstance(record.get("reminder_message_id"), str)
                        or not record.get("reminder_message_id")
                        or not isinstance(record.get("reminder_chat_id"), str)
                        or not record.get("reminder_chat_id")
                    )
                )
            ):
                raise WorkflowStateError("workflow state schema is invalid")
            if decision_status == "consumed":
                selected = record.get("selected_action")
                matching_action = next(
                    (
                        action
                        for action in payload["actions"]
                        if action.get("id")
                        == (selected.get("id") if isinstance(selected, dict) else None)
                    ),
                    None,
                )
                if (
                    reminder_status != "canceled"
                    or "decision_token" in record
                    or matching_action != selected
                ):
                    raise WorkflowStateError("workflow state schema is invalid")
            elif decision_source is not None:
                raise WorkflowStateError("workflow state schema is invalid")

    def _validate_recovery(
        self,
        key: str,
        recovery: Any,
        notifications: dict[str, Any],
    ) -> None:
        notification = notifications.get(key)
        if (
            not STATE_KEY_PATTERN.fullmatch(key)
            or not isinstance(recovery, dict)
            or not isinstance(notification, dict)
        ):
            raise WorkflowStateError("workflow state schema is invalid")
        selected = recovery.get("selected_action")
        if not isinstance(selected, dict):
            raise WorkflowStateError("workflow state schema is invalid")
        matching_action = next(
            (
                action
                for action in notification.get("actions", [])
                if isinstance(action, dict) and action.get("id") == selected.get("id")
            ),
            None,
        )
        if (
            recovery.get("workflow_id") != notification.get("workflow_id")
            or recovery.get("event_id") != notification.get("event_id")
            or recovery.get("attention_request_id") != notification.get("event_id")
            or recovery.get("task_id") != notification.get("task_id")
            or recovery.get("summary") != notification.get("summary")
            or recovery.get("workbench_url") != notification.get("workbench_url")
            or notification.get("decision_status") != "consumed"
            or matching_action != selected
            or recovery.get("selected_action_id") != selected.get("id")
            or recovery.get("selected_action_label") != selected.get("label")
            or recovery.get("resolution") != selected.get("resolution")
            or recovery.get("marker")
            not in {
                f"ori-one-workflow-recovery:{key}",
                f"workflow-recovery:{key}",
            }
            or recovery.get("status")
            not in {"pending", "delivered", "delivery_unknown"}
            or not isinstance(recovery.get("attempts"), int)
            or isinstance(recovery.get("attempts"), bool)
            or int(recovery["attempts"]) < 0
            or not self._number(recovery.get("created_at"))
        ):
            raise WorkflowStateError("workflow state schema is invalid")
        if recovery.get("status") == "pending" and not self._number(
            recovery.get("next_attempt_at")
        ):
            raise WorkflowStateError("workflow state schema is invalid")
        if recovery.get("status") == "delivered" and (
            not self._number(recovery.get("delivered_at"))
            or not isinstance(recovery.get("turn_id"), str)
            or not recovery.get("turn_id")
        ):
            raise WorkflowStateError("workflow state schema is invalid")

    def _validate_state(self, state: Any) -> dict[str, Any]:
        if (
            not isinstance(state, dict)
            or set(state) != {"version", "notifications", "recoveries"}
            or type(state.get("version")) is not int
            or state.get("version") != 1
            or not isinstance(state.get("notifications"), dict)
            or not isinstance(state.get("recoveries"), dict)
        ):
            raise WorkflowStateError("workflow state schema is invalid")
        notifications = state["notifications"]
        recoveries = state["recoveries"]
        for key, record in notifications.items():
            if not isinstance(key, str):
                raise WorkflowStateError("workflow state schema is invalid")
            self._validate_notification(key, record)
        for key, recovery in recoveries.items():
            if not isinstance(key, str):
                raise WorkflowStateError("workflow state schema is invalid")
            self._validate_recovery(key, recovery, notifications)
        for key, record in notifications.items():
            has_recovery = key in recoveries
            if (record.get("decision_status") == "consumed") != has_recovery:
                raise WorkflowStateError("workflow state schema is invalid")
        return state

    def _validate_private_parent(self, create: bool = False) -> None:
        if create:
            try:
                self.path.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
            except OSError as exc:
                raise WorkflowStateError("workflow state directory is unavailable") from exc
        try:
            parent_stat = self.path.parent.lstat()
        except OSError as exc:
            raise WorkflowStateError("workflow state directory is unavailable") from exc
        if (
            not stat.S_ISDIR(parent_stat.st_mode)
            or self.path.parent.is_symlink()
            or parent_stat.st_uid != os.getuid()
            or (parent_stat.st_mode & 0o777) != 0o700
        ):
            raise WorkflowStateError("workflow state directory is unsafe")

    def _validated_existing_stat(self) -> os.stat_result:
        try:
            path_stat = self.path.lstat()
        except FileNotFoundError:
            raise
        except OSError as exc:
            raise WorkflowStateError("workflow state is unavailable") from exc
        if (
            not stat.S_ISREG(path_stat.st_mode)
            or self.path.is_symlink()
            or path_stat.st_uid != os.getuid()
            or (path_stat.st_mode & 0o777) != 0o600
        ):
            raise WorkflowStateError("workflow state file is unsafe")
        return path_stat

    def load(self) -> dict[str, Any]:
        with self._lock:
            try:
                path_stat = self._validated_existing_stat()
            except FileNotFoundError:
                try:
                    self.path.parent.lstat()
                except FileNotFoundError:
                    return self._empty()
                except OSError as exc:
                    raise WorkflowStateError(
                        "workflow state directory is unavailable"
                    ) from exc
                self._validate_private_parent(create=False)
                return self._empty()
            self._validate_private_parent(create=False)
            flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
            try:
                descriptor = os.open(self.path, flags)
                with os.fdopen(descriptor, "r", encoding="utf-8") as handle:
                    opened_stat = os.fstat(handle.fileno())
                    if (
                        opened_stat.st_dev != path_stat.st_dev
                        or opened_stat.st_ino != path_stat.st_ino
                        or not stat.S_ISREG(opened_stat.st_mode)
                        or opened_stat.st_uid != os.getuid()
                        or (opened_stat.st_mode & 0o777) != 0o600
                    ):
                        raise WorkflowStateError("workflow state file changed unexpectedly")
                    state = json.load(handle)
            except WorkflowStateError:
                raise
            except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
                raise WorkflowStateError("workflow state is unreadable") from exc
            return self._validate_state(state)

    def save(self, state: dict[str, Any]) -> None:
        with self._lock:
            self._validate_state(state)
            self._validate_private_parent(create=True)
            try:
                existing_stat = self._validated_existing_stat()
            except FileNotFoundError:
                existing_stat = None
            if existing_stat is not None:
                self.load()
                existing_stat = self._validated_existing_stat()
            temporary = self.path.parent / (
                f".{self.path.name}.{os.getpid()}.{secrets.token_hex(8)}.tmp"
            )
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
            try:
                descriptor = os.open(temporary, flags, 0o600)
                with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
                    json.dump(state, handle, ensure_ascii=False, indent=2)
                    handle.write("\n")
                    handle.flush()
                    os.fsync(handle.fileno())
                if existing_stat is not None:
                    current_stat = self._validated_existing_stat()
                    if (
                        current_stat.st_dev != existing_stat.st_dev
                        or current_stat.st_ino != existing_stat.st_ino
                    ):
                        raise WorkflowStateError(
                            "workflow state file changed unexpectedly"
                        )
                else:
                    try:
                        self.path.lstat()
                    except FileNotFoundError:
                        pass
                    except OSError as exc:
                        raise WorkflowStateError("workflow state is unavailable") from exc
                    else:
                        raise WorkflowStateError(
                            "workflow state file changed unexpectedly"
                        )
                os.replace(temporary, self.path)
                directory_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0)
                directory_descriptor = os.open(self.path.parent, directory_flags)
                try:
                    os.fsync(directory_descriptor)
                finally:
                    os.close(directory_descriptor)
                self._validated_existing_stat()
            except Exception:
                temporary.unlink(missing_ok=True)
                raise

    def enqueue(
        self,
        payload: dict[str, Any],
        allowed_workflow_ids: str | set[str] | frozenset[str],
        now: float | None = None,
    ) -> str:
        normalized = validate_payload(payload)
        allowed = (
            {allowed_workflow_ids}
            if isinstance(allowed_workflow_ids, str)
            else set(allowed_workflow_ids)
        )
        if normalized["workflow_id"] not in allowed:
            raise WorkflowNotificationError("workflow_id is not allowed")
        timestamp = time.time() if now is None else now
        key = event_key(normalized["workflow_id"], normalized["event_id"])
        digest = payload_digest(normalized)
        with self._lock:
            state = self.load()
            notifications = state["notifications"]
            existing = notifications.get(key)
            if isinstance(existing, dict):
                if existing.get("payload_digest") != digest:
                    raise WorkflowNotificationError("event_id conflicts with an existing payload")
                return "duplicate"
            if len(notifications) >= MAX_NOTIFICATIONS:
                removable = sorted(
                    (
                        item_key,
                        float(item.get("created_at") or 0),
                    )
                    for item_key, item in notifications.items()
                    if isinstance(item, dict)
                    and item.get("delivery_status") == "sent"
                    and item.get("decision_status") in {"not_applicable", "consumed"}
                )
                while len(notifications) >= MAX_NOTIFICATIONS and removable:
                    old_key = removable.pop(0)[0]
                    notifications.pop(old_key, None)
                    recovery = state["recoveries"].get(old_key)
                    if isinstance(recovery, dict) and recovery.get("status") == "delivered":
                        state["recoveries"].pop(old_key, None)
                if len(notifications) >= MAX_NOTIFICATIONS:
                    raise WorkflowNotificationError("workflow outbox is full")
            record = {
                **normalized,
                "payload_digest": digest,
                "created_at": timestamp,
                "delivery_status": "pending",
                "delivery_attempts": 0,
                "next_delivery_at": timestamp,
                "decision_status": (
                    "pending"
                    if normalized["status"] == "user_action_required"
                    else "not_applicable"
                ),
            }
            if normalized["status"] == "user_action_required":
                record["decision_token"] = secrets.token_urlsafe(32)
                record["reminder_status"] = "not_scheduled"
            notifications[key] = record
            self.save(state)
        return "queued"

    def due_delivery(self, now: float | None = None) -> tuple[str, str, dict[str, Any]] | None:
        timestamp = time.time() if now is None else now
        with self._lock:
            state = self.load()
            for key, record in state["notifications"].items():
                if not isinstance(record, dict):
                    continue
                if (
                    record.get("delivery_status") == "pending"
                    and float(record.get("next_delivery_at") or 0) <= timestamp
                ):
                    return "initial", key, dict(record)
                if (
                    record.get("decision_status") == "pending"
                    and record.get("reminder_status") == "pending"
                    and float(record.get("next_reminder_at") or 0) <= timestamp
                ):
                    return "reminder", key, dict(record)
        return None

    def delivery_succeeded(
        self,
        key: str,
        kind: str,
        message_id: str,
        chat_id: str,
        reminder_after_seconds: int,
        now: float | None = None,
    ) -> None:
        timestamp = time.time() if now is None else now
        with self._lock:
            state = self.load()
            record = state["notifications"].get(key)
            if not isinstance(record, dict):
                return
            if kind == "reminder":
                record["reminder_status"] = "sent"
                record["reminder_sent_at"] = timestamp
                record["reminder_message_id"] = message_id
                record["reminder_chat_id"] = chat_id
            else:
                record["delivery_status"] = "sent"
                record["delivered_at"] = timestamp
                record["message_id"] = message_id
                record["chat_id"] = chat_id
                if record.get("decision_status") == "pending":
                    record["reminder_status"] = "pending"
                    record["next_reminder_at"] = timestamp + reminder_after_seconds
            record.pop("last_failure", None)
            self.save(state)

    def delivery_failed(
        self,
        key: str,
        kind: str,
        reason: str,
        now: float | None = None,
    ) -> None:
        timestamp = time.time() if now is None else now
        with self._lock:
            state = self.load()
            record = state["notifications"].get(key)
            if not isinstance(record, dict):
                return
            counter = "reminder_attempts" if kind == "reminder" else "delivery_attempts"
            schedule = "next_reminder_at" if kind == "reminder" else "next_delivery_at"
            attempts = int(record.get(counter) or 0) + 1
            record[counter] = attempts
            record[schedule] = timestamp + delivery_delay(attempts)
            record["last_failure"] = reason[:160]
            self.save(state)

    def _consume(
        self,
        state: dict[str, Any],
        key: str,
        record: dict[str, Any],
        action_id: str,
        now: float,
        source_kind: str | None = None,
        source_id: str | None = None,
    ) -> tuple[str, dict[str, Any] | None]:
        if record.get("decision_status") == "consumed":
            selected = record.get("selected_action")
            source = record.get("decision_source")
            if (
                isinstance(selected, dict)
                and selected.get("id") == action_id
                and isinstance(source, dict)
                and source == {"kind": source_kind, "id": source_id}
            ):
                recovery = state["recoveries"].get(key)
                return (
                    "consumed_retry",
                    dict(recovery) if isinstance(recovery, dict) else None,
                )
            return "already_consumed", None
        if (source_kind is None) != (source_id is None) or (
            source_kind is not None
            and (
                source_kind not in {"card", "text"}
                or not source_id
                or len(source_id) > 512
                or any(character in source_id for character in "\r\n")
            )
        ):
            return "invalid_source", None
        action = next(
            (
                item
                for item in record.get("actions", [])
                if isinstance(item, dict) and item.get("id") == action_id
            ),
            None,
        )
        if action is None or action.get("resolution") not in ALLOWED_RESOLUTIONS:
            return "unknown_action", None
        record["decision_status"] = "consumed"
        record["decision_consumed_at"] = now
        record["selected_action"] = dict(action)
        if source_kind is not None and source_id is not None:
            record["decision_source"] = {"kind": source_kind, "id": source_id}
        record["reminder_status"] = "canceled"
        record.pop("decision_token", None)
        recovery = {
            "workflow_id": record["workflow_id"],
            "event_id": record["event_id"],
            "attention_request_id": record["event_id"],
            "task_id": record["task_id"],
            "summary": record["summary"],
            "workbench_url": record["workbench_url"],
            "selected_action": dict(action),
            "selected_action_id": action["id"],
            "selected_action_label": action["label"],
            "resolution": action["resolution"],
            "marker": f"workflow-recovery:{key}",
            "status": "pending",
            "attempts": 0,
            "next_attempt_at": now,
            "created_at": now,
        }
        state["recoveries"][key] = recovery
        self.save(state)
        return "consumed", dict(recovery)

    def consume_token_decision(
        self,
        workflow_id: str,
        event_id: str,
        token: str,
        action_id: str,
        now: float | None = None,
        source_id: str | None = None,
    ) -> tuple[str, dict[str, Any] | None]:
        timestamp = time.time() if now is None else now
        key = event_key(workflow_id, event_id)
        with self._lock:
            state = self.load()
            record = state["notifications"].get(key)
            if not isinstance(record, dict):
                return "not_found", None
            if record.get("decision_status") == "consumed":
                return self._consume(
                    state,
                    key,
                    record,
                    action_id,
                    timestamp,
                    "card" if source_id is not None else None,
                    source_id,
                )
            expected = str(record.get("decision_token") or "")
            if not expected or not hmac.compare_digest(expected, token):
                return "invalid_token", None
            return self._consume(
                state,
                key,
                record,
                action_id,
                timestamp,
                "card" if source_id is not None else None,
                source_id,
            )

    def consume_reply_decision(
        self,
        message_id: str,
        action_value: str,
        now: float | None = None,
        source_id: str | None = None,
    ) -> tuple[str, dict[str, Any] | None, dict[str, Any] | None]:
        timestamp = time.time() if now is None else now
        normalized = action_value.strip().casefold()
        with self._lock:
            state = self.load()
            record_entry = next(
                (
                    (key, item)
                    for key, item in state["notifications"].items()
                    if isinstance(item, dict)
                    and message_id in {
                        str(item.get("message_id") or ""),
                        str(item.get("reminder_message_id") or ""),
                    }
                ),
                None,
            )
            if record_entry is None:
                return "not_found", None, None
            key, record = record_entry
            actions = [item for item in record.get("actions", []) if isinstance(item, dict)]
            matches = [
                item
                for index, item in enumerate(actions, start=1)
                if normalized in {
                    str(item.get("id") or "").casefold(),
                    str(item.get("label") or "").casefold(),
                    str(index),
                }
            ]
            if len(matches) != 1:
                if record.get("decision_status") == "consumed":
                    return "already_consumed", None, dict(record)
                return "unknown_action", None, dict(record)
            result, recovery = self._consume(
                state,
                key,
                record,
                str(matches[0]["id"]),
                timestamp,
                "text" if source_id is not None else None,
                source_id,
            )
            return result, recovery, dict(record)

    def record_for_event(self, workflow_id: str, event_id: str) -> dict[str, Any] | None:
        with self._lock:
            record = self.load()["notifications"].get(event_key(workflow_id, event_id))
            return dict(record) if isinstance(record, dict) else None

    def record_for_message(self, message_id: str) -> dict[str, Any] | None:
        with self._lock:
            record = next(
                (
                    item
                    for item in self.load()["notifications"].values()
                    if isinstance(item, dict)
                    and message_id in {
                        str(item.get("message_id") or ""),
                        str(item.get("reminder_message_id") or ""),
                    }
                ),
                None,
            )
            return dict(record) if isinstance(record, dict) else None

    def due_recovery(self, now: float | None = None) -> tuple[str, dict[str, Any]] | None:
        timestamp = time.time() if now is None else now
        with self._lock:
            state = self.load()
            pending = sorted(
                (
                    float(recovery.get("created_at") or 0),
                    key,
                    recovery,
                )
                for key, recovery in state["recoveries"].items()
                if isinstance(recovery, dict)
                and recovery.get("status") == "pending"
            )
            if pending:
                _created_at, key, recovery = pending[0]
                if float(recovery.get("next_attempt_at") or 0) > timestamp:
                    return None
                return key, dict(recovery)
        return None

    def unknown_recoveries(self) -> list[tuple[str, dict[str, Any]]]:
        with self._lock:
            state = self.load()
            return [
                (key, dict(recovery))
                for _created_at, key, recovery in sorted(
                    (
                        float(recovery.get("created_at") or 0),
                        key,
                        recovery,
                    )
                    for key, recovery in state["recoveries"].items()
                    if isinstance(recovery, dict)
                    and recovery.get("status") == "delivery_unknown"
                )
            ]

    def safe_status(self) -> dict[str, int]:
        with self._lock:
            state = self.load()
            notifications = [
                item
                for item in state["notifications"].values()
                if isinstance(item, dict)
            ]
            recoveries = [
                item
                for item in state["recoveries"].values()
                if isinstance(item, dict)
            ]
            return {
                "pending_notifications": sum(
                    item.get("delivery_status") == "pending" for item in notifications
                ),
                "pending_decisions": sum(
                    item.get("decision_status") == "pending" for item in notifications
                ),
                "pending_reminders": sum(
                    item.get("reminder_status") == "pending" for item in notifications
                ),
                "pending_recoveries": sum(
                    item.get("status") == "pending" for item in recoveries
                ),
                "delivery_unknown": sum(
                    item.get("status") == "delivery_unknown" for item in recoveries
                ),
            }

    def recovery_succeeded(
        self,
        key: str,
        turn_id: str,
        now: float | None = None,
    ) -> None:
        timestamp = time.time() if now is None else now
        with self._lock:
            state = self.load()
            recovery = state["recoveries"].get(key)
            if not isinstance(recovery, dict):
                return
            recovery["status"] = "delivered"
            recovery["delivered_at"] = timestamp
            recovery["turn_id"] = turn_id
            recovery.pop("last_failure", None)
            self.save(state)

    def recovery_failed(
        self,
        key: str,
        reason: str,
        retryable: bool,
        now: float | None = None,
    ) -> None:
        timestamp = time.time() if now is None else now
        with self._lock:
            state = self.load()
            recovery = state["recoveries"].get(key)
            if not isinstance(recovery, dict):
                return
            attempts = int(recovery.get("attempts") or 0) + 1
            recovery["attempts"] = attempts
            recovery["last_failure"] = reason[:160]
            if retryable:
                recovery["next_attempt_at"] = timestamp + delivery_delay(attempts)
            else:
                recovery["status"] = "delivery_unknown"
            self.save(state)
