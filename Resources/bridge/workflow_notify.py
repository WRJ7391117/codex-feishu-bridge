#!/usr/bin/env python3
"""Submit one Ori One workflow event to the local bridge over its Unix socket."""

from __future__ import annotations

import json
import os
from pathlib import Path
import socket
import sys
import uuid

from workflow_notifications import WorkflowNotificationError, validate_payload


MAX_REQUEST_BYTES = 64 * 1024
DEFAULT_SOCKET = (
    Path.home() / ".codex/feishu-bridge/workflow-notifications.sock"
)
DEFAULT_CONTROL_SOCKET = (
    Path.home() / ".codex/feishu-bridge/workflow-control.sock"
)


def read_payload() -> dict[str, object]:
    raw = sys.stdin.buffer.read(MAX_REQUEST_BYTES + 1)
    if not raw or len(raw) > MAX_REQUEST_BYTES:
        raise WorkflowNotificationError("invalid request")
    try:
        return validate_payload(json.loads(raw))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise WorkflowNotificationError("invalid request") from exc


def exchange(socket_path: Path, payload: dict[str, object]) -> dict[str, object]:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(35)
        connection.connect(str(socket_path))
        connection.sendall(
            json.dumps(
                payload,
                ensure_ascii=False,
                separators=(",", ":"),
            ).encode("utf-8")
            + b"\n"
        )
        response = connection.recv(MAX_REQUEST_BYTES)
    result = json.loads(response)
    if not isinstance(result, dict):
        raise ValueError("invalid response")
    return result


def print_result(result: dict[str, object]) -> None:
    print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))


def roundtrip_test_payload() -> dict[str, object]:
    return validate_payload(
        {
            "workflow_id": "ori-one-mind",
            "event_id": f"test-roundtrip-{uuid.uuid4()}",
            "task_id": "TEST-ROUNDTRIP",
            "status": "user_action_required",
            "summary": "验证飞书主动卡片、单次回复和专用 Codex Task 接收链路。",
            "workbench_url": "https://deepori.cn/ori-one/workbench/automation/",
            "actions": [
                {
                    "id": "confirm",
                    "label": "确认往返",
                    "description": "记录测试回执，不执行研发任务。",
                    "recommended": True,
                    "resolution": "resume",
                },
                {
                    "id": "cancel",
                    "label": "结束测试",
                    "description": "结束本次测试，不执行研发任务。",
                    "recommended": False,
                    "resolution": "pause",
                },
            ],
        }
    )


def main() -> int:
    arguments = sys.argv[1:]
    if arguments == ["--help"]:
        print(
            "用法：workflow-notify "
            "[--dry-run|--health|--status|--retry-outbox|--roundtrip-test]\n"
            "默认与 --dry-run 从 stdin 读取一个 JSON payload。"
        )
        return 0
    if len(arguments) > 1 or (arguments and arguments[0] not in {
        "--dry-run",
        "--health",
        "--status",
        "--retry-outbox",
        "--roundtrip-test",
    }):
        print_result({"ok": False, "error": "invalid_arguments"})
        return 2
    mode = arguments[0] if arguments else "--notify"
    if mode in {"--notify", "--dry-run"}:
        try:
            payload = read_payload()
        except WorkflowNotificationError:
            print_result({"ok": False, "error": "invalid_request"})
            return 2
        if mode == "--dry-run":
            print_result({"ok": True, "result": "valid"})
            return 0
        socket_path = Path(
            os.environ.get("CODEX_FEISHU_WORKFLOW_SOCKET", str(DEFAULT_SOCKET))
        ).expanduser()
        request: dict[str, object] = payload
    elif mode == "--roundtrip-test":
        socket_path = Path(
            os.environ.get("CODEX_FEISHU_WORKFLOW_SOCKET", str(DEFAULT_SOCKET))
        ).expanduser()
        request = roundtrip_test_payload()
    else:
        socket_path = Path(
            os.environ.get(
                "CODEX_FEISHU_WORKFLOW_CONTROL_SOCKET",
                str(DEFAULT_CONTROL_SOCKET),
            )
        ).expanduser()
        request = {"command": mode.removeprefix("--")}
    try:
        result = exchange(socket_path, request)
    except (OSError, socket.timeout):
        print_result({"ok": False, "error": "bridge_unavailable"})
        return 1
    except (json.JSONDecodeError, ValueError):
        print_result({"ok": False, "error": "invalid_response"})
        return 1
    print_result(result)
    return 0 if result.get("ok") is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
