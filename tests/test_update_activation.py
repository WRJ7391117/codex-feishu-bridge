import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Sources/CodexFeishuBridgeApp/main.swift"


class UpdateActivationHookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")

    def test_activation_check_is_rate_limited_and_non_overlapping(self):
        coordinator = self.source.split(
            "private final class SparkleUpdateCoordinator", 1
        )[1].split("private final class BridgeViewModel", 1)[0]
        method = coordinator.split(
            "func checkForUpdatesAfterActivation()", 1
        )[1].split("func waitForBridgeToBecomeIdle", 1)[0]

        self.assertIn(
            "private static let activationCheckMinimumInterval: TimeInterval = 60",
            coordinator,
        )
        self.assertIn("controller.updater.canCheckForUpdates", method)
        self.assertIn("!controller.updater.sessionInProgress", method)
        self.assertIn("lastActivationCheckAt", method)
        self.assertIn("controller.updater.lastUpdateCheckDate", method)
        self.assertIn("Self.activationCheckMinimumInterval", method)
        self.assertIn("controller.updater.checkForUpdatesInBackground()", method)
        self.assertNotIn("Timer", method)

    def test_app_activation_and_reopen_use_the_background_hook(self):
        delegate = self.source.split(
            "private final class AppDelegate", 1
        )[1]
        did_become_active = delegate.split(
            "func applicationDidBecomeActive", 1
        )[1].split("func applicationShouldHandleReopen", 1)[0]
        reopen = delegate.split(
            "func applicationShouldHandleReopen", 1
        )[1].split("func applicationShouldTerminateAfterLastWindowClosed", 1)[0]

        self.assertIn("model.checkForUpdatesAfterActivation()", did_become_active)
        self.assertIn("model.checkForUpdatesAfterActivation()", reopen)
        self.assertNotIn("Timer", did_become_active)
        self.assertNotIn("Timer", reopen)


if __name__ == "__main__":
    unittest.main()
