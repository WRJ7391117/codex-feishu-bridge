import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import unittest
from unittest import mock


BRIDGE_PATH = (
    Path(__file__).resolve().parents[1]
    / "Resources/bridge/feishu_codex_bridge.py"
)


def load_bridge():
    temporary = tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=False)
    temporary.write(
        '{"allowed_users": [{"open_id": "ou_admin", "name": "Admin", '
        '"allowed_projects": ["*"]}]}'
    )
    temporary.close()
    previous = os.environ.get("CODEX_FEISHU_BRIDGE_CONFIG")
    os.environ["CODEX_FEISHU_BRIDGE_CONFIG"] = temporary.name
    try:
        spec = importlib.util.spec_from_file_location("bridge_remote_test", BRIDGE_PATH)
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


class RemoteFeatureTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        self.temporary = tempfile.TemporaryDirectory()
        self.bridge.STATE_PATH = Path(self.temporary.name) / "state.json"
        self.bridge.LOG_PATH = Path(self.temporary.name) / "bridge.log"
        self.bridge.send_card = mock.Mock(
            return_value=(True, "oc_test", "om_current_status")
        )

    def tearDown(self):
        self.temporary.cleanup()

    def test_save_state_restricts_state_file_permissions(self):
        self.bridge.STATE_PATH.write_text("{}\n", encoding="utf-8")
        self.bridge.STATE_PATH.chmod(0o644)

        self.bridge.save_state({"queued_turns": []})

        self.assertEqual(self.bridge.STATE_PATH.stat().st_mode & 0o777, 0o600)

    def test_global_run_limit_queues_different_tasks(self):
        self.bridge.MAX_CONCURRENT_RUNS = 2
        runs = [
            self.bridge.new_run(
                "ou_admin",
                "oc_test",
                f"om_{index}",
                {"id": f"task-{index}", "title": f"Task {index}", "project": "deepori"},
                [],
                [],
            )
            for index in range(3)
        ]

        self.assertTrue(self.bridge.claim_active_run(runs[0]))
        self.assertTrue(self.bridge.claim_active_run(runs[1]))
        self.assertFalse(self.bridge.claim_active_run(runs[2]))

    def test_same_task_never_runs_twice_even_below_global_limit(self):
        self.bridge.MAX_CONCURRENT_RUNS = 3
        task = {"id": "task-a", "title": "Home", "project": "deepori"}
        first = self.bridge.new_run("ou_admin", "oc_test", "om_1", task, [], [])
        second = self.bridge.new_run("ou_admin", "oc_test", "om_2", task, [], [])

        self.assertTrue(self.bridge.claim_active_run(first))
        self.assertFalse(self.bridge.claim_active_run(second))
        self.assertEqual(second["queue_reason"], "same_task")

    def test_different_tasks_in_same_project_run_in_parallel_until_global_limit(self):
        self.bridge.MAX_CONCURRENT_RUNS = 2
        first = self.bridge.new_run(
            "ou_admin", "oc_test", "om_1", self.tasks()[0], [], []
        )
        second = self.bridge.new_run(
            "ou_admin", "oc_test", "om_2", self.tasks()[1], [], []
        )
        third = self.bridge.new_run(
            "ou_admin",
            "oc_test",
            "om_3",
            {"id": "task-d", "title": "API", "project": "deepori"},
            [],
            [],
        )

        self.assertTrue(self.bridge.claim_active_run(first))
        self.assertTrue(self.bridge.claim_active_run(second))
        self.assertFalse(self.bridge.claim_active_run(third))
        self.assertEqual(third["queue_reason"], "global_limit")
        self.assertEqual(third["active_run_count"], 2)

    def tasks(self):
        return [
            {"id": "task-a", "title": "Home", "project": "deepori"},
            {"id": "task-b", "title": "Site", "project": "deepori"},
            {"id": "task-c", "title": "Paper", "project": "thesis"},
        ]

    def test_task_identity_text_is_consistent_across_plain_replies(self):
        task = self.tasks()[0]

        self.assertEqual(
            self.bridge.current_task_text(task),
            "🟢 当前 Task\n项目：deepori\nTask：Home",
        )
        self.assertEqual(
            self.bridge.current_task_changed_text(task),
            "✅ 当前 Task 已切换\n项目：deepori\nTask：Home",
        )
        self.assertEqual(
            self.bridge.task_status_prefix(task, "已完成"),
            "🟢 当前 Task\n项目：deepori\nTask：Home\n状态：已完成\n\n",
        )
        self.assertEqual(
            self.bridge.task_status_prefix(task, "已完成", False),
            "🔵 结果所属 Task\n项目：deepori\nTask：Home\n状态：已完成\n\n",
        )

    def test_running_queued_and_approval_cards_show_current_task_identity(self):
        task = self.tasks()[0]
        run = {
            "run_id": "run-1",
            "task": task,
            "status": "运行中",
            "outcome": "running",
            "started_at": time.time(),
            "attachment_count": 0,
        }
        entry = {
            "queue_id": "queue-1",
            "task": task,
            "image_keys": [],
            "file_keys": [],
        }
        approval = {
            "type": "command",
            "request_id": "request-1",
            "detail": "运行测试",
        }

        cards = [
            (self.bridge.build_run_card(run), "运行中"),
            (self.bridge.build_queued_card(entry, 1), "已排队"),
            (self.bridge.build_approval_card(run, approval), "待处理"),
        ]
        for card, status in cards:
            with self.subTest(status=status):
                self.assertEqual(card["header"]["title"]["content"], "Task：Home")
                self.assertEqual(card["header"]["subtitle"]["content"], "项目：deepori")
                self.assertEqual(
                    [tag["text"]["content"] for tag in card["header"]["text_tag_list"]],
                    ["当前 Task", status],
                )

        self.assertIn(
            "Codex 请求运行命令",
            self.bridge.build_approval_card(run, approval)["body"]["elements"][0]["content"],
        )

    def test_old_task_cards_stop_claiming_to_be_current_after_switch(self):
        task = self.tasks()[0]
        run = {
            "run_id": "run-1",
            "task": task,
            "status": "运行中",
            "outcome": "running",
            "started_at": time.time(),
            "attachment_count": 0,
            "is_current_task": False,
        }
        queued = {
            "queue_id": "queue-1",
            "task": task,
            "image_keys": [],
            "file_keys": [],
            "is_current_task": False,
        }

        self.assertEqual(
            self.bridge.build_run_card(run)["header"]["text_tag_list"][0]["text"]["content"],
            "运行 Task",
        )
        self.assertEqual(
            self.bridge.build_queued_card(queued, 1)["header"]["text_tag_list"][0]["text"]["content"],
            "排队 Task",
        )

    def test_queue_card_explains_global_capacity_and_same_task_reasons(self):
        base = {
            "queue_id": "queue-1",
            "task": self.tasks()[0],
            "image_keys": [],
            "file_keys": [],
        }
        same_task = self.bridge.build_queued_card(
            {**base, "queue_reason": "same_task"},
            2,
        )
        global_limit = self.bridge.build_queued_card(
            {
                **base,
                "queue_reason": "global_limit",
                "active_run_count": 2,
                "max_concurrent_runs": 2,
            },
            1,
        )

        self.assertIn("同一 Task 正在运行", same_task["body"]["elements"][0]["content"])
        self.assertIn("本 Task 队列：第 2 条", same_task["body"]["elements"][0]["content"])
        self.assertIn("全局并发已满（2/2）", global_limit["body"]["elements"][0]["content"])

    def test_task_card_filters_by_project_without_truncating_project_tasks(self):
        card = self.bridge.build_task_card(self.tasks(), "task-a", "deepori")
        selectors = {
            item.get("name"): item
            for item in card["body"]["elements"]
            if item.get("tag") == "select_static"
        }

        self.assertEqual(selectors["project_selector"]["initial_option"], "deepori")
        self.assertEqual(
            [option["value"] for option in selectors["project_selector"]["options"]],
            ["deepori", "thesis"],
        )
        self.assertEqual(
            [option["value"] for option in selectors["task_selector"]["options"]],
            ["task-a", "task-b"],
        )

    def test_task_card_defaults_to_first_project_without_an_all_projects_option(self):
        card = self.bridge.build_task_card(self.tasks(), None, "__all__")
        selectors = {
            item.get("name"): item
            for item in card["body"]["elements"]
            if item.get("tag") == "select_static"
        }

        self.assertEqual(selectors["project_selector"]["initial_option"], "deepori")
        self.assertEqual(
            [option["value"] for option in selectors["task_selector"]["options"]],
            ["task-a", "task-b"],
        )

    def test_task_card_paginates_large_project_without_ten_item_truncation(self):
        tasks = [
            {"id": f"task-{index}", "title": f"Task {index}", "project": "deepori"}
            for index in range(55)
        ]

        first = self.bridge.build_task_card(tasks, None, "deepori", page=0)
        first_selector = next(
            item for item in first["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual(len(first_selector["options"]), 50)
        self.assertTrue(any(
            item.get("tag") == "button"
            and item.get("text", {}).get("content") == "下一页"
            for item in first["body"]["elements"]
        ))

        second = self.bridge.build_task_card(tasks, None, "deepori", page=1)
        second_selector = next(
            item for item in second["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual(len(second_selector["options"]), 5)

    def test_task_card_search_filters_title_and_can_be_cleared(self):
        card = self.bridge.build_task_card(
            self.tasks(),
            "task-a",
            "deepori",
            search_query="site",
        )
        selector = next(
            item for item in card["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual([item["value"] for item in selector["options"]], ["task-b"])
        self.assertTrue(any(
            item.get("tag") == "button"
            and item.get("text", {}).get("content") == "清除搜索"
            for item in card["body"]["elements"]
        ))

    def test_task_card_only_contains_task_selection_controls(self):
        card = self.bridge.build_task_card(self.tasks(), "task-a", "deepori")
        button_labels = {
            item.get("text", {}).get("content")
            for item in card["body"]["elements"]
            if item.get("tag") == "button"
        }

        self.assertNotIn("新建 Task", button_labels)
        self.assertNotIn("归档当前 Task…", button_labels)
        self.assertIn("查看已归档 Task", button_labels)
        self.assertIn("刷新 Task 列表", button_labels)
        self.assertIn("取消切换", button_labels)

        cancel = next(
            item
            for item in card["body"]["elements"]
            if item.get("text", {}).get("content") == "取消切换"
        )
        self.assertEqual(
            cancel["behaviors"][0]["value"],
            {"action": "cancel_task_switch", "task_id": "task-a"},
        )

    def test_task_card_without_current_task_has_no_cancel_switch_button(self):
        card = self.bridge.build_task_card(self.tasks(), None, "deepori")

        self.assertEqual(card["header"]["title"]["content"], "切换 Codex Task")
        self.assertFalse(
            any(
                item.get("text", {}).get("content") == "取消切换"
                for item in card["body"]["elements"]
            )
        )

    def test_task_card_separates_current_project_and_title(self):
        card = self.bridge.build_task_card(self.tasks(), "task-a", "deepori")

        self.assertEqual(card["header"]["title"]["content"], "Task：Home")
        self.assertEqual(card["header"]["subtitle"]["content"], "项目：deepori")
        self.assertEqual(
            card["header"]["text_tag_list"][0]["text"]["content"],
            "当前 Task",
        )

    def test_task_card_marks_a_new_selection_as_changed(self):
        card = self.bridge.build_task_card(
            self.tasks(),
            "task-b",
            "deepori",
            selection_changed=True,
        )

        self.assertIn("当前 Task 已切换", card["body"]["elements"][0]["content"])

    def test_current_status_card_is_reused_per_user(self):
        self.bridge.save_state({"selected": {"ou_admin": "task-a"}})
        self.bridge.recent_tasks = lambda user_id: self.tasks()
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.assertTrue(
            self.bridge.update_current_status_card(
                "ou_admin",
                "当前 Task 已切换",
                task=self.tasks()[0],
                ensure=True,
            )
        )
        self.assertTrue(self.bridge.update_current_status_card("ou_admin"))

        self.bridge.send_card.assert_called_once()
        self.bridge.patch_card.assert_called_once()
        status_card = self.bridge.patch_card.call_args.args[1]
        self.assertEqual(status_card["header"]["title"]["content"], "Task：Home")
        self.assertEqual(
            status_card["header"]["text_tag_list"][0]["text"]["content"],
            "当前 Task",
        )

    def test_forced_current_status_card_is_sent_as_the_latest_message(self):
        task = self.tasks()[0]
        self.bridge.save_state(
            {
                "selected": {"ou_admin": task["id"]},
                "current_status_cards": {
                    "ou_admin": {
                        "message_id": "om_old_status",
                        "chat_id": "oc_test",
                    }
                },
            }
        )
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.assertTrue(
            self.bridge.update_current_status_card(
                "ou_admin", task=task, ensure=True, force_new=True
            )
        )

        self.bridge.patch_card.assert_not_called()
        self.bridge.send_card.assert_called_once()

    def test_current_status_card_shows_recent_exchange_and_favorite(self):
        card = self.bridge.build_current_status_card(
            self.tasks()[0],
            "空闲",
            0,
            0,
            recent_exchange={"question": "请生成报告", "answer": "报告已经生成"},
            is_favorite=True,
        )

        content = card["body"]["elements"][0]["content"]
        self.assertIn("最近提问", content)
        self.assertIn("请生成报告", content)
        self.assertIn("最近回复", content)
        self.assertIn("报告已经生成", content)
        self.assertEqual(
            [tag["text"]["content"] for tag in card["header"]["text_tag_list"]],
            ["当前 Task", "已收藏", "空闲"],
        )

    def test_codex_usage_uses_live_rate_limit_buckets_and_remaining_percent(self):
        usage = self.bridge.normalize_codex_usage(
            {
                "rateLimitsByLimitId": {
                    "codex_bengalfox": {
                        "limitId": "codex_bengalfox",
                        "limitName": "GPT-5.3-Codex-Spark",
                        "primary": {
                            "usedPercent": 4,
                            "windowDurationMins": 300,
                            "resetsAt": 2_000,
                        },
                    },
                    "codex": {
                        "limitId": "codex",
                        "limitName": None,
                        "primary": {
                            "usedPercent": 63,
                            "windowDurationMins": 10_080,
                            "resetsAt": 3_000,
                        },
                    },
                }
            },
            now=1_000,
        )

        self.assertEqual(usage["updated_at"], 1_000)
        self.assertEqual([bucket["id"] for bucket in usage["buckets"]], ["codex", "codex_bengalfox"])
        self.assertEqual(usage["buckets"][0]["name"], "Codex")
        self.assertEqual(usage["buckets"][0]["windows"][0]["remaining_percent"], 37)
        self.assertEqual(usage["buckets"][1]["windows"][0]["remaining_percent"], 96)

    def test_codex_usage_has_a_compact_card_and_is_absent_from_task_status(self):
        usage = {
            "buckets": [
                {
                    "id": "codex",
                    "name": "Codex",
                    "windows": [
                        {
                            "remaining_percent": 37,
                            "window_minutes": 10_080,
                            "resets_at": 3_000,
                        }
                    ],
                }
            ],
            "updated_at": 1_000,
        }
        with self.bridge._codex_usage_lock:
            self.bridge._codex_usage = usage

        status_card = self.bridge.current_status_card_for_user("ou_admin", self.tasks()[0])
        usage_card = self.bridge.build_codex_usage_card(usage)
        status_content = status_card["body"]["elements"][0]["content"]
        usage_content = usage_card["body"]["elements"][0]["content"]
        usage_buttons = [
            element.get("text", {}).get("content")
            for element in usage_card["body"]["elements"]
            if element.get("tag") == "button"
        ]
        self.assertNotIn("Codex 用量", status_content)
        self.assertIn("剩余 37%", usage_content)
        self.assertEqual(
            usage_buttons,
            ["当日 Task 用量分析", "当期 Task 用量分析", "刷新用量"],
        )

    def test_task_usage_parser_deduplicates_token_events_and_counts_causes(self):
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        fields = {
            "input_tokens": 100,
            "cached_input_tokens": 60,
            "cache_write_input_tokens": 0,
            "output_tokens": 40,
            "reasoning_output_tokens": 20,
            "total_tokens": 140,
        }

        def token_event(timestamp, total, last):
            return {
                "timestamp": timestamp,
                "type": "event_msg",
                "payload": {
                    "type": "token_count",
                    "info": {
                        "total_token_usage": total,
                        "last_token_usage": last,
                    },
                },
            }

        second = {key: value * 2 for key, value in fields.items()}
        records = [
            {
                "timestamp": "2026-08-24T23:00:00Z",
                "type": "event_msg",
                "payload": {"type": "agent_reasoning", "text": "x" * 200},
            }
            for _ in range(40)
        ] + [
            token_event("2026-08-25T00:10:00Z", fields, fields),
            token_event("2026-08-25T00:10:01Z", fields, fields),
            {
                "timestamp": "2026-08-25T00:11:00Z",
                "type": "response_item",
                "payload": {"type": "custom_tool_call"},
            },
            {
                "timestamp": "2026-08-25T00:12:00Z",
                "type": "event_msg",
                "payload": {"type": "context_compacted"},
            },
            token_event("2026-08-25T00:13:00Z", second, fields),
        ]
        rollout.write_text(
            "\n".join(json.dumps(record) for record in records) + "\n",
            encoding="utf-8",
        )

        metrics = self.bridge.task_usage_from_rollout(
            rollout,
            1_787_616_000,
            1_787_619_600,
        )

        self.assertEqual(metrics["total_tokens"], 280)
        self.assertEqual(metrics["model_calls"], 2)
        self.assertEqual(metrics["tool_calls"], 1)
        self.assertEqual(metrics["compactions"], 1)

    def test_daily_and_period_task_usage_cards_explain_rank_and_normality(self):
        usage = {
            "buckets": [
                {
                    "id": "codex",
                    "name": "Codex",
                    "windows": [
                        {
                            "remaining_percent": 37,
                            "window_minutes": 10_080,
                            "resets_at": 700_000,
                        }
                    ],
                }
            ],
            "updated_at": 100_000,
        }
        daily_range = self.bridge.task_usage_time_range("daily", usage, now=100_000)
        period_range = self.bridge.task_usage_time_range("period", usage, now=100_000)
        item = {
            "task": {"project": "deepori", "title": "Architecture"},
            "total_tokens": 200_000,
            "model_calls": 4,
            "turns": 1,
            "tool_calls": 8,
            "share_percent": 80,
            "assessment": "偏高，需要关注",
            "reason": "长上下文/缓存输入 90%；工具调用 8 次",
        }
        daily = self.bridge.build_task_usage_analysis_card(
            {**daily_range, "tasks": [item], "total_tokens": 200_000}
        )
        period = self.bridge.build_task_usage_analysis_card(
            {**period_range, "tasks": [item], "total_tokens": 200_000}
        )

        self.assertEqual(daily["header"]["title"]["content"], "当日 Task 用量分析")
        self.assertEqual(period["header"]["title"]["content"], "当期 Task 用量分析")
        for card in (daily, period):
            content = card["body"]["elements"][0]["content"]
            self.assertIn("deepori · Architecture", content)
            self.assertIn("偏高，需要关注", content)
            self.assertIn("长上下文", content)
            self.assertIn("不等同于官方额度", content)
            buttons = [
                element.get("text", {}).get("content")
                for element in card["body"]["elements"]
                if element.get("tag") == "button"
            ]
            self.assertIn("返回实时用量", buttons)

    def test_task_usage_analysis_buttons_start_background_analysis(self):
        self.bridge.patch_card = mock.Mock(return_value=True)
        analysis_thread = mock.Mock()
        thread_factory = mock.Mock(return_value=analysis_thread)
        with self.bridge._codex_usage_lock:
            self.bridge._codex_usage = {
                "buckets": [],
                "updated_at": 1_000,
            }

        with mock.patch.object(self.bridge.threading, "Thread", thread_factory):
            for index, action in enumerate(
                (
                    "show_daily_task_usage_analysis",
                    "show_period_task_usage_analysis",
                    "show_codex_usage",
                )
            ):
                with self.subTest(action=action):
                    self.bridge.handle_card_event(
                        {
                            "event_id": f"evt-usage-view-{index}",
                            "message_id": "om_status",
                            "chat_id": "oc_test",
                            "operator_id": "ou_admin",
                            "action_tag": "button",
                            "action_value": json.dumps({"action": action}),
                        }
                    )

        titles = [
            call.args[1]["header"]["title"]["content"]
            for call in self.bridge.patch_card.call_args_list
        ]
        self.assertEqual(
            titles,
            ["当日 Task 用量分析", "当期 Task 用量分析", "Codex 用量"],
        )
        self.assertEqual(thread_factory.call_count, 2)
        self.assertIs(
            thread_factory.call_args_list[0].kwargs["target"],
            self.bridge.refresh_task_usage_analysis_card,
        )
        self.assertEqual(
            thread_factory.call_args_list[0].kwargs["args"],
            ("ou_admin", "om_status", "daily"),
        )
        self.assertEqual(
            thread_factory.call_args_list[1].kwargs["args"],
            ("ou_admin", "om_status", "period"),
        )

    def test_usage_menu_sends_refreshable_card_to_all_authorized_users(self):
        self.bridge.ALLOWED_USERS["ou_miller"] = {"*"}
        sent = []
        self.bridge.send_card = lambda user_id, card, kind: (
            sent.append((user_id, card, kind)) or True,
            "oc_test",
            "om_usage_card",
        )

        for index, user_id in enumerate(("ou_admin", "ou_miller")):
            self.bridge.handle_menu_event(
                {
                    "event_id": f"evt-usage-menu-{index}",
                    "event_key": "codex_usage",
                    "operator_id": user_id,
                }
            )

        self.assertEqual(len(sent), 2)
        for _user_id, card, _kind in sent:
            self.assertEqual(card["header"]["title"]["content"], "Codex 用量")
            self.assertIn("刷新用量", [
                element.get("text", {}).get("content")
                for element in card["body"]["elements"]
                if element.get("tag") == "button"
            ])

    def test_current_task_menu_opens_current_status_card_directly(self):
        task = self.tasks()[0]
        self.bridge.selected_task = mock.Mock(return_value=task)
        self.bridge.update_current_status_card = mock.Mock(return_value=True)

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-current-task-menu",
                "event_key": "current_task",
                "operator_id": "ou_admin",
            }
        )

        self.bridge.update_current_status_card.assert_called_once_with(
            "ou_admin", task=task, ensure=True, force_new=True
        )
        self.bridge.send_card.assert_not_called()

    def test_current_task_menu_opens_selector_only_when_no_task_is_selected(self):
        self.bridge.selected_task = mock.Mock(return_value=None)
        self.bridge.send_task_card = mock.Mock()

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-current-task-menu-empty",
                "event_key": "current_task",
                "operator_id": "ou_admin",
            }
        )

        self.bridge.send_task_card.assert_called_once()

    def test_task_menu_event_keys_have_seven_distinct_defaults(self):
        event_keys = (
            self.bridge.CURRENT_TASK_MENU_EVENT_KEY,
            self.bridge.TASK_MENU_EVENT_KEY,
            self.bridge.NEW_TASK_MENU_EVENT_KEY,
            self.bridge.ARCHIVE_TASK_MENU_EVENT_KEY,
            self.bridge.USAGE_MENU_EVENT_KEY,
            self.bridge.DESKTOP_SYNC_MENU_EVENT_KEY,
            self.bridge.DESKTOP_SYNC_SWITCH_MENU_EVENT_KEY,
        )

        self.assertEqual(
            event_keys,
            (
                "current_task",
                "select_task",
                "new_task",
                "archive_task",
                "codex_usage",
                "sync_desktop",
                "sync_desktop_switch",
            ),
        )
        self.assertEqual(len(set(event_keys)), 7)

    def test_latest_rollout_turn_reads_completed_desktop_result(self):
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        records = [
            {"type": "event_msg", "payload": {"type": "task_started", "turn_id": "turn-1"}},
            {"type": "event_msg", "payload": {"type": "task_complete", "turn_id": "turn-1", "last_agent_message": "桌面结果"}},
        ]
        rollout.write_text(
            "".join(json.dumps(record, ensure_ascii=False) + "\n" for record in records),
            encoding="utf-8",
        )

        snapshot = self.bridge.latest_rollout_turn(rollout)

        self.assertEqual(snapshot["status"], "completed")
        self.assertEqual(snapshot["turn_id"], "turn-1")
        self.assertEqual(snapshot["message"], "桌面结果")
        self.assertEqual(snapshot["cursor_offset"], rollout.stat().st_size)

    def test_latest_rollout_turn_stops_before_large_old_history(self):
        rollout = Path(self.temporary.name) / "large-rollout.jsonl"
        old = {
            "type": "event_msg",
            "payload": {"type": "old_history", "blob": "x" * (2 * 1024 * 1024)},
        }
        current = [
            {
                "type": "event_msg",
                "payload": {"type": "task_started", "turn_id": "turn-latest"},
            },
            {
                "type": "event_msg",
                "payload": {
                    "type": "task_complete",
                    "turn_id": "turn-latest",
                    "last_agent_message": "最新结果",
                },
            },
        ]
        rollout.write_text(
            json.dumps(old) + "\n"
            + "".join(json.dumps(record) + "\n" for record in current),
            encoding="utf-8",
        )
        original_loads = self.bridge.json.loads

        def guarded_loads(value):
            if b'"old_history"' in value:
                raise AssertionError("old history should not be parsed")
            return original_loads(value)

        with mock.patch.object(self.bridge.json, "loads", side_effect=guarded_loads):
            snapshot = self.bridge.latest_rollout_turn(rollout)

        self.assertEqual(snapshot["status"], "completed")
        self.assertEqual(snapshot["message"], "最新结果")

    def test_latest_rollout_turn_ignores_incomplete_trailing_record(self):
        rollout = Path(self.temporary.name) / "incomplete-rollout.jsonl"
        complete = (
            json.dumps(
                {
                    "type": "event_msg",
                    "payload": {"type": "task_started", "turn_id": "turn-running"},
                }
            )
            + "\n"
        )
        rollout.write_text(complete + '{"type":"event_msg"', encoding="utf-8")

        snapshot = self.bridge.latest_rollout_turn(rollout)

        self.assertEqual(snapshot["status"], "running")
        self.assertEqual(snapshot["cursor_offset"], len(complete.encode("utf-8")))

    def test_desktop_sync_menu_requires_confirmation_for_selected_current_task(self):
        task = self.tasks()[0]
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        rollout.write_text(
            json.dumps({"type": "event_msg", "payload": {"type": "task_started", "turn_id": "turn-running"}}) + "\n",
            encoding="utf-8",
        )
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.rollout_path_for_task = lambda task_id: rollout

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-desktop-sync",
                "event_key": "sync_desktop",
                "operator_id": "ou_admin",
            }
        )

        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("desktop_result_subscriptions", {}),
        )
        card = self.bridge.send_card.call_args.args[1]
        self.assertEqual(
            card["header"]["title"]["content"],
            "从 Codex Desktop 回到飞书",
        )
        self.assertEqual(card["header"]["subtitle"]["content"], "deepori · Home")
        self.assertIn(
            "这是你在桥接中选择的当前 Task",
            card["body"]["elements"][0]["content"],
        )
        self.assertIn(
            "查看桌面端的最新结果，并在飞书继续沟通",
            card["body"]["elements"][0]["content"],
        )
        self.assertEqual(
            [tag["text"]["content"] for tag in card["header"]["text_tag_list"]],
            ["当前 Task", "运行中"],
        )
        buttons = {
            element.get("text", {}).get("content"): element
            for element in card["body"]["elements"]
            if element.get("tag") == "button"
        }
        self.assertEqual(
            buttons["接续当前 Task"]["behaviors"][0]["value"],
            {"action": "confirm_desktop_sync", "task_id": "task-a"},
        )
        self.assertEqual(
            buttons["接续其他 Task"]["behaviors"][0]["value"],
            {"action": "show_desktop_sync_task_selector"},
        )
        self.assertIn("暂不接续", buttons)

    def test_desktop_sync_switch_menu_opens_selector_in_sync_context(self):
        self.bridge.recent_tasks = lambda user_id: self.tasks()

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-desktop-sync-switch",
                "event_key": "sync_desktop_switch",
                "operator_id": "ou_admin",
            }
        )

        card = self.bridge.send_card.call_args.args[1]
        self.assertEqual(card["header"]["title"]["content"], "切换 Codex Task")
        state = self.bridge.load_state()
        self.assertEqual(
            state["card_contexts"]["om_current_status"]["type"],
            "desktop_sync_selection",
        )

    def test_desktop_sync_task_selection_returns_to_confirmation(self):
        tasks = self.tasks()
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.rollout_path_for_task = (
            lambda task_id: Path(self.temporary.name) / f"{task_id}.missing"
        )
        self.bridge.patch_card = mock.Mock(return_value=True)
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-show-desktop-sync-selector",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "show_desktop_sync_task_selector"}
                ),
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["card_contexts"]["om_sync"]["user_id"], "ou_admin")
        self.assertEqual(state["card_contexts"]["om_sync"]["type"], "desktop_sync_selection")
        selector_card = self.bridge.patch_card.call_args.args[1]

        self.bridge.handle_card_event(
            {
                "event_id": "evt-filter-desktop-sync-project",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "select_static",
                "action_name": "project_selector",
                "option": "deepori",
                "token": "token-project",
                "card_content": json.dumps(selector_card),
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["card_contexts"]["om_sync"]["user_id"], "ou_admin")
        self.assertEqual(state["card_contexts"]["om_sync"]["type"], "desktop_sync_selection")
        selector_card = self.bridge.update_card.call_args.args[1]

        self.bridge.handle_card_event(
            {
                "event_id": "evt-select-desktop-sync-task",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "select_static",
                "action_name": "task_selector",
                "option": "task-b",
                "token": "token-test",
                "card_content": json.dumps(selector_card),
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["selected"]["ou_admin"], "task-b")
        self.assertNotIn(
            "ou_admin",
            state.get("desktop_result_subscriptions", {}),
        )
        confirmation = self.bridge.update_card.call_args.args[1]
        self.assertEqual(
            confirmation["header"]["title"]["content"],
            "从 Codex Desktop 回到飞书",
        )
        self.assertEqual(confirmation["header"]["subtitle"]["content"], "deepori · Site")
        buttons = {
            element.get("text", {}).get("content"): element
            for element in confirmation["body"]["elements"]
            if element.get("tag") == "button"
        }
        self.assertEqual(
            buttons["接续当前 Task"]["behaviors"][0]["value"],
            {"action": "confirm_desktop_sync", "task_id": "task-b"},
        )
        self.assertEqual(state["card_contexts"]["om_sync"]["user_id"], "ou_admin")
        self.assertEqual(state["card_contexts"]["om_sync"]["type"], "desktop_sync_confirmation")

    def test_desktop_sync_task_selection_context_is_isolated_per_user(self):
        tasks = self.tasks()
        self.bridge.ALLOWED_USERS["ou_miller"] = {"*"}
        self.bridge.recent_tasks = lambda user_id: tasks
        state = {
            "selected": {"ou_admin": "task-a", "ou_miller": "task-c"},
            "card_contexts": {
                "om_sync_admin": {
                    "user_id": "ou_admin",
                    "type": "desktop_sync_selection",
                },
                "om_task_miller": {"user_id": "ou_miller", "type": "tasks"},
            },
        }
        self.bridge.save_state(state)
        self.bridge.update_card = mock.Mock(return_value=True)
        self.bridge.rollout_path_for_task = (
            lambda task_id: Path(self.temporary.name) / f"{task_id}.missing"
        )

        self.bridge.handle_card_event(
            {
                "event_id": "evt-select-desktop-sync-isolated",
                "message_id": "om_sync_admin",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "select_static",
                "action_name": "task_selector",
                "option": "task-b",
                "token": "token-test",
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["selected"]["ou_admin"], "task-b")
        self.assertEqual(state["selected"]["ou_miller"], "task-c")
        self.assertEqual(state["card_contexts"]["om_task_miller"]["user_id"], "ou_miller")
        self.assertEqual(state["card_contexts"]["om_task_miller"]["type"], "tasks")

    def test_confirm_desktop_sync_subscribes_to_selected_running_task(self):
        task = self.tasks()[0]
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        rollout.write_text(
            json.dumps({"type": "event_msg", "payload": {"type": "task_started", "turn_id": "turn-running"}}) + "\n",
            encoding="utf-8",
        )
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.rollout_path_for_task = lambda task_id: rollout
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-confirm-desktop-sync",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "confirm_desktop_sync", "task_id": "task-a"}
                ),
            }
        )

        subscription = self.bridge.load_state()["desktop_result_subscriptions"]["ou_admin"]
        self.assertEqual(subscription["task_id"], "task-a")
        self.assertEqual(subscription["turn_id"], "turn-running")
        card = self.bridge.patch_card.call_args.args[1]
        self.assertEqual(card["header"]["text_tag_list"][1]["text"]["content"], "运行中")
        self.assertEqual(card["header"]["title"]["content"], "等待桌面端完成")
        self.assertIn("完成后结果会自动推送到这里", card["body"]["elements"][0]["content"])

    def test_confirm_desktop_sync_rejects_stale_task_after_current_task_changes(self):
        current = self.tasks()[1]
        self.bridge.selected_task = lambda user_id, state: current
        self.bridge.rollout_path_for_task = lambda task_id: Path(self.temporary.name) / "missing"
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-stale-desktop-sync",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "confirm_desktop_sync", "task_id": "task-a"}
                ),
            }
        )

        card = self.bridge.patch_card.call_args.args[1]
        self.assertEqual(
            card["header"]["title"]["content"],
            "从 Codex Desktop 回到飞书",
        )
        self.assertIn("当前 Task 已变化", card["body"]["elements"][0]["content"])
        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("desktop_result_subscriptions", {}),
        )

    def test_desktop_sync_subscription_pushes_result_when_turn_completes(self):
        task = self.tasks()[0]
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        started = {"type": "event_msg", "payload": {"type": "task_started", "turn_id": "turn-running"}}
        rollout.write_text(json.dumps(started) + "\n", encoding="utf-8")
        cursor = rollout.stat().st_size
        self.bridge.task_by_id = lambda task_id, user_id: task
        self.bridge.rollout_path_for_task = lambda task_id: rollout
        self.bridge.patch_card = mock.Mock(return_value=True)
        self.bridge.reply_or_queue = mock.Mock(return_value=True)
        self.bridge.update_current_status_card = mock.Mock(return_value=True)
        self.bridge.schedule_user_task_identity_refresh = mock.Mock()
        self.bridge.record_task_exchange = mock.Mock()
        self.bridge.follow_result_task = mock.Mock(return_value=True)
        state = self.bridge.load_state()
        state["desktop_result_subscriptions"] = {
            "ou_admin": {
                "task_id": "task-a",
                "turn_id": "turn-running",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "cursor_offset": cursor,
                "images": [],
                "created_at": time.time(),
                "next_check_at": 0,
            }
        }
        self.bridge.save_state(state)
        completed = {
            "type": "event_msg",
            "payload": {
                "type": "task_complete",
                "turn_id": "turn-running",
                "last_agent_message": "最终结果",
            },
        }
        with rollout.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(completed, ensure_ascii=False) + "\n")

        self.assertTrue(self.bridge.retry_desktop_result_subscriptions())

        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("desktop_result_subscriptions", {}),
        )
        self.assertIn("最终结果", self.bridge.reply_or_queue.call_args.args[1])
        self.bridge.follow_result_task.assert_called_once_with("ou_admin", task)

    def test_desktop_sync_menu_never_guesses_a_task_when_none_is_selected(self):
        task = self.tasks()[0]
        self.bridge.recent_tasks = lambda user_id: [task]
        self.bridge.selected_task = lambda user_id, state: None

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-desktop-completed",
                "event_key": "sync_desktop",
                "operator_id": "ou_admin",
            }
        )

        card = self.bridge.send_card.call_args.args[1]
        self.assertEqual(card["header"]["title"]["content"], "切换 Codex Task")
        state = self.bridge.load_state()
        self.assertEqual(
            state["card_contexts"]["om_current_status"]["type"],
            "desktop_sync_selection",
        )
        self.assertNotIn("ou_admin", state.get("selected", {}))
        self.assertNotIn(
            "ou_admin",
            state.get("desktop_result_subscriptions", {}),
        )

    def test_desktop_sync_confirmation_uses_each_users_independent_current_task(self):
        tasks_by_user = {
            "ou_admin": self.tasks()[0],
            "ou_miller": self.tasks()[1],
        }
        self.bridge.ALLOWED_USERS["ou_miller"] = {"*"}
        self.bridge.selected_task = lambda user_id, state: tasks_by_user[user_id]
        self.bridge.rollout_path_for_task = (
            lambda task_id: Path(self.temporary.name) / f"{task_id}.missing"
        )

        for index, user_id in enumerate(tasks_by_user):
            self.bridge.handle_menu_event(
                {
                    "event_id": f"evt-desktop-user-{index}",
                    "event_key": "sync_desktop",
                    "operator_id": user_id,
                }
            )

        sent = [call.args for call in self.bridge.send_card.call_args_list]
        self.assertEqual(sent[0][0], "ou_admin")
        self.assertEqual(
            sent[0][1]["header"]["subtitle"]["content"],
            "deepori · Home",
        )
        self.assertEqual(sent[1][0], "ou_miller")
        self.assertEqual(
            sent[1][1]["header"]["subtitle"]["content"],
            "deepori · Site",
        )

    def test_confirm_desktop_sync_immediately_pushes_selected_completed_result(self):
        task = self.tasks()[0]
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        records = [
            {"type": "event_msg", "payload": {"type": "task_started", "turn_id": "turn-complete"}},
            {"type": "event_msg", "payload": {"type": "task_complete", "turn_id": "turn-complete", "last_agent_message": "已经完成"}},
        ]
        rollout.write_text(
            "".join(json.dumps(record, ensure_ascii=False) + "\n" for record in records),
            encoding="utf-8",
        )
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.rollout_path_for_task = lambda task_id: rollout
        self.bridge.patch_card = mock.Mock(return_value=True)
        self.bridge.reply_or_queue = mock.Mock(return_value=True)
        self.bridge.update_current_status_card = mock.Mock(return_value=True)
        self.bridge.schedule_user_task_identity_refresh = mock.Mock()
        self.bridge.record_task_exchange = mock.Mock()
        self.bridge.follow_result_task = mock.Mock(return_value=False)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-desktop-completed",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "confirm_desktop_sync", "task_id": "task-a"}
                ),
            }
        )

        self.assertIn("已经完成", self.bridge.reply_or_queue.call_args.args[1])
        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("desktop_result_subscriptions", {}),
        )

    def test_desktop_sync_does_not_duplicate_a_run_already_owned_by_same_user(self):
        task = self.tasks()[0]
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        rollout.write_text(
            json.dumps({"type": "event_msg", "payload": {"type": "task_started", "turn_id": "turn-owned"}}) + "\n",
            encoding="utf-8",
        )
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.rollout_path_for_task = lambda task_id: rollout
        self.bridge.active_run_for_task = lambda task_id: {"user_id": "ou_admin"}
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-desktop-owned",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "confirm_desktop_sync", "task_id": "task-a"}
                ),
            }
        )

        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("desktop_result_subscriptions", {}),
        )

    def test_cancel_desktop_sync_keeps_current_task_and_creates_no_subscription(self):
        task = self.tasks()[0]
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-cancel-desktop-sync",
                "message_id": "om_sync",
                "chat_id": "oc_test",
                "operator_id": "ou_admin",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "cancel_desktop_sync", "task_id": "task-a"}
                ),
            }
        )

        card = self.bridge.patch_card.call_args.args[1]
        self.assertEqual(card["header"]["text_tag_list"][0]["text"]["content"], "已取消")
        self.assertEqual(card["header"]["title"]["content"], "已取消从桌面接续")
        self.assertIn("当前 Task 保持不变", card["body"]["elements"][0]["content"])
        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("desktop_result_subscriptions", {}),
        )

    def test_authorized_user_usage_button_starts_live_refresh_with_visible_feedback(self):
        self.bridge.ALLOWED_USERS["ou_miller"] = {"*"}
        self.bridge.patch_card = mock.Mock(return_value=True)
        refresh_thread = mock.Mock()
        thread_factory = mock.Mock(return_value=refresh_thread)

        with mock.patch.object(self.bridge.threading, "Thread", thread_factory):
            self.bridge.handle_card_event(
                {
                    "event_id": "evt-refresh-usage",
                    "message_id": "om_status",
                    "chat_id": "oc_test",
                    "operator_id": "ou_miller",
                    "action_tag": "button",
                    "action_value": json.dumps({"action": "refresh_codex_usage"}),
                }
            )

        progress_card = self.bridge.patch_card.call_args.args[1]
        self.assertIn("正在刷新用量", progress_card["body"]["elements"][0]["content"])
        self.assertIs(
            thread_factory.call_args.kwargs["target"],
            self.bridge.refresh_codex_usage_card,
        )
        self.assertEqual(thread_factory.call_args.kwargs["args"], ("om_status",))
        refresh_thread.start.assert_called_once_with()

    def test_task_card_filters_favorites_and_recent_use(self):
        favorites = self.bridge.build_task_card(
            self.tasks(),
            "task-a",
            "deepori",
            favorite_ids={"task-b"},
            task_scope="favorites",
        )
        recent = self.bridge.build_task_card(
            self.tasks(),
            "task-a",
            "deepori",
            recent_ids=["task-b", "task-a"],
            task_scope="recent",
        )

        favorite_selector = next(
            item
            for item in favorites["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        recent_selector = next(
            item
            for item in recent["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual(
            [option["value"] for option in favorite_selector["options"]],
            ["task-b"],
        )
        self.assertEqual(
            [option["value"] for option in recent_selector["options"]],
            ["task-b", "task-a"],
        )
        scope_selector = next(
            item
            for item in recent["body"]["elements"]
            if item.get("name") == "task_scope_selector"
        )
        self.assertEqual(scope_selector["initial_option"], "recent")

    def test_record_task_exchange_is_private_per_user_and_task(self):
        self.bridge.record_task_exchange("ou_admin", "task-a", question="第一问")
        self.bridge.record_task_exchange("ou_admin", "task-a", answer="第一答")
        self.bridge.record_task_exchange("ou_other", "task-b", question="第二问")

        state = self.bridge.load_state()
        self.assertEqual(
            state["task_summaries"]["ou_admin"]["task-a"]["answer"],
            "第一答",
        )
        self.assertNotIn("task-b", state["task_summaries"]["ou_admin"])
        self.assertEqual(state["recent_task_ids"]["ou_admin"], ["task-a"])

    def test_archived_task_card_requires_selection_before_restore(self):
        initial = self.bridge.build_task_card(
            self.tasks(),
            None,
            "deepori",
            archived=True,
        )
        selected = self.bridge.build_task_card(
            self.tasks(),
            "task-a",
            "deepori",
            archived=True,
        )

        initial_buttons = [
            item["text"]["content"]
            for item in initial["body"]["elements"]
            if item.get("tag") == "button"
        ]
        selected_buttons = [
            item["text"]["content"]
            for item in selected["body"]["elements"]
            if item.get("tag") == "button"
        ]
        self.assertEqual(initial_buttons, ["刷新 Task 列表", "返回当前 Task"])
        self.assertEqual(
            selected_buttons,
            ["刷新 Task 列表", "恢复这个 Task", "返回当前 Task"],
        )
        restore = next(
            item
            for item in selected["body"]["elements"]
            if item.get("text", {}).get("content") == "恢复这个 Task"
        )
        self.assertEqual(
            restore["behaviors"][0]["value"],
            {"action": "restore_task", "task_id": "task-a"},
        )
        self.assertIn("confirm", restore)

    def test_new_task_menu_card_only_lists_authorized_desktop_projects(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"},
            {"id": "project-2", "name": "thesis", "root": "/tmp/thesis"},
        ]
        sent = []
        self.bridge.send_card = lambda user_id, card, kind: (
            sent.append((user_id, card, kind)) or True,
            "oc_test",
            "om_new_task_card",
        )

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-new-task-menu",
                "event_key": "new_task",
                "operator_id": "ou_admin",
            }
        )

        self.assertEqual(len(sent), 1)
        selector = next(
            item
            for item in sent[0][1]["body"]["elements"]
            if item.get("name") == "new_task_project_selector"
        )
        self.assertEqual(
            [option["value"] for option in selector["options"]],
            ["deepori"],
        )
        self.assertTrue(
            any(
                item.get("text", {}).get("content") == "取消新建"
                for item in sent[0][1]["body"]["elements"]
            )
        )

    def test_new_task_card_project_selection_updates_creation_target(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori", "thesis"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"},
            {"id": "project-2", "name": "thesis", "root": "/tmp/thesis"},
        ]
        original = self.bridge.build_new_task_card(
            ["deepori", "thesis"],
            "deepori",
        )
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-new-task-project",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "action_name": "new_task_project_selector",
                "option": "thesis",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        updated = self.bridge.update_card.call_args.args[1]
        button = next(
            item
            for item in updated["body"]["elements"]
            if item.get("tag") == "button"
        )
        payload = button["behaviors"][0]["value"]
        self.assertEqual(payload, {"action": "new_task", "project": "thesis"})

    def test_new_task_project_is_inferred_from_persisted_card_context(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori", "Evolution"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"},
            {"id": "project-2", "name": "Evolution", "root": "/tmp/evolution"},
        ]
        self.bridge.save_state(
            {
                "card_contexts": {
                    "om_new_task_card": {
                        "user_id": "ou_admin",
                        "type": "new_task",
                    }
                }
            }
        )
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-new-task-project-without-card",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_new_task_card",
                "action_tag": "select_static",
                "option": "Evolution",
                "token": "token-test",
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["last_projects"]["ou_admin"], "Evolution")
        updated = self.bridge.update_card.call_args.args[1]
        self.assertEqual(
            updated["header"]["subtitle"]["content"],
            "当前项目：Evolution",
        )
        create = next(
            item
            for item in updated["body"]["elements"]
            if item.get("text", {}).get("content") == "在此项目新建"
        )
        self.assertEqual(
            create["behaviors"][0]["value"]["project"],
            "Evolution",
        )

    def test_new_task_button_prefers_latest_selected_project(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori", "Evolution"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"},
            {"id": "project-2", "name": "Evolution", "root": "/tmp/evolution"},
        ]
        self.bridge.save_state({"last_projects": {"ou_admin": "Evolution"}})
        self.bridge.reply = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-new-task-stale-button",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_new_task_card",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "new_task", "project": "deepori"}
                ),
            }
        )

        self.assertEqual(
            self.bridge.load_state()["pending_task_creations"]["ou_admin"],
            "Evolution",
        )
        self.assertIn("Evolution", self.bridge.reply.call_args.args[1])

    def test_cancel_new_task_button_clears_pending_creation(self):
        self.bridge.save_state(
            {"pending_task_creations": {"ou_admin": "Evolution"}}
        )
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-cancel-new-task",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_new_task_card",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps({"action": "cancel_new_task"}),
            }
        )

        state = self.bridge.load_state()
        self.assertNotIn(
            "ou_admin",
            state.get("pending_task_creations", {}),
        )
        canceled = self.bridge.update_card.call_args.args[1]
        self.assertEqual(canceled["header"]["subtitle"]["content"], "已取消")
        self.assertIn(
            "不会再等待 Task 标题",
            canceled["body"]["elements"][0]["content"],
        )

    def test_new_task_card_confirmation_waits_for_title(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"},
        ]
        self.bridge.reply = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-new-task-confirm",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_new_task_card",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "new_task", "project": "deepori"}
                ),
            }
        )

        self.assertEqual(
            self.bridge.load_state()["pending_task_creations"]["ou_admin"],
            "deepori",
        )
        self.assertIn("请发送 Task 标题", self.bridge.reply.call_args.args[1])

    def test_archive_task_menu_sends_confirmation_for_current_task(self):
        task = self.tasks()[0]
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.active_run_for_task = lambda task_id: None
        sent = []
        self.bridge.send_card = lambda user_id, card, kind: (
            sent.append((user_id, card, kind)) or True,
            "oc_test",
            "om_archive_task_card",
        )

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-archive-task-menu",
                "event_key": "archive_task",
                "operator_id": "ou_admin",
            }
        )

        self.assertEqual(len(sent), 1)
        card = sent[0][1]
        self.assertEqual(card["header"]["title"]["content"], "Task：Home")
        self.assertEqual(card["header"]["subtitle"]["content"], "项目：deepori")
        self.assertEqual(
            [tag["text"]["content"] for tag in card["header"]["text_tag_list"]],
            ["当前 Task"],
        )
        buttons = [
            item for item in card["body"]["elements"]
            if item.get("tag") == "button"
        ]
        self.assertEqual(
            [button["text"]["content"] for button in buttons],
            ["归档这个 Task…", "取消，不归档"],
        )
        button = buttons[0]
        self.assertEqual(
            button["behaviors"][0]["value"],
            {"action": "archive_task", "task_id": "task-a"},
        )
        self.assertIn("confirm", button)
        self.assertEqual(
            buttons[1]["behaviors"][0]["value"],
            {"action": "cancel_archive", "task_id": "task-a"},
        )
        self.assertNotIn("confirm", buttons[1])

    def test_cancel_archive_keeps_current_task_selected(self):
        task = self.tasks()[0]
        self.bridge.STATE_PATH.write_text(
            json.dumps({"selected": {"ou_admin": "task-a"}}),
            encoding="utf-8",
        )
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.archive_codex_task = mock.Mock()
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-archive-cancel",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_archive_card",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "cancel_archive", "task_id": "task-a"}
                ),
            }
        )

        self.bridge.archive_codex_task.assert_not_called()
        self.assertEqual(
            self.bridge.load_state()["selected"]["ou_admin"],
            "task-a",
        )
        canceled = self.bridge.update_card.call_args.args[1]
        self.assertIn("已取消归档", canceled["body"]["elements"][0]["content"])
        self.assertEqual(
            [tag["text"]["content"] for tag in canceled["header"]["text_tag_list"]],
            ["当前 Task", "已取消"],
        )

    def test_archive_callback_clears_selection_only_after_success(self):
        task = self.tasks()[0]
        self.bridge.STATE_PATH.write_text(
            json.dumps({"selected": {"ou_admin": "task-a"}}),
            encoding="utf-8",
        )
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.active_run_for_task = lambda task_id: None
        self.bridge.archive_codex_task = mock.Mock()
        self.bridge.reply = mock.Mock(return_value=True)
        self.bridge.update_card = mock.Mock(return_value=True)
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-archive-confirm",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_archive_card",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "archive_task", "task_id": "task-a"}
                ),
            }
        )

        self.bridge.archive_codex_task.assert_called_once_with("ou_admin", task)
        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("selected", {}),
        )
        completed = self.bridge.patch_card.call_args.args[1]
        self.assertEqual(
            [tag["text"]["content"] for tag in completed["header"]["text_tag_list"]],
            ["已归档"],
        )
        self.assertEqual(
            [
                item["text"]["content"]
                for item in completed["body"]["elements"]
                if item.get("tag") == "button"
            ],
            ["撤销归档", "切换到其他 Task", "新建 Task"],
        )

    def test_slow_archive_does_not_hold_global_state_lock(self):
        task = self.tasks()[0]
        self.bridge.save_state({"selected": {"ou_admin": "task-a"}})
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.active_run_for_task = lambda task_id: None
        self.bridge.reply = mock.Mock(return_value=True)
        self.bridge.update_card = mock.Mock(return_value=True)
        self.bridge.patch_card = mock.Mock(return_value=True)
        entered = threading.Event()
        release = threading.Event()

        def slow_archive(*args):
            entered.set()
            release.wait(1)

        self.bridge.archive_codex_task = slow_archive
        event = {
            "event_id": "evt-slow-archive",
            "operator_id": "ou_admin",
            "chat_id": "oc_test",
            "message_id": "om_archive_card",
            "token": "token-test",
            "action_tag": "button",
            "action_value": json.dumps(
                {"action": "archive_task", "task_id": "task-a"}
            ),
        }
        worker = threading.Thread(target=self.bridge.handle_card_event, args=(event,))
        worker.start()
        self.assertTrue(entered.wait(0.5))
        self.assertTrue(self.bridge._state_lock.acquire(timeout=0.2))
        self.bridge._state_lock.release()
        release.set()
        worker.join(1)

        self.assertFalse(worker.is_alive())

    def test_restore_callback_restores_and_selects_archived_task(self):
        task = self.tasks()[0]
        self.bridge.STATE_PATH.write_text("{}", encoding="utf-8")
        self.bridge.archived_tasks = lambda user_id: [task]
        self.bridge.restore_codex_task = mock.Mock()
        self.bridge.reply = mock.Mock(return_value=True)
        self.bridge.update_card = mock.Mock(return_value=True)
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-restore-confirm",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_archive_card",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "restore_task", "task_id": "task-a"}
                ),
            }
        )

        self.bridge.restore_codex_task.assert_called_once_with("ou_admin", task)
        state = self.bridge.load_state()
        self.assertEqual(state["selected"]["ou_admin"], "task-a")
        self.assertEqual(state["last_projects"]["ou_admin"], "deepori")
        restored = self.bridge.patch_card.call_args.args[1]
        self.assertEqual(
            [tag["text"]["content"] for tag in restored["header"]["text_tag_list"]],
            ["当前 Task", "已恢复"],
        )
        self.assertIn(
            "✅ 当前 Task 已恢复\n项目：deepori\nTask：Home",
            self.bridge.reply.call_args.args[1],
        )

    def test_restore_codex_task_uses_desktop_unarchive_protocol(self):
        task = self.tasks()[0]
        self.bridge.codex_app_server_requests = mock.Mock(return_value=[{}])

        self.bridge.restore_codex_task("ou_admin", task)

        self.bridge.codex_app_server_requests.assert_called_once_with(
            [("thread/unarchive", {"threadId": "task-a"})]
        )

    def test_show_archived_tasks_opens_restore_selector(self):
        self.bridge.STATE_PATH.write_text("{}", encoding="utf-8")
        self.bridge.archived_tasks = lambda user_id: self.tasks()
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-show-archived",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_task_card",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps({"action": "show_archived_tasks"}),
            }
        )

        card = self.bridge.update_card.call_args.args[1]
        selector_names = {
            item.get("name")
            for item in card["body"]["elements"]
            if item.get("tag") == "select_static"
        }
        self.assertEqual(
            selector_names,
            {"archived_project_selector", "archived_task_selector"},
        )

    def test_archived_task_selection_adds_restore_button(self):
        tasks = self.tasks()
        original = self.bridge.build_task_card(
            tasks,
            None,
            "deepori",
            archived=True,
        )
        self.bridge.archived_tasks = lambda user_id: tasks
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-select-archived",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "action_name": "archived_task_selector",
                "option": "task-a",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        card = self.bridge.update_card.call_args.args[1]
        self.assertTrue(
            any(
                item.get("text", {}).get("content") == "恢复这个 Task"
                for item in card["body"]["elements"]
            )
        )

    def test_archive_callback_blocks_an_active_task(self):
        task = self.tasks()[0]
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.active_run_for_task = lambda task_id: {"run_id": "run-1"}
        self.bridge.archive_codex_task = mock.Mock()
        self.bridge.reply = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-archive-busy",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_archive_card",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "archive_task", "task_id": "task-a"}
                ),
            }
        )

        self.bridge.archive_codex_task.assert_not_called()
        self.assertIn("正在运行", self.bridge.reply.call_args.args[1])

    def test_task_management_menus_ignore_unauthorized_users(self):
        self.bridge.send_card = mock.Mock(
            return_value=(True, "oc_test", "om_task_card")
        )

        for index, event_key in enumerate(
            ("current_task", "select_task", "new_task", "archive_task", "codex_usage")
        ):
            self.bridge.handle_menu_event(
                {
                    "event_id": f"evt-unauthorized-{index}",
                    "event_key": event_key,
                    "operator_id": "ou_unknown",
                }
            )

        self.bridge.send_card.assert_not_called()

    def test_create_task_uses_authorized_desktop_project_and_names_thread(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"}
        ]
        task_id = "019ff634-60a0-7c22-a011-111111111111"
        self.bridge.codex_app_server_requests = mock.Mock(
            return_value=[{"thread": {"id": task_id}}, {}]
        )

        task = self.bridge.create_codex_task("ou_admin", "deepori", " Ori Home ")

        self.assertEqual(
            task,
            {"id": task_id, "title": "Ori Home", "project": "deepori"},
        )
        self.bridge.codex_app_server_requests.assert_called_once()
        first_request = self.bridge.codex_app_server_requests.call_args.args[0]
        self.assertEqual(first_request[0][0], "thread/start")
        self.assertEqual(first_request[0][1]["cwd"], "/tmp/deepori")
        self.assertNotIn("projectId", first_request[0][1])
        second_request = first_request[1]
        self.assertEqual(
            second_request[0],
            "thread/name/set",
        )
        self.assertEqual(
            second_request[1]([{"thread": {"id": task_id}}]),
            {"threadId": task_id, "name": "Ori Home"},
        )

    def test_create_task_rejects_project_outside_user_scope(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori"}
        with self.assertRaisesRegex(RuntimeError, "没有.*权限"):
            self.bridge.create_codex_task("ou_admin", "thesis", "Paper")

    def test_completed_task_creation_announces_the_new_current_task(self):
        task = self.tasks()[0]
        self.bridge.create_codex_task = mock.Mock(return_value=task)
        self.bridge.reply = mock.Mock(return_value=True)

        self.bridge.complete_task_creation(
            "om_new_task",
            "ou_admin",
            "deepori",
            "Home",
        )

        self.assertEqual(
            self.bridge.load_state()["selected"]["ou_admin"],
            "task-a",
        )
        self.assertIn(
            "✅ 当前 Task 已新建\n项目：deepori\nTask：Home",
            self.bridge.reply.call_args.args[1],
        )

    def test_first_completed_turn_restores_requested_task_name_once(self):
        self.bridge.save_state(
            {
                "pending_task_names": {
                    "ou_admin": {
                        "task_id": "task-a",
                        "title": "飞书桥接验收-临时",
                    }
                }
            }
        )
        self.bridge.codex_app_server_requests = mock.Mock(return_value=[{}])

        restored = self.bridge.restore_pending_task_name("ou_admin", "task-a")

        self.assertTrue(restored)
        self.bridge.codex_app_server_requests.assert_called_once_with(
            [
                (
                    "thread/name/set",
                    {"threadId": "task-a", "name": "飞书桥接验收-临时"},
                )
            ]
        )
        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("pending_task_names", {}),
        )

    def test_project_selector_updates_same_card_and_persists_filter(self):
        tasks = self.tasks()
        original = self.bridge.build_task_card(tasks, "task-a")
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-project",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "action_name": "project_selector",
                "option": "thesis",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["last_projects"]["ou_admin"], "thesis")
        updated = self.bridge.update_card.call_args.args[1]
        selector = next(
            item
            for item in updated["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual([option["value"] for option in selector["options"]], ["task-c"])

    def test_project_selector_is_inferred_when_feishu_omits_action_name(self):
        tasks = self.tasks()
        original = self.bridge.build_task_card(tasks, "task-a")
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-project-without-name",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "option": "thesis",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["last_projects"]["ou_admin"], "thesis")
        updated = self.bridge.update_card.call_args.args[1]
        selector = next(
            item
            for item in updated["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual([option["value"] for option in selector["options"]], ["task-c"])

    def test_task_selector_marks_the_new_current_task(self):
        tasks = self.tasks()
        original = self.bridge.build_task_card(tasks, "task-a", "deepori")
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-task",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "action_name": "task_selector",
                "option": "task-b",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["selected"]["ou_admin"], "task-b")
        updated = self.bridge.update_card.call_args.args[1]
        self.assertEqual(updated["header"]["title"]["content"], "Task：Site")
        self.assertEqual(updated["header"]["subtitle"]["content"], "项目：deepori")
        self.assertIn("当前 Task 已切换", updated["body"]["elements"][0]["content"])

    def test_cancel_task_switch_keeps_current_task_and_restores_its_project(self):
        tasks = self.tasks()
        task = tasks[0]
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.task_by_id = lambda task_id, user_id: next(
            (candidate for candidate in tasks if candidate["id"] == task_id),
            None,
        )
        self.bridge.update_card = mock.Mock(return_value=True)
        state = {
            "selected": {"ou_admin": task["id"]},
            "last_projects": {"ou_admin": "thesis"},
            "task_queries": {"ou_admin": "paper"},
        }
        self.bridge.remember_card_context(
            state,
            "ou_admin",
            "om_switch",
            self.bridge.build_task_card(tasks, task["id"], "thesis"),
        )

        self.bridge.handle_card_event(
            {
                "event_id": "evt-cancel-switch",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_switch",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "cancel_task_switch", "task_id": task["id"]}
                ),
            }
        )

        saved = self.bridge.load_state()
        self.assertEqual(saved["selected"]["ou_admin"], task["id"])
        self.assertEqual(saved["last_projects"]["ou_admin"], task["project"])
        self.assertNotIn("ou_admin", saved.get("task_queries", {}))
        self.assertNotIn("om_switch", saved.get("card_contexts", {}))
        card = self.bridge.update_card.call_args.args[1]
        self.assertIn("已取消切换", card["body"]["elements"][0]["content"])
        self.assertEqual(card["header"]["title"]["content"], "Task：Home")

    def test_cancel_desktop_task_switch_returns_to_sync_confirmation(self):
        tasks = self.tasks()
        task = tasks[0]
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.task_by_id = lambda task_id, user_id: next(
            (candidate for candidate in tasks if candidate["id"] == task_id),
            None,
        )
        self.bridge.rollout_path_for_task = lambda task_id: None
        self.bridge.update_card = mock.Mock(return_value=True)
        state = {"selected": {"ou_admin": task["id"]}}
        self.bridge.remember_card_context(
            state,
            "ou_admin",
            "om_sync_switch",
            self.bridge.build_task_card(tasks, task["id"], task["project"]),
            "desktop_sync_selection",
        )

        self.bridge.handle_card_event(
            {
                "event_id": "evt-cancel-sync-switch",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_sync_switch",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "cancel_task_switch", "task_id": task["id"]}
                ),
            }
        )

        card = self.bridge.update_card.call_args.args[1]
        buttons = {
            element.get("text", {}).get("content"): element
            for element in card["body"]["elements"]
            if element.get("tag") == "button"
        }
        self.assertIn("接续当前 Task", buttons)
        self.assertEqual(
            self.bridge.load_state()["card_contexts"]["om_sync_switch"]["type"],
            "desktop_sync_confirmation",
        )

    def test_old_task_list_cannot_override_a_new_project_selection(self):
        tasks = self.tasks()
        old_card = self.bridge.build_task_card(tasks, "task-a", "deepori")
        new_card = self.bridge.build_task_card(tasks, "task-c", "thesis")
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.task_by_id = lambda task_id, user_id: next(
            (task for task in tasks if task["id"] == task_id),
            None,
        )
        self.bridge.update_card = mock.Mock(return_value=True)
        state = {
            "selected": {"ou_admin": "task-c"},
            "last_projects": {"ou_admin": "thesis"},
        }
        self.bridge.remember_card_context(
            state,
            "ou_admin",
            "om_task_card",
            new_card,
        )

        self.bridge.handle_card_event(
            {
                "event_id": "evt-old-project-task",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_task_card",
                "action_tag": "select_static",
                "action_name": "task_selector",
                "option": "task-b",
                "token": "token-test",
                "card_content": json.dumps(old_card),
            }
        )

        saved = self.bridge.load_state()
        self.assertEqual(saved["selected"]["ou_admin"], "task-c")
        self.assertEqual(saved["last_projects"]["ou_admin"], "thesis")
        updated = self.bridge.update_card.call_args.args[1]
        self.assertIn("项目已经切换", updated["body"]["elements"][0]["content"])
        selector = next(
            element
            for element in updated["body"]["elements"]
            if element.get("name") == "task_selector"
        )
        self.assertEqual([option["value"] for option in selector["options"]], ["task-c"])

    def test_identity_refresh_scheduler_coalesces_latest_state_per_user(self):
        first, second = self.tasks()[:2]

        self.bridge.schedule_user_task_identity_refresh("ou_admin", "第一次", first)
        self.bridge.schedule_user_task_identity_refresh("ou_admin", "第二次", second)

        pending = self.bridge._identity_refresh_pending["ou_admin"]
        self.assertEqual(pending[0], "第二次")
        self.assertEqual(pending[1], second)

    def test_task_scope_selector_switches_to_recent_use(self):
        tasks = self.tasks()
        original = self.bridge.build_task_card(tasks, "task-a", "deepori")
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.task_by_id = lambda task_id, user_id: next(
            (task for task in tasks if task["id"] == task_id),
            None,
        )
        self.bridge.save_state(
            {
                "selected": {"ou_admin": "task-a"},
                "recent_task_ids": {"ou_admin": ["task-b", "task-a"]},
            }
        )
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-scope",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "action_name": "task_scope_selector",
                "option": "recent",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        self.assertEqual(
            self.bridge.load_state()["task_scopes"]["ou_admin"],
            "recent",
        )
        card = self.bridge.update_card.call_args.args[1]
        selector = next(
            item
            for item in card["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual(
            [option["value"] for option in selector["options"]],
            ["task-b", "task-a"],
        )

    def test_favorite_button_toggles_current_task(self):
        task = self.tasks()[0]
        self.bridge.recent_tasks = lambda user_id: self.tasks()
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.update_card = mock.Mock(return_value=True)
        self.bridge.schedule_user_task_identity_refresh = mock.Mock()

        self.bridge.handle_card_event(
            {
                "event_id": "evt-favorite",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_card",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "toggle_task_favorite", "task_id": "task-a"}
                ),
            }
        )

        self.assertEqual(
            self.bridge.load_state()["favorite_task_ids"]["ou_admin"],
            ["task-a"],
        )
        card = self.bridge.update_card.call_args.args[1]
        self.assertIn(
            "已收藏",
            [tag["text"]["content"] for tag in card["header"]["text_tag_list"]],
        )

    def test_file_and_audio_are_native_desktop_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory) / "report.pdf"
            audio = Path(directory) / "note.m4a"
            document.write_bytes(b"pdf")
            audio.write_bytes(b"audio")
            attachments = [
                {"path": str(document), "label": "report.pdf", "kind": "file"},
                {"path": str(audio), "label": "note.m4a", "kind": "audio"},
            ]

            items = self.bridge.codex_turn_input("分析", [], attachments)
            context = self.bridge.codex_attachments(attachments)

        self.assertEqual(
            items[1],
            {"type": "mention", "name": "report.pdf", "path": str(document.resolve())},
        )
        self.assertEqual(items[2], {"type": "localAudio", "path": str(audio.resolve())})
        self.assertEqual(context[0]["fsPath"], str(document.resolve()))

    def test_downloaded_file_is_confined_and_normalized(self):
        root = Path(self.temporary.name)
        downloaded = root / "downloaded"
        downloaded.write_bytes(b"content")
        attachment, error = self.bridge.downloaded_file_path(
            json.dumps({"ok": True, "data": {"saved_path": str(downloaded)}}),
            root,
            1,
            "报告.pdf",
            "file",
        )

        self.assertEqual(error, "")
        self.assertEqual(attachment["kind"], "file")
        self.assertTrue(Path(attachment["path"]).is_file())
        self.assertEqual(Path(attachment["path"]).suffix, ".pdf")

    def test_file_resource_is_downloaded_as_bot_without_forcing_extension_loss(self):
        root = Path(self.temporary.name)

        def complete(command, **kwargs):
            downloaded = root / "原始报告.pdf"
            downloaded.write_bytes(b"content")
            return subprocess.CompletedProcess(
                command,
                0,
                json.dumps({"ok": True, "data": {"saved_path": str(downloaded)}}),
                "",
            )

        with mock.patch.object(self.bridge.subprocess, "run", side_effect=complete) as run:
            attachment, error = self.bridge.download_input_file(
                "om_test",
                "file_v3_test",
                root,
                1,
                "原始报告.pdf",
                "file",
            )

        self.assertEqual(error, "")
        command = run.call_args.args[0]
        self.assertNotIn("--output", command)
        self.assertEqual(command[command.index("--file-key") + 1], "file_v3_test")
        self.assertEqual(command[command.index("--as") + 1], "bot")
        self.assertEqual(attachment["kind"], "file")

    def test_message_handler_returns_while_task_runs(self):
        gate = threading.Event()
        self.bridge.selected_task = lambda user_id, state: self.tasks()[0]
        self.bridge.task_by_id = lambda task_id, user_id: self.tasks()[0]
        self.bridge.reply_card_message = lambda *args, **kwargs: (True, "om_progress")
        self.bridge.patch_card = lambda *args, **kwargs: True
        self.bridge.reply = lambda *args, **kwargs: True
        self.bridge.reply_or_queue = lambda *args, **kwargs: True

        def run(*args, **kwargs):
            gate.wait(2)
            return True, "完成", []

        self.bridge.run_codex = run
        started = time.monotonic()
        self.bridge.handle_message_event(
            {
                "chat_id": "oc_test",
                "chat_type": "p2p",
                "sender_id": "ou_admin",
                "sender_type": "user",
                "message_id": "om_test",
                "message_type": "text",
                "content": "继续",
            }
        )
        elapsed = time.monotonic() - started

        self.assertLess(elapsed, 0.5)
        self.assertIsNotNone(self.bridge.active_run_for_task("task-a"))
        gate.set()
        for _ in range(200):
            if self.bridge.active_run_for_task("task-a") is None:
                break
            time.sleep(0.01)
        self.assertIsNone(self.bridge.active_run_for_task("task-a"))

    def test_completed_result_becomes_current_before_its_reply_is_sent(self):
        task_a, task_b = self.tasks()[:2]
        state = self.bridge.load_state()
        state.setdefault("selected", {})["ou_admin"] = task_b["id"]
        self.bridge.save_state(state)
        run = self.bridge.new_run(
            "ou_admin",
            "oc_test",
            "om_task_a",
            task_a,
            [],
            [],
        )
        observed = {}
        self.bridge.task_by_id = lambda task_id, user_id: (
            task_a if task_id == task_a["id"] else task_b
        )
        self.bridge.run_codex = lambda *args, **kwargs: (True, "A 的最终结果", [])
        self.bridge.restore_pending_task_name = lambda *args: False
        self.bridge.set_run_progress = mock.Mock()
        self.bridge.schedule_user_task_identity_refresh = mock.Mock()
        self.bridge.update_current_status_card = lambda *args, **kwargs: True
        self.bridge.start_next_queued_input = lambda *args: None
        self.bridge.remove_active_run = lambda *args: None

        def deliver(message_id, content, kind):
            observed["selected"] = self.bridge.load_state()["selected"]["ou_admin"]
            observed["content"] = content
            return True

        self.bridge.reply_or_queue = deliver
        self.bridge.process_message_run(run, "执行 A", [], [], "", "text")

        self.assertEqual(observed["selected"], task_a["id"])
        self.assertTrue(observed["content"].startswith("🟢 当前 Task\n"))
        self.assertIn("Task：Home", observed["content"])
        self.bridge.schedule_user_task_identity_refresh.assert_called_once_with(
            "ou_admin",
            "当前 Task 已跟随最新结果",
            task_a,
        )

    def test_latest_completed_result_wins_across_parallel_tasks(self):
        task_a, task_b = self.tasks()[:2]
        state = self.bridge.load_state()
        state.setdefault("selected", {})["ou_admin"] = task_b["id"]
        self.bridge.save_state(state)
        self.bridge.task_by_id = lambda task_id, user_id: next(
            task for task in (task_a, task_b) if task["id"] == task_id
        )

        self.assertTrue(self.bridge.follow_result_task("ou_admin", task_a))
        self.assertTrue(self.bridge.follow_result_task("ou_admin", task_b))
        self.assertEqual(
            self.bridge.load_state()["selected"]["ou_admin"],
            task_b["id"],
        )

    def test_busy_task_queues_message_and_runs_it_next(self):
        gate = threading.Event()
        calls = []
        cards = []
        card_ids = iter(("om_progress_1", "om_progress_2"))
        self.bridge.selected_task = lambda user_id, state: self.tasks()[0]
        self.bridge.task_by_id = lambda task_id, user_id: self.tasks()[0]
        self.bridge.reply_card_message = lambda message_id, card, kind: (
            cards.append(card) or True,
            next(card_ids),
        )
        self.bridge.patch_card = lambda *args, **kwargs: True
        self.bridge.reply = lambda *args, **kwargs: True
        self.bridge.reply_or_queue = lambda *args, **kwargs: True

        def refresh_status(*args, **kwargs):
            if kwargs.get("task") is not None:
                raise RuntimeError("status unavailable")
            return True

        self.bridge.update_current_status_card = mock.Mock(side_effect=refresh_status)

        def run(task_id, prompt, **kwargs):
            calls.append(prompt)
            if len(calls) == 1:
                gate.wait(2)
            return True, f"完成：{prompt}", []

        self.bridge.run_codex = run
        base_event = {
            "chat_id": "oc_test",
            "chat_type": "p2p",
            "sender_id": "ou_admin",
            "sender_type": "user",
            "message_type": "text",
        }
        self.bridge.handle_message_event(
            {**base_event, "message_id": "om_first", "content": "第一条"}
        )
        self.bridge.handle_message_event(
            {**base_event, "message_id": "om_second", "content": "第二条"}
        )

        state = self.bridge.load_state()
        self.assertEqual(len(state["pending_inputs"]), 1)
        self.assertEqual(calls, ["第一条"])
        self.assertEqual(
            [tag["text"]["content"] for tag in cards[1]["header"]["text_tag_list"]],
            ["当前 Task", "已排队"],
        )
        gate.set()
        for _ in range(200):
            if calls == ["第一条", "第二条"] and not self.bridge.active_run_for_task("task-a"):
                break
            time.sleep(0.01)

        self.assertEqual(calls, ["第一条", "第二条"])
        self.assertEqual(self.bridge.load_state().get("pending_inputs"), [])
        self.assertGreaterEqual(self.bridge.update_current_status_card.call_count, 2)

    def test_queued_message_can_be_canceled_from_its_card(self):
        entry = {
            "queue_id": "queue-1",
            "user_id": "ou_admin",
            "chat_id": "oc_test",
            "source_message_id": "om_source",
            "task": self.tasks()[0],
            "content": "稍后执行",
            "image_keys": [],
            "file_keys": [],
            "raw_content": '{"text":"稍后执行"}',
            "message_type": "text",
            "created_at": time.time(),
            "available_at": 0,
            "ready": True,
            "progress_message_id": "om_queue_card",
        }
        self.assertTrue(self.bridge.enqueue_pending_input(entry)[0])
        patched = []
        self.bridge.patch_card = lambda message_id, card: patched.append(card) or True

        self.bridge.handle_card_event(
            {
                "type": "card.action.trigger",
                "event_id": "evt-cancel-queue",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_queue_card",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "cancel_queued_input", "queue_id": "queue-1"}
                ),
            }
        )

        self.assertEqual(self.bridge.load_state().get("pending_inputs"), [])
        self.assertEqual(
            [tag["text"]["content"] for tag in patched[0]["header"]["text_tag_list"]],
            ["当前 Task", "已取消"],
        )

    def test_desktop_busy_response_converts_direct_message_to_queue(self):
        self.bridge.selected_task = lambda user_id, state: self.tasks()[0]
        self.bridge.reply_card_message = lambda *args, **kwargs: (True, "om_progress")
        patched = []
        self.bridge.patch_card = lambda message_id, card: patched.append(card) or True
        self.bridge.reply = lambda *args, **kwargs: True
        self.bridge.reply_or_queue = lambda *args, **kwargs: True
        self.bridge.run_codex = lambda *args, **kwargs: (
            False,
            "当前 task 正在运行，请稍后重试。",
            [],
        )

        self.bridge.handle_message_event(
            {
                "chat_id": "oc_test",
                "chat_type": "p2p",
                "sender_id": "ou_admin",
                "sender_type": "user",
                "message_type": "text",
                "message_id": "om_busy",
                "content": "排队执行",
            }
        )
        for _ in range(100):
            if self.bridge.load_state().get("pending_inputs"):
                break
            time.sleep(0.01)

        queued = self.bridge.load_state()["pending_inputs"]
        self.assertEqual(len(queued), 1)
        self.assertEqual(queued[0]["content"], "排队执行")
        self.assertEqual(
            [tag["text"]["content"] for tag in patched[-1]["header"]["text_tag_list"]],
            ["当前 Task", "已排队"],
        )

    def test_desktop_unavailable_message_waits_for_user_choice(self):
        self.bridge.selected_task = lambda user_id, state: self.tasks()[0]
        self.bridge.reply_card_message = lambda *args, **kwargs: (True, "om_progress")
        patched = []
        self.bridge.patch_card = lambda message_id, card: patched.append(card) or True
        self.bridge.reply = lambda *args, **kwargs: True
        self.bridge.reply_or_queue = lambda *args, **kwargs: True

        def unavailable(*args, **kwargs):
            raise self.bridge.DesktopUnavailableError("no-client-found")

        self.bridge.run_codex = unavailable
        self.bridge.handle_message_event(
            {
                "chat_id": "oc_test",
                "chat_type": "p2p",
                "sender_id": "ou_admin",
                "sender_type": "user",
                "message_type": "text",
                "message_id": "om_desktop_unavailable",
                "content": "等待 Desktop",
            }
        )
        for _ in range(100):
            if not self.bridge.active_run_for_task("task-a"):
                break
            time.sleep(0.01)

        pending = self.bridge.load_state().get("pending_cli_fallbacks", {})
        self.assertEqual(len(pending), 1)
        self.assertEqual(next(iter(pending.values()))["content"], "等待 Desktop")
        self.assertEqual(
            [tag["text"]["content"] for tag in patched[-1]["header"]["text_tag_list"]],
            ["当前 Task", "等待选择"],
        )

    def test_progress_card_patch_moves_transient_failure_to_background(self):
        failure = subprocess.CompletedProcess(
            ["lark-cli"],
            4,
            json.dumps(
                {
                    "ok": False,
                    "error": {"type": "network", "subtype": "temporary"},
                }
            ),
            "",
        )
        success = subprocess.CompletedProcess(
            ["lark-cli"], 0, json.dumps({"ok": True}), ""
        )
        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            return_value=failure,
        ) as first_run:
            patched = self.bridge.patch_card(
                "om_progress",
                self.bridge.build_task_card(self.tasks(), "task-a", "deepori"),
            )

        self.assertFalse(patched)
        first_run.assert_called_once()
        pending = self.bridge.load_state()["pending_replies"]
        self.assertEqual(len(pending), 1)
        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            return_value=success,
        ) as background_run:
            self.assertTrue(
                self.bridge.retry_pending_replies(
                    now=float(pending[0]["next_attempt_at"]),
                )
            )

        background_run.assert_called_once()
        self.assertEqual(self.bridge.load_state()["pending_replies"], [])

    def test_stop_button_uses_confirmed_desktop_interrupt_protocol(self):
        run = {
            "run_id": "run-1",
            "user_id": "ou_admin",
            "chat_id": "oc_test",
            "source_message_id": "om_source",
            "task": self.tasks()[0],
            "turn_id": "turn-1",
            "status": "运行中",
            "outcome": "running",
            "started_at": time.time(),
            "attachment_count": 0,
            "cancel_event": threading.Event(),
            "cancel_confirmed": threading.Event(),
            "ipc_connection": object(),
            "ipc_send_lock": threading.Lock(),
            "ipc_pending_lock": threading.RLock(),
            "ipc_pending": {},
        }
        self.bridge.register_active_run(run)
        self.bridge.patch_card = lambda *args, **kwargs: True
        called = []

        def interrupt(run):
            called.append((run["task"]["id"], run["turn_id"]))
            return True

        self.bridge.interrupt_desktop_turn = interrupt
        self.bridge.handle_card_event(
            {
                "type": "card.action.trigger",
                "event_id": "evt-stop",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "button",
                "action_value": json.dumps({"action": "stop_run", "run_id": "run-1"}),
            }
        )
        for _ in range(100):
            if called:
                break
            time.sleep(0.01)

        self.assertTrue(run["cancel_event"].is_set())
        self.assertTrue(run["cancel_confirmed"].is_set())
        self.assertEqual(called, [("task-a", "turn-1")])

    def test_stop_button_is_secondary_and_requires_confirmation(self):
        card = self.bridge.build_run_card(
            {
                "run_id": "run-1",
                "task": self.tasks()[0],
                "status": "运行中",
                "outcome": "running",
                "started_at": time.time(),
                "attachment_count": 0,
            }
        )
        button = next(
            item for item in card["body"]["elements"] if item.get("tag") == "button"
        )

        self.assertEqual(button["text"]["content"], "停止运行…")
        self.assertEqual(button["type"], "default")
        self.assertNotIn("width", button)
        self.assertIn("confirm", button)
        self.assertEqual(button["confirm"]["title"]["content"], "确认停止当前运行？")

    def test_desktop_unavailable_card_requires_explicit_cli_choice(self):
        card = self.bridge.build_run_card(
            {
                "run_id": "run-1",
                "fallback_id": "fallback-1",
                "task": self.tasks()[0],
                "status": "等待你选择执行方式",
                "outcome": "desktop_unavailable",
                "started_at": time.time(),
                "attachment_count": 0,
            }
        )
        buttons = {
            item["text"]["content"]: item
            for item in card["body"]["elements"]
            if item.get("tag") == "button"
        }

        self.assertEqual(
            list(buttons),
            ["重试 Desktop", "使用备用 CLI…", "取消本条消息"],
        )
        self.assertEqual(
            buttons["重试 Desktop"]["behaviors"][0]["value"]["action"],
            "retry_desktop",
        )
        self.assertIn("confirm", buttons["使用备用 CLI…"])

    def test_retry_desktop_immediately_disables_the_retry_button(self):
        task = self.tasks()[0]
        self.bridge.save_state(
            {
                "pending_cli_fallbacks": {
                    "fallback-1": {
                        "fallback_id": "fallback-1",
                        "user_id": "ou_admin",
                        "chat_id": "oc_test",
                        "source_message_id": "om_source",
                        "progress_message_id": "om_progress",
                        "task": task,
                        "content": "继续处理",
                        "image_keys": [],
                        "file_keys": [],
                        "raw_content": "继续处理",
                        "message_type": "text",
                        "reason": "no-client-found",
                        "created_at": time.time(),
                    }
                }
            }
        )
        self.bridge.task_by_id = lambda task_id, user_id: task
        self.bridge.patch_card = mock.Mock(return_value=True)
        self.bridge.update_current_status_card = mock.Mock()
        worker = mock.Mock()

        with mock.patch.object(
            self.bridge.threading,
            "Thread",
            return_value=worker,
        ):
            self.bridge.handle_card_event(
                {
                    "type": "card.action.trigger",
                    "event_id": "evt-retry-desktop",
                    "operator_id": "ou_admin",
                    "chat_id": "oc_test",
                    "message_id": "om_progress",
                    "action_tag": "button",
                    "action_value": json.dumps(
                        {
                            "action": "retry_desktop",
                            "fallback_id": "fallback-1",
                        }
                    ),
                }
            )

        run = next(iter(self.bridge._active_runs.values()))
        self.assertEqual(run["outcome"], "desktop_retrying")
        card = self.bridge.patch_card.call_args.args[1]
        retry_button = next(
            item
            for item in card["body"]["elements"]
            if item.get("tag") == "button"
        )
        self.assertEqual(retry_button["text"]["content"], "正在重试 Desktop…")
        self.assertTrue(retry_button["disabled"])
        self.assertNotIn("behaviors", retry_button)
        worker.start.assert_called_once()
        self.bridge.remove_active_run(str(run["run_id"]))

    def test_retry_desktop_becomes_running_after_desktop_accepts(self):
        run = self.bridge.new_run(
            "ou_admin",
            "oc_test",
            "om_source",
            self.tasks()[0],
            [],
            [],
            "om_progress",
        )
        run["outcome"] = "desktop_retrying"
        self.bridge.patch_card = mock.Mock(return_value=True)

        self.bridge.message_run_started(run, "Codex Desktop 已接收，正在运行")

        self.assertEqual(run["outcome"], "running")
        card = self.bridge.patch_card.call_args.args[1]
        button = next(
            item
            for item in card["body"]["elements"]
            if item.get("tag") == "button"
        )
        self.assertEqual(button["text"]["content"], "停止运行…")

    def test_cli_fallback_button_starts_only_after_explicit_confirmation(self):
        task = self.tasks()[0]
        self.bridge.save_state(
            {
                "pending_cli_fallbacks": {
                    "fallback-1": {
                        "fallback_id": "fallback-1",
                        "user_id": "ou_admin",
                        "chat_id": "oc_test",
                        "source_message_id": "om_source",
                        "progress_message_id": "om_progress",
                        "task": task,
                        "content": "继续处理",
                        "image_keys": [],
                        "file_keys": [],
                        "raw_content": "继续处理",
                        "message_type": "text",
                        "reason": "no-client-found",
                        "created_at": time.time(),
                    }
                }
            }
        )
        self.bridge.task_by_id = lambda task_id, user_id: task
        started = []
        self.bridge.start_claimed_run = lambda run, *args: started.append((run, args))

        self.bridge.handle_card_event(
            {
                "type": "card.action.trigger",
                "event_id": "evt-use-cli",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_progress",
                "action_tag": "button",
                "action_value": json.dumps(
                    {
                        "action": "use_cli_fallback",
                        "fallback_id": "fallback-1",
                    }
                ),
            }
        )

        self.assertEqual(len(started), 1)
        self.assertTrue(started[0][0]["use_cli_fallback"])
        self.assertEqual(started[0][1][0], "继续处理")
        self.assertEqual(
            self.bridge.load_state().get("pending_cli_fallbacks"),
            {},
        )
        self.bridge.remove_active_run(str(started[0][0]["run_id"]))

    def test_approval_decisions_use_desktop_protocol(self):
        calls = []
        self.bridge.send_run_ipc_request = lambda run, method, version, params: calls.append(
            (method, version, params)
        ) or {"resultType": "success"}
        run = {"task": self.tasks()[0]}

        self.assertTrue(
            self.bridge.respond_desktop_approval(
                run,
                {"type": "command", "request_id": "req-1"},
                True,
            )
        )
        self.assertEqual(calls[0][0], "thread-follower-command-approval-decision")
        self.assertEqual(calls[0][1], 1)
        self.assertEqual(calls[0][2]["decision"], "accept")

    def test_interrupt_uses_expected_turn_guard(self):
        calls = []
        self.bridge.send_run_ipc_request = lambda run, method, version, params: calls.append(
            (method, version, params)
        ) or {"resultType": "success"}
        run = {"task": self.tasks()[0], "turn_id": "turn-1"}

        self.assertTrue(self.bridge.interrupt_desktop_turn(run))
        self.assertEqual(calls[0][0], "thread-follower-interrupt-turn")
        self.assertEqual(calls[0][1], 4)
        self.assertEqual(calls[0][2]["mode"], "user-stop")
        self.assertEqual(calls[0][2]["expectedTurnId"], "turn-1")

    def test_state_updates_from_workers_do_not_overwrite_each_other(self):
        barrier = threading.Barrier(3)

        def queue_reply():
            barrier.wait()
            self.bridge.queue_pending_reply("om_test", "结果", "final", "网络失败")

        def remember_turn():
            barrier.wait()
            self.bridge.remember_bridge_turn("turn-1")

        workers = [
            threading.Thread(target=queue_reply),
            threading.Thread(target=remember_turn),
        ]
        for worker in workers:
            worker.start()
        barrier.wait()
        for worker in workers:
            worker.join()

        state = self.bridge.load_state()
        self.assertEqual(state["bridge_turns"], ["turn-1"])
        self.assertEqual(state["pending_replies"][0]["message_id"], "om_test")

    def test_progress_card_patch_uses_message_patch_api(self):
        completed = subprocess.CompletedProcess([], 0, '{"ok":true}', "")
        with mock.patch.object(self.bridge.subprocess, "run", return_value=completed) as run:
            self.assertTrue(self.bridge.patch_card("om_progress", {"schema": "2.0"}))

        command = run.call_args.args[0]
        self.assertEqual(command[command.index("--message-id") + 1], "om_progress")
        payload = json.loads(command[command.index("--data") + 1])
        self.assertEqual(json.loads(payload["content"])["schema"], "2.0")

    def test_follower_actions_share_the_turn_owner_connection(self):
        client, server = self.bridge.socket.socketpair()
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        rollout.write_text("", encoding="utf-8")
        run = {
            "task": self.tasks()[0],
            "ipc_connection": client,
            "ipc_client_id": "owner-client",
            "ipc_send_lock": threading.Lock(),
            "ipc_pending_lock": threading.RLock(),
            "ipc_pending": {},
        }
        result = []

        def request():
            result.append(
                self.bridge.send_run_ipc_request(
                    run,
                    "thread-follower-interrupt-turn",
                    4,
                    {
                        "conversationId": "task-a",
                        "mode": "user-stop",
                        "expectedTurnId": "turn-1",
                    },
                    2000,
                )
            )

        def desktop():
            request_frame = self.bridge.receive_ipc_message(server)
            self.bridge.send_ipc_message(
                server,
                {
                    "type": "response",
                    "requestId": request_frame["requestId"],
                    "resultType": "success",
                },
            )
            with rollout.open("a", encoding="utf-8") as handle:
                handle.write(
                    json.dumps(
                        {
                            "type": "event_msg",
                            "payload": {
                                "type": "task_complete",
                                "turn_id": "turn-1",
                                "last_agent_message": "完成",
                            },
                        }
                    )
                    + "\n"
                )

        requester = threading.Thread(target=request)
        desktop_side = threading.Thread(target=desktop)
        requester.start()
        desktop_side.start()
        try:
            success, message, _ = self.bridge.wait_for_desktop_turn(
                rollout,
                0,
                "turn-1",
                ipc_connection=client,
                on_ipc_response=lambda response: self.bridge.complete_run_ipc_response(
                    run,
                    response,
                ),
            )
        finally:
            requester.join()
            desktop_side.join()
            client.close()
            server.close()

        self.assertTrue(success)
        self.assertEqual(message, "完成")
        self.assertEqual(result[0]["resultType"], "success")

    def test_following_subscribes_without_replaying_complete_history(self):
        client, server = self.bridge.socket.socketpair()
        server.settimeout(0.05)
        try:
            self.bridge.begin_desktop_following(client, "bridge-client", "task-a")

            following = self.bridge.receive_ipc_message(server)
            with self.assertRaises(self.bridge.socket.timeout):
                self.bridge.receive_ipc_message(server)
        finally:
            client.close()
            server.close()

        self.assertEqual(following["method"], "thread-stream-following-changed")
        self.assertTrue(following["params"]["following"])

    def test_approval_request_is_read_directly_from_stream_patch(self):
        change = {
            "type": "patches",
            "patches": [
                {
                    "op": "add",
                    "path": ["requests", "req-1"],
                    "value": {
                        "id": "req-1",
                        "method": "item/commandExecution/requestApproval",
                        "params": {"command": "echo test"},
                    },
                }
            ],
        }

        approvals = self.bridge.approval_requests_from_stream_change(change)

        self.assertEqual(len(approvals), 1)
        self.assertEqual(approvals[0]["request_id"], "req-1")
        self.assertEqual(approvals[0]["type"], "command")


if __name__ == "__main__":
    unittest.main()
