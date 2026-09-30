import unittest
from pathlib import Path

from crec_mcp.config import (
    BLOCKED_BUTTON_IDS,
    DEFAULT_SAFE_BUTTON_IDS,
    Settings,
)


class SettingsTests(unittest.TestCase):
    def test_uses_documented_defaults(self) -> None:
        settings = Settings.from_environment({}, Path("project"))

        self.assertEqual("http://localhost:1234", settings.llm_url)
        self.assertEqual("google/gemma-4-e2b", settings.llm_model)
        self.assertEqual("127.0.0.1", settings.mcp_host)
        self.assertEqual(8765, settings.mcp_port)
        self.assertEqual(Path("project/prompts"), settings.prompts_dir)
        self.assertEqual(DEFAULT_SAFE_BUTTON_IDS, settings.safe_button_ids)
        self.assertEqual(BLOCKED_BUTTON_IDS, settings.blocked_button_ids)

    def test_normalizes_comma_separated_allowlist(self) -> None:
        settings = Settings.from_environment(
            {"SAFE_BUTTON_IDS": " first,second, first, "},
            Path("project"),
        )

        self.assertEqual(
            frozenset({"first", "second"}),
            settings.safe_button_ids,
        )


if __name__ == "__main__":
    unittest.main()
