import importlib.util
import os
from pathlib import Path
import tempfile
import unittest


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
        spec = importlib.util.spec_from_file_location("bridge_under_test", BRIDGE_PATH)
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


class MultiUserTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        self.bridge.ALLOWED_USERS.clear()
        self.bridge.ALLOWED_USERS.update(
            {
                "ou_admin": {"*"},
                "ou_member": {"deepori"},
            }
        )
        self.bridge.PRIMARY_ALLOWED_USER = "ou_admin"

    def test_legacy_sender_keeps_all_projects(self):
        self.bridge.CONFIG = {"allowed_sender_id": "ou_legacy"}
        self.assertEqual(self.bridge.configured_allowed_users(), {"ou_legacy": {"*"}})

    def test_project_rules_filter_tasks_exactly(self):
        deepori = {"id": "task-a", "title": "A", "project": "deepori"}
        other = {"id": "task-b", "title": "B", "project": "other"}
        self.assertTrue(self.bridge.user_can_access_task("ou_admin", other))
        self.assertTrue(self.bridge.user_can_access_task("ou_member", deepori))
        self.assertFalse(self.bridge.user_can_access_task("ou_member", other))

    def test_each_user_keeps_an_independent_selection(self):
        tasks = {
            "ou_admin": [{"id": "task-a", "title": "A", "project": "other"}],
            "ou_member": [{"id": "task-b", "title": "B", "project": "deepori"}],
        }
        self.bridge.recent_tasks = lambda user_id: tasks[user_id]
        self.bridge.task_by_id = lambda thread_id, user_id: next(
            (task for task in tasks[user_id] if task["id"] == thread_id),
            None,
        )
        self.bridge.save_state = lambda state: None
        state = {}

        self.bridge.select_task("ou_admin", "1", state)
        self.bridge.select_task("ou_member", "1", state)

        self.assertEqual(
            state["selected"],
            {"ou_admin": "task-a", "ou_member": "task-b"},
        )

    def test_revoked_project_clears_previous_selection(self):
        state = {"selected": {"ou_member": "task-b"}}
        self.bridge.task_by_id = lambda thread_id, user_id: None
        self.bridge.save_state = lambda state: None

        self.assertIsNone(self.bridge.selected_task("ou_member", state))
        self.assertNotIn("ou_member", state["selected"])

    def test_unauthorized_user_events_are_ignored(self):
        self.bridge.load_state = lambda: self.fail("unauthorized event read state")
        self.bridge.handle_message_event(
            {
                "sender_id": "ou_intruder",
                "sender_type": "user",
                "message_type": "text",
            }
        )
        self.bridge.handle_card_event(
            {
                "operator_id": "ou_intruder",
                "action_tag": "select_static",
            }
        )
        self.bridge.handle_menu_event(
            {
                "operator_id": "ou_intruder",
                "event_key": self.bridge.TASK_MENU_EVENT_KEY,
            }
        )


if __name__ == "__main__":
    unittest.main()
