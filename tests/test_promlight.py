import importlib.util
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest import mock


BRIDGE_PATH = (
    Path(__file__).resolve().parents[1]
    / "Resources/bridge/feishu_codex_bridge.py"
)


def load_bridge():
    temporary = tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=False)
    temporary.write(
        '{"allowed_users": ['
        '{"open_id": "ou_admin", "name": "Admin", "allowed_projects": ["*"]},'
        '{"open_id": "ou_member", "name": "Member", "allowed_projects": ["deepori"]}'
        ']}'
    )
    temporary.close()
    previous = os.environ.get("CODEX_FEISHU_BRIDGE_CONFIG")
    os.environ["CODEX_FEISHU_BRIDGE_CONFIG"] = temporary.name
    try:
        spec = importlib.util.spec_from_file_location("bridge_promlight_test", BRIDGE_PATH)
        module = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(module)
        return module
    finally:
        Path(temporary.name).unlink()
        if previous is None:
            os.environ.pop("CODEX_FEISHU_BRIDGE_CONFIG", None)
        else:
            os.environ["CODEX_FEISHU_BRIDGE_CONFIG"] = previous


class PromLightTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        self.temporary = tempfile.TemporaryDirectory()
        self.bridge.STATE_PATH = Path(self.temporary.name) / "state.json"
        self.bridge.LOG_PATH = Path(self.temporary.name) / "bridge.log"
        self.tasks = {
            "ou_admin": [
                {"id": "task-a", "title": "Home", "project": "deepori"},
                {"id": "task-x", "title": "Other", "project": "other"},
            ],
            "ou_member": [
                {"id": "task-a", "title": "Home", "project": "deepori"},
                {"id": "task-b", "title": "Site", "project": "deepori"},
            ],
        }
        self.bridge.recent_tasks = lambda user_id: list(self.tasks[user_id])
        self.bridge.task_by_id = lambda task_id, user_id: next(
            (task for task in self.tasks[user_id] if task["id"] == task_id),
            None,
        )

    def tearDown(self):
        self.temporary.cleanup()

    def bind(self, user_id, relay_ref, name):
        with mock.patch.object(
            self.bridge,
            "discover_promlight_devices",
            return_value=[{"relay_ref": relay_ref, "label": name, "online": True}],
        ):
            return self.bridge.bind_promlight(user_id, relay_ref, name)

    def drain_promlight_work(self):
        for _ in range(20):
            if not self.bridge.process_promlight_work_once():
                return
        self.fail("PromLight work queue did not drain")

    def test_menu_and_cards_use_the_approved_labels_and_legend(self):
        self.assertEqual(self.bridge.PROMLIGHT_MENU_EVENT_KEY, "promlight")
        self.assertEqual(self.bridge.PROMLIGHT_LEGEND_MENU_EVENT_KEY, "promlight_legend")
        legend = json.dumps(self.bridge.build_promlight_legend_card(), ensure_ascii=False)
        for text in (
            "灯光状态说明",
            "绿灯常亮：已完成，当前无需处理",
            "黄灯常亮：正在处理中",
            "黄灯闪烁：需要你处理",
            "红灯闪烁：执行出错，请查看 Task",
            "红灯闪烁 > 黄灯闪烁 > 黄灯常亮 > 绿灯常亮",
        ):
            self.assertIn(text, legend)
        compact_legend = json.dumps(
            self.bridge.promlight_legend_element(), ensure_ascii=False
        )
        self.assertIn("灯光对应的事件说明", compact_legend)
        self.assertNotIn("灯光图例", compact_legend)
        control = json.dumps(
            self.bridge.build_promlight_control_card(
                "ou_admin", self.bridge.load_state()
            ),
            ensure_ascii=False,
        )
        self.assertIn("请在运行 Bridge 的 Mac 上打开 App 首页", control)
        self.assertNotIn("连接附近提示灯", control)
        self.assertNotIn("在本地 Bridge 连接新灯", control)

    def test_two_promlight_menu_leaves_send_control_and_legend_cards(self):
        self.bridge.send_card = mock.Mock(return_value=(True, "oc_test", "om_test"))
        self.bridge.reconcile_promlight_state = mock.Mock(return_value=False)

        self.bridge.handle_menu_event(
            {"event_id": "menu-light", "event_key": "promlight", "operator_id": "ou_admin"}
        )
        self.bridge.handle_menu_event(
            {
                "event_id": "menu-legend",
                "event_key": "promlight_legend",
                "operator_id": "ou_admin",
            }
        )

        cards = [call.args[1] for call in self.bridge.send_card.call_args_list]
        self.assertEqual(cards[0]["header"]["title"]["content"], "提示灯控制中心")
        self.assertEqual(cards[1]["header"]["title"]["content"], "灯光状态说明")

    def test_two_users_and_two_lamps_are_isolated(self):
        lamp_a = self.bind("ou_admin", "relay-a", "Desk A")
        lamp_b = self.bind("ou_member", "relay-b", "Desk B")

        self.bridge.set_promlight_task_subscription("ou_admin", lamp_a, "task-x", True)
        self.bridge.set_promlight_task_subscription("ou_member", lamp_b, "task-a", True)

        state = self.bridge.load_state()
        self.assertEqual(state["promlight"]["lamps"][lamp_a]["owner_open_id"], "ou_admin")
        self.assertEqual(state["promlight"]["lamps"][lamp_b]["owner_open_id"], "ou_member")
        self.assertEqual(state["promlight"]["lamps"][lamp_a]["task_ids"], ["task-x"])
        self.assertEqual(state["promlight"]["lamps"][lamp_b]["task_ids"], ["task-a"])
        with self.assertRaises(PermissionError):
            self.bridge.set_promlight_task_subscription("ou_member", lamp_a, "task-a", True)

    def test_one_user_can_name_and_manage_separate_task_lists_for_multiple_lamps(self):
        lamp_a = self.bind("ou_admin", "relay-a", "Desk")
        lamp_b = self.bind("ou_admin", "relay-b", "Door")
        self.bridge.set_promlight_task_subscription("ou_admin", lamp_a, "task-a", True)
        self.bridge.set_promlight_task_subscription("ou_admin", lamp_b, "task-x", True)

        state = self.bridge.load_state()
        self.assertEqual(state["promlight"]["lamps"][lamp_a]["name"], "Desk")
        self.assertEqual(state["promlight"]["lamps"][lamp_b]["name"], "Door")
        self.assertEqual(state["promlight"]["lamps"][lamp_a]["task_ids"], ["task-a"])
        self.assertEqual(state["promlight"]["lamps"][lamp_b]["task_ids"], ["task-x"])
        card = json.dumps(
            self.bridge.build_promlight_control_card("ou_admin", state),
            ensure_ascii=False,
        )
        for text in ("Desk", "Door", "deepori · Home", "other · Other"):
            self.assertIn(text, card)

    def test_task_permission_is_rechecked_when_subscription_is_saved(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        with self.assertRaises(PermissionError):
            self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-x", True)

    def test_status_aggregation_uses_fixed_priority(self):
        statuses = self.bridge.aggregate_promlight_status
        self.assertEqual(statuses([]), "idle")
        self.assertEqual(statuses(["idle", "running"]), "running")
        self.assertEqual(statuses(["running", "human_gate"]), "human_gate")
        self.assertEqual(statuses(["human_gate", "error"]), "error")
        self.assertEqual(statuses(["unknown"]), "unknown")
        self.assertEqual(self.bridge.promlight_command_for_status("error"), "led red blink --only")
        with self.assertRaises(ValueError):
            self.bridge.promlight_command_for_status("unknown")

    def test_inferred_error_is_recorded_but_never_drives_red(self):
        lamp = self.bind("ou_admin", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_admin", lamp, "task-a", True)
        with mock.patch.object(self.bridge, "deliver_promlight_effect") as deliver:
            self.bridge.record_promlight_task_status("task-a", "error", "bounded_inference", False)
        status = self.bridge.load_state()["promlight"]["task_statuses"]["task-a"]
        self.assertEqual(status["status"], "unknown")
        self.assertEqual(status["reported_status"], "error")
        self.assertNotIn("error", [call.args[1] for call in deliver.call_args_list])

    def test_unselected_task_never_drives_a_lamp(self):
        lamp = self.bind("ou_admin", "relay-a", "Desk")
        with mock.patch.object(self.bridge, "deliver_promlight_effect") as deliver:
            self.bridge.record_promlight_task_status("task-a", "running", "bridge_run", True)
        deliver.assert_not_called()
        self.assertEqual(self.bridge.load_state()["promlight"]["lamps"][lamp]["task_ids"], [])

    def test_same_task_can_drive_separate_users_lamps_without_crossing(self):
        lamp_a = self.bind("ou_admin", "relay-a", "Desk A")
        lamp_b = self.bind("ou_member", "relay-b", "Desk B")
        self.bridge.set_promlight_task_subscription("ou_admin", lamp_a, "task-a", True)
        self.bridge.set_promlight_task_subscription("ou_member", lamp_b, "task-a", True)
        calls = []

        def capture(lamp, status):
            calls.append((lamp["lamp_id"], lamp["relay_ref"], status))
            return {"online": True, "delivery": "acknowledged"}

        with mock.patch.object(self.bridge, "deliver_promlight_effect", side_effect=capture):
            self.bridge.record_promlight_task_status("task-a", "running", "bridge_run", True)

        self.assertEqual(
            set(calls),
            {(lamp_a, "relay-a", "running"), (lamp_b, "relay-b", "running")},
        )

    def test_human_gate_flashes_only_for_the_user_who_must_act(self):
        lamp_a = self.bind("ou_admin", "relay-a", "Desk A")
        lamp_b = self.bind("ou_member", "relay-b", "Desk B")
        self.bridge.set_promlight_task_subscription("ou_admin", lamp_a, "task-a", True)
        self.bridge.set_promlight_task_subscription("ou_member", lamp_b, "task-a", True)
        calls = []

        def capture(lamp, status):
            calls.append((lamp["owner_open_id"], status))
            return {"online": True, "delivery": "acknowledged", "verified": False}

        with mock.patch.object(self.bridge, "deliver_promlight_effect", side_effect=capture):
            self.bridge.record_promlight_task_status(
                "task-a",
                "human_gate",
                "bridge_run",
                True,
                target_user_id="ou_member",
            )

        self.assertEqual(
            set(calls),
            {("ou_admin", "running"), ("ou_member", "human_gate")},
        )

    def test_stale_task_card_keeps_its_own_lamp_context(self):
        lamp_a = self.bind("ou_admin", "relay-a", "Desk A")
        lamp_b = self.bind("ou_admin", "relay-b", "Desk B")
        with self.bridge._state_lock:
            state = self.bridge.load_state()
            card_a = self.bridge.build_promlight_task_card("ou_admin", lamp_a, state)
            self.bridge.remember_card_context(
                state, "ou_admin", "om_lamp_a", card_a, "promlight_tasks"
            )
            self.bridge.build_promlight_task_card("ou_admin", lamp_b, state)
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "type": "card.action.trigger",
                "event_id": "select-a-task",
                "operator_id": "ou_admin",
                "message_id": "om_lamp_a",
                "action_tag": "select_static",
                "action_name": "promlight_task_selector",
                "option": "task-a",
            }
        )

        patched = self.bridge.patch_card.call_args.args[1]
        self.assertEqual(patched["header"]["subtitle"]["content"], "Desk A")

    def test_promlight_card_refresh_prefers_message_patch(self):
        card = self.bridge.build_promlight_legend_card()
        self.bridge.patch_card = mock.Mock(return_value=True)
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.patch_promlight_event_card(
            {"message_id": "om_card", "token": "callback-token"},
            "ou_admin",
            card,
        )

        self.bridge.patch_card.assert_called_once_with("om_card", card, persist=False)
        self.bridge.update_card.assert_not_called()

    def test_promlight_card_refresh_falls_back_and_persists_only_after_both_fail(self):
        card = self.bridge.build_promlight_legend_card()
        self.bridge.patch_card = mock.Mock(return_value=False)
        self.bridge.update_card = mock.Mock(return_value=False)
        self.bridge.queue_pending_card_patch = mock.Mock()

        self.bridge.patch_promlight_event_card(
            {"message_id": "om_card", "token": "callback-token"},
            "ou_admin",
            card,
        )

        self.bridge.patch_card.assert_called_once_with("om_card", card, persist=False)
        self.bridge.update_card.assert_called_once_with("callback-token", card)
        self.bridge.queue_pending_card_patch.assert_called_once_with(
            "om_card", card, "飞书卡片刷新失败"
        )

    def test_promlight_processing_card_disables_only_the_clicked_action(self):
        lamp = self.bind("ou_admin", "relay-a", "Desk")
        card = self.bridge.build_promlight_task_card(
            "ou_admin", lamp, self.bridge.load_state()
        )

        processing = self.bridge.promlight_action_processing_card(
            card, "promlight_toggle_task"
        )
        buttons = [
            element
            for element in processing["body"]["elements"]
            if element.get("tag") == "button"
        ]
        clicked = next(
            button
            for button in buttons
            if button.get("text", {}).get("content") == "正在处理…"
        )
        self.assertTrue(clicked["disabled"])
        self.assertEqual(clicked["type"], "default")
        self.assertNotIn("behaviors", clicked)
        self.assertTrue(
            any(
                button.get("text", {}).get("content") == "返回我的提示灯"
                and "behaviors" in button
                for button in buttons
            )
        )

    def test_follow_action_patches_processing_state_before_final_card(self):
        lamp = self.bind("ou_admin", "relay-a", "Desk")
        self.bridge.patch_promlight_event_card = mock.Mock()

        handled = self.bridge.handle_promlight_button_action(
            {
                "operator_id": "ou_admin",
                "message_id": "om_card",
                "token": "callback-token",
            },
            {
                "action": "promlight_toggle_task",
                "lamp_id": lamp,
                "task_id": "task-a",
            },
        )

        self.assertTrue(handled)
        self.assertEqual(self.bridge.patch_promlight_event_card.call_count, 2)
        processing = self.bridge.patch_promlight_event_card.call_args_list[0].args[2]
        final = self.bridge.patch_promlight_event_card.call_args_list[1].args[2]
        processing_json = json.dumps(processing, ensure_ascii=False)
        final_json = json.dumps(final, ensure_ascii=False)
        self.assertIn("正在处理…", processing_json)
        self.assertIn('"disabled": true', processing_json)
        self.assertIn("已关注这个 Task", final_json)
        self.assertEqual(
            self.bridge.load_state()["promlight"]["lamps"][lamp]["task_ids"],
            ["task-a"],
        )

    def test_stale_pairing_button_returns_to_the_current_control_card(self):
        self.bridge.patch_promlight_event_card = mock.Mock()

        handled = self.bridge.handle_promlight_button_action(
            {"operator_id": "ou_admin", "message_id": "om_old"},
            {"action": "promlight_mobile_pairing"},
        )

        self.assertTrue(handled)
        card = self.bridge.patch_promlight_event_card.call_args.args[2]
        rendered = json.dumps(card, ensure_ascii=False)
        self.assertIn("提示灯控制中心", rendered)
        self.assertNotIn("连接附近提示灯", rendered)

    def test_daemon_ack_is_not_reported_as_verified_light_effect(self):
        with mock.patch.object(
            self.bridge,
            "promlight_http_json",
            return_value={"ok": True, "results": [{"status": "ok"}]},
        ):
            result = self.bridge.deliver_promlight_effect(
                {"relay_ref": "relay-a", "active_relay": "desktop"},
                "running",
            )
        self.assertEqual(result["delivery"], "acknowledged")
        self.assertFalse(result["verified"])

    def test_no_ack_is_offline_and_keeps_last_logical_state(self):
        with mock.patch.object(
            self.bridge,
            "promlight_http_json",
            return_value={"ok": False, "results": [{"status": "no-ack"}]},
        ):
            result = self.bridge.deliver_promlight_effect(
                {"relay_ref": "relay-a", "active_relay": "desktop"},
                "error",
            )
        self.assertFalse(result["online"])
        self.assertEqual(result["delivery"], "unknown")

    def test_revoked_or_archived_tasks_are_removed_and_stop_driving(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-b", True)
        self.tasks["ou_member"] = [self.tasks["ou_member"][0]]
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ) as deliver:
            self.assertTrue(self.bridge.reconcile_promlight_state())
            self.drain_promlight_work()
        current = self.bridge.load_state()["promlight"]["lamps"][lamp]
        self.assertEqual(current["task_ids"], [])
        deliver.assert_called_once()
        self.assertEqual(deliver.call_args.args[1], "idle")

    def test_restart_restores_ownership_subscriptions_and_last_status(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ):
            self.bridge.record_promlight_task_status("task-a", "running", "rollout", True)
        reloaded = self.bridge.load_state()["promlight"]["lamps"][lamp]
        self.assertEqual(reloaded["owner_open_id"], "ou_member")
        self.assertEqual(reloaded["task_ids"], ["task-a"])
        self.assertEqual(reloaded["last_logical_status"], "running")

    def test_existing_rollout_observer_drives_desktop_task_without_new_timer(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        self.bridge.rollout_path_for_task = mock.Mock(return_value=Path("/tmp/unused"))
        self.bridge.latest_rollout_turn = mock.Mock(
            return_value={"status": "running", "turn_id": "turn-1"}
        )
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ) as deliver:
            self.assertTrue(self.bridge.poll_promlight_task_statuses())
            self.drain_promlight_work()
        self.assertIn("running", [call.args[1] for call in deliver.call_args_list])

    def test_rollout_running_does_not_overwrite_live_human_gate(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ):
            self.bridge.record_promlight_task_status(
                "task-a",
                "human_gate",
                "bridge_run",
                True,
                target_user_id="ou_member",
            )
        self.bridge.rollout_path_for_task = mock.Mock(return_value=Path("/tmp/unused"))
        self.bridge.latest_rollout_turn = mock.Mock(
            return_value={"status": "running", "turn_id": "turn-1"}
        )
        self.bridge.poll_promlight_task_statuses()
        current = self.bridge.load_state()["promlight"]["task_statuses"]["task-a"]
        self.assertEqual(current["status"], "human_gate")

    def test_discovery_keeps_device_reference_local_and_card_hides_it(self):
        with mock.patch.object(
            self.bridge,
            "promlight_http_json",
            return_value={
                "bluetooth": True,
                "devices": [
                    {
                        "mac": "private-device-ref",
                        "label": "Desk",
                        "product": "PromLight",
                        "version": "0.1.3",
                        "release_number": 19,
                        "opened": True,
                    }
                ],
            },
        ):
            devices = self.bridge.discover_promlight_devices()
        self.assertEqual(devices[0]["relay_ref"], "private-device-ref")
        self.assertEqual(devices[0]["product"], "PromLight")
        self.assertEqual(devices[0]["device_version"], "0.1.3")
        self.assertEqual(devices[0]["release_number"], 19)
        lamp = self.bind("ou_admin", "private-device-ref", "Desk")
        card = json.dumps(
            self.bridge.build_promlight_control_card("ou_admin", self.bridge.load_state()),
            ensure_ascii=False,
        )
        self.assertIn(lamp, card)
        self.assertNotIn("private-device-ref", card)

    def test_discovery_excludes_devices_that_are_not_opened(self):
        with mock.patch.object(
            self.bridge,
            "promlight_http_json",
            return_value={"devices": [{"mac": "relay-off", "opened": False}]},
        ):
            self.assertEqual(self.bridge.discover_promlight_devices(), [])

    def test_missing_task_card_context_fails_closed(self):
        lamp_a = self.bind("ou_admin", "relay-a", "Desk A")
        lamp_b = self.bind("ou_admin", "relay-b", "Desk B")
        with self.bridge._state_lock:
            state = self.bridge.load_state()
            self.bridge.build_promlight_task_card("ou_admin", lamp_a, state)
            self.bridge.build_promlight_task_card("ou_admin", lamp_b, state)
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "type": "card.action.trigger",
                "event_id": "missing-context",
                "operator_id": "ou_admin",
                "message_id": "om_missing",
                "action_tag": "select_static",
                "action_name": "promlight_task_selector",
                "option": "task-a",
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["promlight"]["selected_lamps"]["ou_admin"], lamp_b)
        patched = json.dumps(self.bridge.patch_card.call_args.args[1], ensure_ascii=False)
        self.assertIn("卡片已失效", patched)
        self.assertEqual(state["promlight"]["lamps"][lamp_a]["task_ids"], [])
        self.assertEqual(state["promlight"]["lamps"][lamp_b]["task_ids"], [])

    def test_sender_id_cannot_authorize_card_mutation_without_operator(self):
        lamp = self.bind("ou_admin", "relay-a", "Desk")
        self.bridge.handle_card_event(
            {
                "type": "card.action.trigger",
                "event_id": "sender-only",
                "sender_id": "ou_admin",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "promlight_unbind", "lamp_id": lamp}
                ),
            }
        )
        current = self.bridge.load_state()["promlight"]["lamps"][lamp]
        self.assertFalse(current.get("pending_unbind", False))

    def test_failed_unbind_is_retained_until_idle_is_acknowledged(self):
        lamp = self.bind("ou_admin", "relay-a", "Desk")
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": False, "delivery": "unknown", "verified": False},
        ):
            self.bridge.unbind_promlight("ou_admin", lamp)
            self.drain_promlight_work()
        current = self.bridge.load_state()["promlight"]["lamps"][lamp]
        self.assertTrue(current["pending_unbind"])
        self.assertEqual(current["last_delivery"], "unknown")

        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ):
            self.bridge.schedule_promlight_lamp_refresh(lamp, force=True)
            self.drain_promlight_work()
        self.assertNotIn(lamp, self.bridge.load_state()["promlight"]["lamps"])

    def test_stale_running_ack_cannot_complete_concurrent_unbind(self):
        lamp = self.bind("ou_admin", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_admin", lamp, "task-a", True)
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ):
            self.bridge.record_promlight_task_status("task-a", "running", "bridge_run", True)
        started = threading.Event()
        release = threading.Event()

        def blocked_delivery(_lamp, status):
            self.assertEqual(status, "running")
            started.set()
            release.wait(1)
            return {"online": True, "delivery": "acknowledged", "verified": False}

        with mock.patch.object(
            self.bridge, "deliver_promlight_effect", side_effect=blocked_delivery
        ):
            worker = threading.Thread(
                target=self.bridge.refresh_promlight_lamp,
                args=(lamp, True),
            )
            worker.start()
            self.assertTrue(started.wait(1))
            self.bridge.unbind_promlight("ou_admin", lamp)
            release.set()
            worker.join(1)

        current = self.bridge.load_state()["promlight"]["lamps"][lamp]
        self.assertTrue(current["pending_unbind"])
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ):
            self.drain_promlight_work()
        self.assertNotIn(lamp, self.bridge.load_state()["promlight"]["lamps"])

    def test_no_ack_retries_same_turn_after_bounded_backoff(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        no_ack = {"online": False, "delivery": "unknown", "verified": False}
        with mock.patch.object(self.bridge, "deliver_promlight_effect", return_value=no_ack):
            self.bridge.record_promlight_task_status("task-a", "running", "rollout", True)
        with self.bridge._state_lock:
            state = self.bridge.load_state()
            state["promlight"]["task_statuses"]["task-a"]["turn_id"] = "turn-1"
            state["promlight"]["lamps"][lamp]["next_retry_at"] = 0
            self.bridge.save_state(state)
        self.bridge.rollout_path_for_task = mock.Mock(return_value=Path("/tmp/unused"))
        self.bridge.latest_rollout_turn = mock.Mock(
            return_value={"status": "running", "turn_id": "turn-1"}
        )
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ) as deliver:
            self.bridge.poll_promlight_task_statuses()
            self.drain_promlight_work()
        deliver.assert_called_once()

    def test_status_scheduling_coalesces_latest_value_per_task(self):
        for index in range(25):
            self.bridge.schedule_promlight_task_status(
                "task-a", "running", f"progress-{index}", True
            )
        self.assertEqual(len(self.bridge._promlight_pending_statuses), 1)
        pending = self.bridge._promlight_pending_statuses["task-a"]
        self.assertEqual(pending[1], "progress-24")

    def test_rollout_running_cannot_overwrite_pending_live_human_gate(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        self.bridge.schedule_promlight_task_status(
            "task-a",
            "human_gate",
            "bridge_run",
            True,
            target_user_id="ou_member",
        )
        self.bridge.rollout_path_for_task = mock.Mock(return_value=Path("/tmp/unused"))
        self.bridge.latest_rollout_turn = mock.Mock(
            return_value={"status": "running", "turn_id": "turn-1"}
        )
        self.bridge.poll_promlight_task_statuses()
        pending = self.bridge._promlight_pending_statuses["task-a"]
        self.assertEqual(pending[0], "human_gate")
        self.assertEqual(pending[1], "bridge_run")
        self.assertEqual(pending[4], "ou_member")

    def test_archived_last_task_retries_idle_until_acknowledged(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ):
            self.bridge.record_promlight_task_status("task-a", "running", "bridge_run", True)
        self.tasks["ou_member"] = [self.tasks["ou_member"][1]]
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": False, "delivery": "unknown", "verified": False},
        ):
            self.bridge.reconcile_promlight_state()
            self.drain_promlight_work()
        with self.bridge._state_lock:
            state = self.bridge.load_state()
            state["promlight"]["lamps"][lamp]["next_retry_at"] = 0
            self.bridge.save_state(state)
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ) as deliver:
            self.bridge.reconcile_promlight_state()
            self.drain_promlight_work()
        deliver.assert_called_once()
        current = self.bridge.load_state()["promlight"]["lamps"][lamp]
        self.assertFalse(current.get("pending_idle", False))
        self.assertEqual(current["last_logical_status"], "idle")

    def test_canceling_last_subscription_retries_idle_until_acknowledged(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": False, "delivery": "unknown", "verified": False},
        ):
            self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", False)
        with self.bridge._state_lock:
            state = self.bridge.load_state()
            state["promlight"]["lamps"][lamp]["next_retry_at"] = 0
            self.bridge.save_state(state)
        with mock.patch.object(
            self.bridge,
            "deliver_promlight_effect",
            return_value={"online": True, "delivery": "acknowledged", "verified": False},
        ) as deliver:
            self.bridge.reconcile_promlight_state()
            self.drain_promlight_work()
        deliver.assert_called_once()
        self.assertFalse(
            self.bridge.load_state()["promlight"]["lamps"][lamp].get("pending_idle", False)
        )

    def test_unknown_observation_does_not_schedule_physical_delivery_retry(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        self.bridge.record_promlight_task_status("task-a", "unknown", "rollout", False)
        with self.bridge._state_lock:
            state = self.bridge.load_state()
            state["promlight"]["task_statuses"]["task-a"]["turn_id"] = "turn-none"
            self.bridge.save_state(state)
        self.bridge.rollout_path_for_task = mock.Mock(return_value=Path("/tmp/unused"))
        self.bridge.latest_rollout_turn = mock.Mock(
            return_value={"status": "none", "turn_id": "turn-none"}
        )
        self.bridge.poll_promlight_task_statuses()
        self.assertEqual(self.bridge._promlight_pending_lamps, {})

    def test_rebind_rejects_a_lamp_that_is_waiting_for_unbind_cleanup(self):
        lamp = self.bind("ou_admin", "relay-a", "Desk")
        self.bridge.unbind_promlight("ou_admin", lamp)
        with mock.patch.object(
            self.bridge,
            "discover_promlight_devices",
            return_value=[{"relay_ref": "relay-a", "label": "Desk", "online": True}],
        ), self.assertRaisesRegex(ValueError, "解绑待收口"):
            self.bridge.bind_promlight("ou_admin", "relay-a", "Desk")

    def test_permission_revocation_clears_user_selectors_and_task_metadata(self):
        lamp = self.bind("ou_member", "relay-a", "Desk")
        self.bridge.set_promlight_task_subscription("ou_member", lamp, "task-a", True)
        with self.bridge._state_lock:
            state = self.bridge.load_state()
            state["promlight"]["pending_renames"]["ou_member"] = lamp
            self.bridge.save_state(state)
        self.bridge.ALLOWED_USERS.pop("ou_member", None)
        self.bridge.reconcile_promlight_state()
        namespace = self.bridge.load_state()["promlight"]
        for key in ("selected_lamps", "selected_tasks", "selected_projects", "pending_renames"):
            self.assertNotIn("ou_member", namespace[key])
        self.assertNotIn("task-a", namespace["task_statuses"])


if __name__ == "__main__":
    unittest.main()
