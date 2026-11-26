import tempfile
import unittest
from pathlib import Path
from unittest import mock

from tests.test_runtime import load_bridge


class AppServerBackendTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.bridge.STATE_PATH = Path(directory.name) / "state.json"
        self.bridge.log = mock.Mock()
        self.bridge.write_runtime_status = mock.Mock()
        self.bridge.latest_task_turn = mock.Mock(return_value={
            "turn_id": "turn-before", "status": "completed", "cursor_offset": 0,
            "message": "previous result", "images": [],
        })
        self.bridge.schedule_promlight_task_status = mock.Mock()
        self.bridge.cli_resume_preflight = mock.Mock(return_value=(True, ""))
        self.bridge.desktop_connection_status = mock.Mock(return_value={
            "available": False, "reason": "ipc-not-listening",
        })
        self.client = mock.MagicMock()
        self.client.__enter__.return_value = self.client
        self.bridge.codex_app_server_client = mock.Mock(return_value=self.client)
        self.resumed = {
            "thread": {"id": "task-original"}, "cwd": "/workspace",
            "model": "model-original", "reasoningEffort": "high",
            "serviceTier": "priority", "approvalPolicy": "on-request",
            "sandbox": {"type": "workspaceWrite", "writableRoots": ["/workspace"]},
        }
        self.client.request.side_effect = self.request
        self.client.next_event.side_effect = [self.terminal()]

    def request(self, method, params=None, **kwargs):
        if method == "thread/read":
            return {"thread": {"id": "task-original", "cliVersion": "0.171.0", "status": {"type": "idle"}}}
        if method == "thread/resume":
            return self.resumed
        if method == "turn/start":
            return {"turn": {"id": "turn-1", "status": "inProgress"}}
        return {}

    def terminal(self, status="completed", items=None):
        return {"method": "turn/completed", "params": {
            "threadId": "task-original", "turn": {
                "id": "turn-1", "status": status,
                "items": items if items is not None else [{
                    "id": "final", "type": "agentMessage", "phase": "final_answer", "text": "done",
                }],
            },
        }}

    def registered_run(self, message_id="om_source", task_id="task-original"):
        run = self.bridge.new_run(
            "ou_admin", "oc_test", message_id,
            {"id": task_id, "title": "Original Task", "project": "Bridge"},
            [], [], "om_progress",
        )
        self.bridge._active_runs[run["run_id"]] = run
        return run

    def pending_start(self, *, request_sent=True):
        run = self.registered_run()
        captured = {}
        journal_before_send = []

        def request(method, params=None, **kwargs):
            if method == "turn/start":
                captured.update(params)
                journal_before_send.extend(self.bridge.load_state().get("recoverable_runs", {}))
                raise self.bridge.AppServerError("start failed", request_sent=request_sent)
            return self.request(method, params, **kwargs)

        self.client.request.side_effect = request
        result = self.bridge.run_codex_via_app_server("task-original", "hello")
        pending_id = "pending:" + captured["clientUserMessageId"]
        self.assertEqual(journal_before_send, [pending_id])
        return run, pending_id, captured, result

    def recovery_fixture(self, run):
        self.bridge._active_runs.clear()
        self.bridge._shared_target_ids = {"task-original", "other-task"}
        self.bridge.task_by_id = mock.Mock(return_value=run["task"])
        self.bridge.deliver_desktop_sync_result = mock.Mock()

    def matching_terminal(self, captured, *, task_id="task-original"):
        event = self.terminal(items=[
            {
                "type": "userMessage", "id": "input-item",
                "clientId": captured["clientUserMessageId"],
                "content": [{"type": "text", "text": "hello", "text_elements": []}],
            },
            {"id": "final", "type": "agentMessage", "phase": "final_answer", "text": "recovered result"},
        ])
        event["params"]["threadId"] = task_id
        return event

    def test_resumes_exact_task_and_inherits_all_settings(self):
        started, turn_started = mock.Mock(), mock.Mock()
        result = self.bridge.run_codex_via_app_server(
            "task-original", "hello", on_started=started, on_turn_started=turn_started,
        )
        self.assertEqual(result, (True, "done", []))
        calls = self.client.request.call_args_list
        self.assertEqual(calls[1].args, ("thread/resume", {"threadId": "task-original", "excludeTurns": True}))
        self.assertEqual(calls[2].args, ("turn/start", {
            "threadId": "task-original", "input": [{"type": "text", "text": "hello", "text_elements": []}],
        }))
        started.assert_called_once()
        turn_started.assert_called_once_with("turn-1")

    def test_new_desktop_without_legacy_listener_uses_supported_backend(self):
        self.bridge.run_codex_via_desktop = mock.Mock(return_value=("unavailable", "ipc-not-listening", []))
        self.bridge.run_codex_via_app_server = mock.Mock(return_value=(True, "done", []))
        result = self.bridge.run_codex("task-original", "hello")
        self.assertEqual(result, (True, "done", []))
        self.bridge.run_codex_via_desktop.assert_called_once()
        self.bridge.run_codex_via_app_server.assert_called_once()

    def test_uncertain_legacy_submission_never_switches_backend(self):
        self.bridge.run_codex_via_desktop = mock.Mock(return_value=("failed", "不会再次提交", []))
        self.bridge.run_codex_via_app_server = mock.Mock()
        self.assertFalse(self.bridge.run_codex("task-original", "hello")[0])
        self.bridge.run_codex_via_app_server.assert_not_called()

    def test_inputs_use_public_image_audio_and_file_schema(self):
        self.bridge.run_codex_via_app_server(
            "task-original", "hello", input_images=["/tmp/image.png"],
            input_files=[{"kind": "audio", "path": "/tmp/a.ogg", "label": "audio"},
                         {"kind": "file", "path": "/tmp/a.pdf", "label": "document"}],
        )
        request = self.client.request.call_args_list[2].args[1]
        self.assertEqual(request["input"][1:], [
            {"type": "localImage", "path": "/private/tmp/image.png"},
            {"type": "localAudio", "path": "/private/tmp/a.ogg"},
            {"type": "mention", "name": "document", "path": "/private/tmp/a.pdf"},
        ] if Path('/tmp').resolve() == Path('/private/tmp') else [
            {"type": "localImage", "path": "/tmp/image.png"},
            {"type": "localAudio", "path": "/tmp/a.ogg"},
            {"type": "mention", "name": "document", "path": "/tmp/a.pdf"},
        ])

    def test_duplicate_final_item_is_delivered_once(self):
        item = {"id": "final", "type": "agentMessage", "phase": "final_answer", "text": "done"}
        self.client.next_event.side_effect = [
            {"method": "item/completed", "params": {"threadId": "task-original", "turnId": "turn-1", "item": item}},
            self.terminal(items=[item]),
        ]
        self.assertEqual(self.bridge.run_codex_via_app_server("task-original", "hello")[1], "done")

    def test_zero_approval_request_id_is_preserved(self):
        approval = mock.Mock()
        self.client.next_event.side_effect = [{
            "id": 0, "method": "item/commandExecution/requestApproval",
            "params": {"threadId": "task-original", "turnId": "turn-1", "command": "pwd"},
        }, self.terminal()]
        self.assertTrue(self.bridge.run_codex_via_app_server("task-original", "hello", on_approval=approval)[0])
        self.assertEqual(approval.call_args.args[0]["protocol_request_id"], 0)

    def test_unknown_server_request_is_rejected_without_hanging(self):
        self.client.next_event.side_effect = [{"id": "request-x", "method": "unsupported", "params": {}}, self.terminal()]
        self.assertTrue(self.bridge.run_codex_via_app_server("task-original", "hello")[0])
        self.client.respond_error.assert_called_once()
        self.client.respond.assert_not_called()

    def test_approval_is_one_time_and_preserves_numeric_or_string_id(self):
        run = {"app_server_client": self.client}
        for kind, request_id in (("command", 0), ("file", "abc"), ("permission", 7)):
            with self.subTest(kind=kind):
                approval = {"type": kind, "protocol_request_id": request_id, "params": {"permissions": {"network": {"enabled": True}}}}
                self.assertTrue(self.bridge.respond_desktop_approval(run, approval, True))
                response = self.client.respond.call_args.args
                self.assertEqual(response[0], request_id)
                self.assertEqual(response[1], {"permissions": approval["params"]["permissions"], "scope": "turn"} if kind == "permission" else {"decision": "accept"})

    def test_stop_targets_original_turn_without_killing_daemon(self):
        run = {"app_server_client": self.client, "task": {"id": "task-original"}, "turn_id": "turn-1"}
        self.assertTrue(self.bridge.interrupt_desktop_turn(run))
        self.client.request.assert_called_once_with("turn/interrupt", {"threadId": "task-original", "turnId": "turn-1"}, timeout=10)
        self.client.close.assert_not_called()

    def test_running_task_is_not_resumed_or_started_again(self):
        self.client.request.side_effect = lambda *_args, **_kwargs: {"thread": {"id": "task-original", "status": {"type": "active"}}}
        result = self.bridge.run_codex_via_app_server("task-original", "hello")
        self.assertFalse(result[0])
        self.assertIn("正在运行", result[1])
        self.client.request.assert_called_once()

    def test_different_resume_task_is_rejected_before_submit(self):
        self.resumed["thread"]["id"] = "other-task"
        self.assertFalse(self.bridge.run_codex_via_app_server("task-original", "hello")[0])
        self.assertNotIn("turn/start", [call.args[0] for call in self.client.request.call_args_list])

    def test_start_timeout_is_never_retried(self):
        original = self.request
        def request(method, params=None, **kwargs):
            if method == "turn/start":
                raise self.bridge.AppServerError("timeout", request_sent=True)
            return original(method, params, **kwargs)
        self.client.request.side_effect = request
        result = self.bridge.run_codex_via_app_server("task-original", "hello")
        self.assertFalse(result[0])
        self.assertIn("不会再次提交", result[1])
        self.assertEqual([c.args[0] for c in self.client.request.call_args_list].count("turn/start"), 1)

    def test_start_timeout_keeps_pre_submit_journal_and_original_reply_route(self):
        run, pending_id, captured, result = self.pending_start()
        journal = self.bridge.load_state()["recoverable_runs"]
        self.assertFalse(result[0])
        self.assertTrue(run.get("submission_uncertain"))
        self.assertEqual(set(journal), {pending_id})
        self.assertEqual(journal[pending_id]["task"]["id"], "task-original")
        self.assertEqual(journal[pending_id]["source_message_id"], "om_source")
        self.assertEqual(journal[pending_id]["progress_message_id"], "om_progress")
        self.assertEqual(pending_id, "pending:" + captured["clientUserMessageId"])
        self.assertEqual([c.args[0] for c in self.client.request.call_args_list].count("turn/start"), 1)

    def test_definitely_unsent_start_failure_clears_journal_and_allows_next_message(self):
        run, _pending_id, _captured, result = self.pending_start(request_sent=False)
        self.assertFalse(result[0])
        self.assertFalse(run.get("submission_uncertain", False))
        self.assertEqual(self.bridge.load_state().get("recoverable_runs"), {})
        self.assertNotIn("不会再次提交", result[1])
        self.bridge._active_runs.clear()
        next_run = self.bridge.new_run(
            "ou_admin", "oc_test", "om_next", run["task"], [], [],
        )
        self.assertTrue(self.bridge.claim_active_run(next_run))

    def test_pending_journal_blocks_same_task_new_message_but_not_other_tasks(self):
        _run, pending_id, _captured, _result = self.pending_start()
        self.bridge._active_runs.clear()
        next_run = self.bridge.new_run(
            "ou_admin", "oc_test", "om_next", {"id": "task-original"}, [], [],
        )
        self.assertFalse(self.bridge.claim_active_run(next_run))
        self.assertEqual(next_run["queue_reason"], "unconfirmed_previous")
        other_run = self.bridge.new_run(
            "ou_admin", "oc_test", "om_other", {"id": "other-task"}, [], [],
        )
        self.assertTrue(self.bridge.claim_active_run(other_run))
        self.assertIn(pending_id, self.bridge.load_state()["recoverable_runs"])

    def test_pending_journal_does_not_dequeue_or_start_same_task_work(self):
        _run, pending_id, _captured, _result = self.pending_start()
        self.bridge._active_runs.clear()
        entry = {
            "queue_id": "queued-next", "user_id": "ou_admin", "chat_id": "oc_test",
            "source_message_id": "om_queued", "task": {"id": "task-original"},
            "content": "next", "ready": True, "available_at": 0,
        }
        state = self.bridge.load_state()
        state["pending_inputs"] = [entry]
        self.bridge.save_state(state)
        self.bridge.start_claimed_run = mock.Mock()
        self.assertFalse(self.bridge.start_next_queued_input("task-original"))
        self.assertEqual(self.bridge.load_state()["pending_inputs"], [entry])
        self.assertIn(pending_id, self.bridge.load_state()["recoverable_runs"])
        self.bridge.start_claimed_run.assert_not_called()

    def test_real_turn_client_id_recovers_original_message_only_once(self):
        run, pending_id, captured, _result = self.pending_start()
        self.recovery_fixture(run)
        event = self.matching_terminal(captured)
        self.bridge.handle_shared_codex_event(event)
        state = self.bridge.load_state()
        self.assertNotIn(pending_id, state["recoverable_runs"])
        self.assertEqual(state["recoverable_runs"]["turn-1"]["turn_id"], "turn-1")
        self.assertEqual(state["bridge_turn_owners"]["turn-1"], "ou_admin")
        self.bridge.advance_task_turn = mock.Mock(return_value={
            "status": "completed", "message": "recovered result", "images": [],
        })
        self.assertTrue(self.bridge.retry_recoverable_runs())
        self.bridge.handle_shared_codex_event(event)
        self.assertFalse(self.bridge.retry_recoverable_runs())
        delivered = self.bridge.deliver_desktop_sync_result.call_args
        self.assertEqual(delivered.args[:3], ("ou_admin", run["task"], "om_progress"))
        self.assertEqual(delivered.args[3]["message"], "recovered result")
        self.assertEqual(delivered.kwargs["reply_message_id"], "om_source")
        self.assertEqual(self.bridge.advance_task_turn.call_args.args[:2], ("task-original", "turn-1"))
        self.bridge.deliver_desktop_sync_result.assert_called_once()
        self.assertEqual(self.bridge.load_state().get("recoverable_runs"), {})
        self.assertEqual([c.args[0] for c in self.client.request.call_args_list].count("turn/start"), 1)

    def test_matching_client_id_in_another_task_cannot_resolve_pending_submission(self):
        run, pending_id, captured, _result = self.pending_start()
        self.recovery_fixture(run)
        original = self.bridge.load_state()["recoverable_runs"][pending_id]
        self.bridge.handle_shared_codex_event(self.matching_terminal(captured, task_id="other-task"))
        state = self.bridge.load_state()
        self.assertEqual(state["recoverable_runs"], {pending_id: original})
        self.assertNotIn("turn-1", state.get("bridge_turn_owners", {}))
        self.bridge.deliver_desktop_sync_result.assert_not_called()

    def test_other_client_message_id_does_not_resolve_original_submission(self):
        run, pending_id, _captured, _result = self.pending_start()
        self.recovery_fixture(run)
        self.bridge.handle_shared_codex_event(self.matching_terminal({"clientUserMessageId": "other-message"}))
        self.assertEqual(set(self.bridge.load_state()["recoverable_runs"]), {pending_id})
        self.bridge.deliver_desktop_sync_result.assert_not_called()

    def test_reconnect_reconciles_completed_submission_from_paginated_history_without_replay(self):
        run, pending_id, captured, _result = self.pending_start()
        self.recovery_fixture(run)
        created_at = self.bridge.load_state()["recoverable_runs"][pending_id]["created_at"]
        matched = self.matching_terminal(captured)["params"]["turn"]
        matched["startedAt"] = int(created_at)
        unrelated = {
            "id": "newer-turn", "status": "completed", "startedAt": int(created_at) + 10,
            "items": [{"type": "userMessage", "id": "new-input", "clientId": "other-message", "content": []}],
        }

        def history_request(method, params=None, **kwargs):
            if method == "turn/start":
                self.fail("reconciliation must never submit the original message again")
            if method == "thread/turns/list":
                if params.get("cursor") == "before-target":
                    return {"data": [matched], "nextCursor": None}
                return {"data": [unrelated], "nextCursor": "before-target"}
            return self.request(method, params, **kwargs)

        self.client.request.side_effect = history_request
        self.bridge.advance_task_turn = mock.Mock(side_effect=lambda _task, turn, *_args: {
            "status": "completed", "message": "recovered result", "images": [],
        } if turn == "turn-1" else {"status": "missing"})
        self.assertTrue(self.bridge.retry_recoverable_runs())
        self.bridge.deliver_desktop_sync_result.assert_called_once()
        self.assertEqual(self.bridge.deliver_desktop_sync_result.call_args.args[3]["message"], "recovered result")
        self.assertEqual(self.bridge.load_state().get("recoverable_runs"), {})
        history_calls = [call.args[1] for call in self.client.request.call_args_list if call.args[0] == "thread/turns/list"]
        self.assertEqual(len(history_calls), 2)
        self.assertTrue(all(params["itemsView"] == "full" for params in history_calls))
        self.assertEqual(history_calls[1]["cursor"], "before-target")
        self.assertFalse(self.bridge.retry_recoverable_runs())

    def test_unmatched_pending_submission_survives_sixty_second_missing_window(self):
        run, pending_id, _captured, _result = self.pending_start()
        self.recovery_fixture(run)
        created_at = self.bridge.load_state()["recoverable_runs"][pending_id]["created_at"]

        def no_match(method, params=None, **kwargs):
            if method == "turn/start":
                self.fail("an uncertain submission cannot be replayed")
            if method == "thread/turns/list":
                return {"data": [], "nextCursor": None}
            return self.request(method, params, **kwargs)

        self.client.request.side_effect = no_match
        self.bridge.advance_task_turn = mock.Mock(return_value={"status": "missing"})
        self.bridge.retry_recoverable_runs(now=created_at)
        self.bridge.retry_recoverable_runs(now=created_at + 61)
        self.assertIn(pending_id, self.bridge.load_state()["recoverable_runs"])
        self.bridge.deliver_desktop_sync_result.assert_not_called()

    def test_completion_event_before_start_ack_is_cleaned_after_normal_delivery(self):
        run = self.registered_run()
        self.bridge._shared_target_ids = {"task-original"}
        original = self.request

        def early_completion(method, params=None, **kwargs):
            if method == "turn/start":
                self.bridge.handle_shared_codex_event(self.matching_terminal(params))
            return original(method, params, **kwargs)

        self.client.request.side_effect = early_completion
        self.bridge.run_codex_via_desktop = mock.Mock(return_value=("unavailable", "ipc-not-listening", []))
        self.bridge.result_roots_for_task = mock.Mock(return_value=[])
        self.bridge.restore_pending_task_name = mock.Mock(return_value=True)
        self.bridge.update_current_status_card = mock.Mock()
        self.bridge.schedule_user_task_identity_refresh = mock.Mock()
        self.bridge.deliver_result_resources = mock.Mock(return_value=([], [], []))
        self.bridge.reply_or_queue = mock.Mock(return_value=True)
        self.bridge.record_task_exchange = mock.Mock()
        self.bridge.follow_result_task = mock.Mock(return_value=False)
        self.bridge.set_run_progress = mock.Mock()
        self.bridge.start_next_queued_input = mock.Mock()
        self.bridge.deliver_desktop_sync_result = mock.Mock()
        self.bridge.process_message_run(run, "hello", [], [], '{"text":"hello"}', "text")
        self.bridge.reply_or_queue.assert_called_once()
        self.assertEqual(self.bridge.load_state().get("recoverable_runs"), {})
        self.assertFalse(self.bridge.retry_recoverable_runs())
        self.bridge.deliver_desktop_sync_result.assert_not_called()

    def test_active_settings_are_saved_without_resuming_or_interrupting(self):
        run = self.registered_run()
        run.update({"app_server_client": self.client, "app_server_settings": self.resumed})
        self.assertEqual(self.bridge.app_server_task_operation("task-original"), self.resumed)
        self.bridge.update_desktop_task_settings("task-original", model="next", effort="medium")
        self.assertEqual(self.bridge.load_state()["pending_task_settings"], {
            "task-original": {"model": "next", "effort": "medium"},
        })
        self.assertEqual(run["app_server_settings"]["model"], "model-original")
        self.assertEqual(run["app_server_settings"]["reasoningEffort"], "high")
        self.client.request.assert_not_called()
        self.client.close.assert_not_called()
        self.bridge.codex_app_server_client.assert_not_called()

    def test_next_turn_applies_only_explicit_settings_and_clears_them_after_ack(self):
        self.bridge.update_desktop_task_settings("task-original", model="next", effort="medium")
        self.bridge.update_desktop_task_settings("other-task", effort="low")
        run = self.registered_run()
        pending_at_start = []

        def request(method, params=None, **kwargs):
            if method == "turn/start":
                pending_at_start.append(self.bridge.load_state()["pending_task_settings"]["task-original"])
            return self.request(method, params, **kwargs)

        self.client.request.side_effect = request
        self.assertTrue(self.bridge.run_codex_via_app_server("task-original", "hello")[0])
        submitted = self.client.request.call_args_list[2].args[1]
        self.assertEqual({key: value for key, value in submitted.items()
                          if key not in {"threadId", "input", "clientUserMessageId"}}, {
            "model": "next", "effort": "medium",
        })
        self.assertEqual(pending_at_start, [{"model": "next", "effort": "medium"}])
        self.assertEqual(self.bridge.load_state()["pending_task_settings"], {
            "other-task": {"effort": "low"},
        })
        self.assertEqual(run["app_server_settings"]["approvalPolicy"], "on-request")
        self.assertEqual(run["app_server_settings"]["sandbox"], {
            "type": "workspaceWrite", "writableRoots": ["/workspace"],
        })
        self.assertEqual(run["app_server_settings"]["cwd"], "/workspace")

    def test_ack_keeps_settings_changed_while_submission_is_in_flight(self):
        self.bridge.update_desktop_task_settings("task-original", model="next", effort="medium")

        def request(method, params=None, **kwargs):
            if method == "turn/start":
                self.bridge.update_desktop_task_settings("task-original", model="newer", service_tier="priority")
            return self.request(method, params, **kwargs)

        self.client.request.side_effect = request
        self.assertTrue(self.bridge.run_codex_via_app_server("task-original", "hello")[0])
        self.assertEqual(self.bridge.load_state()["pending_task_settings"], {
            "task-original": {"model": "newer", "serviceTier": "priority"},
        })

    def test_default_service_tier_is_explicit_null_in_next_turn(self):
        self.bridge.update_desktop_task_settings("task-original", service_tier="default")
        self.assertEqual(self.bridge.load_state()["pending_task_settings"], {
            "task-original": {"serviceTier": None},
        })
        self.assertTrue(self.bridge.run_codex_via_app_server("task-original", "hello")[0])
        submitted = self.client.request.call_args_list[2].args[1]
        self.assertIn("serviceTier", submitted)
        self.assertIsNone(submitted["serviceTier"])
        self.assertNotIn("model", submitted)
        self.assertNotIn("effort", submitted)
        self.assertEqual(self.bridge.load_state()["pending_task_settings"], {})

    def test_start_timeout_retains_pending_settings_and_submission_settings(self):
        settings = {"model": "next", "effort": "medium", "serviceTier": "priority"}
        self.bridge.update_desktop_task_settings(
            "task-original", model="next", effort="medium", service_tier="priority",
        )
        _run, pending_id, captured, result = self.pending_start()
        self.assertFalse(result[0])
        self.assertEqual({key: captured[key] for key in settings}, settings)
        state = self.bridge.load_state()
        self.assertEqual(state["pending_task_settings"], {"task-original": settings})
        self.assertEqual(state["recoverable_runs"][pending_id]["submitted_settings"], settings)

    def approval_fixture(self, protocol_id, *, subscriber=True, resolution=""):
        self.bridge._active_runs.clear()
        run = self.registered_run()
        run["app_server_client"] = self.client
        request_id = str(protocol_id)
        approval = {
            "type": "command", "request_id": request_id, "protocol_request_id": protocol_id,
            "message_id": "om_active_approval", "resolved": bool(resolution),
            "detail": "echo test", "params": {},
        }
        run["approvals"] = {request_id: approval}
        self.bridge._shared_target_ids = {"task-original"}
        self.bridge.task_by_id = mock.Mock(return_value=run["task"])
        self.bridge.patch_card = mock.Mock()
        self.bridge.update_card = mock.Mock()
        self.bridge.reply = mock.Mock()
        self.bridge.set_run_progress = mock.Mock()
        self.bridge.respond_subscription_approval = mock.Mock(return_value=True)
        state = self.bridge.load_state()
        state["task_subscriptions"] = {"ou_admin": {"task-original": {}}}
        state["subscription_approvals"] = {}
        if subscriber:
            state["subscription_approvals"]["task-original:" + request_id] = {
                "task": run["task"], "approval": dict(approval),
                "recipients": {"ou_admin": {"message_id": "om_subscriber_approval"}},
                "resolved": bool(resolution), "resolution": resolution,
            }
        self.bridge.save_state(state)
        return run, approval

    def test_server_resolved_updates_active_and_subscriber_cards_without_resending(self):
        for protocol_id in (0, "request-x"):
            with self.subTest(protocol_id=protocol_id):
                run, approval = self.approval_fixture(protocol_id)
                self.bridge.handle_shared_codex_event({
                    "method": "serverRequest/resolved",
                    "params": {"threadId": "task-original", "requestId": protocol_id},
                })
                self.assertTrue(approval["resolved"])
                self.assertEqual(self.bridge.subscription_approval_resolution(
                    "task-original", str(protocol_id)), "handled_elsewhere")
                self.assertEqual({call.args[0] for call in self.bridge.patch_card.call_args_list}, {
                    "om_active_approval", "om_subscriber_approval",
                })
                for call in self.bridge.patch_card.call_args_list:
                    self.assertNotIn("button", str(call.args[1]))
                self.bridge.handle_approval_action(run, approval, True, {"message_id": "om_active_approval"})
                self.bridge.handle_subscription_approval_action(
                    "ou_admin", "task-original", str(protocol_id), False,
                    {"message_id": "om_subscriber_approval"},
                )
                self.client.respond.assert_not_called()
                self.bridge.respond_subscription_approval.assert_not_called()

    def test_server_resolved_persists_active_approval_without_subscriber_entry(self):
        run, approval = self.approval_fixture(0, subscriber=False)
        self.bridge.handle_shared_codex_event({
            "method": "serverRequest/resolved",
            "params": {"threadId": "task-original", "requestId": 0},
        })
        self.assertTrue(approval["resolved"])
        self.assertEqual(self.bridge.subscription_approval_resolution("task-original", "0"), "handled_elsewhere")
        self.bridge.handle_approval_action(run, approval, False, {"message_id": "om_active_approval"})
        self.client.respond.assert_not_called()

    def test_server_resolved_does_not_overwrite_prior_approved_resolution(self):
        run, approval = self.approval_fixture("request-x", resolution="approved")
        self.bridge.handle_shared_codex_event({
            "method": "serverRequest/resolved",
            "params": {"threadId": "task-original", "requestId": "request-x"},
        })
        self.assertEqual(self.bridge.subscription_approval_resolution("task-original", "request-x"), "approved")
        self.bridge.handle_approval_action(run, approval, False, {"message_id": "om_active_approval"})
        self.client.respond.assert_not_called()
        card = self.bridge.patch_card.call_args.args[1]
        self.assertEqual(card["header"]["template"], "green")

    def test_compaction_waits_for_terminal_event(self):
        self.client.next_event.side_effect = [{"method": "thread/compacted", "params": {"threadId": "task-original"}}]
        self.bridge.app_server_task_operation("task-original", "thread/compact/start")
        self.assertEqual(self.client.request.call_args.args, ("thread/compact/start", {"threadId": "task-original"}))
        self.client.next_event.assert_called_once()


if __name__ == "__main__":
    unittest.main()
