import tempfile
import unittest
from pathlib import Path

from crec_mcp.conversation import PromptBuilder, build_messages, normalize_history


class ConversationTests(unittest.TestCase):
    def test_normalize_history_keeps_latest_consecutive_message(self) -> None:
        history = [
            {"role": "assistant", "content": "leading"},
            {"role": "user", "content": "old intent"},
            {"role": "user", "content": "latest intent"},
            {"role": "assistant", "content": "answer"},
            {"role": "user", "content": "orphan"},
        ]

        self.assertEqual(
            [
                {"role": "user", "content": "latest intent"},
                {"role": "assistant", "content": "answer"},
            ],
            normalize_history(history),
        )

    def test_build_messages_limits_history_and_appends_current_user(self) -> None:
        history = [
            {"role": "user", "content": "old question"},
            {"role": "assistant", "content": "old answer"},
            {"role": "user", "content": "new question"},
            {"role": "assistant", "content": "new answer"},
        ]

        messages = build_messages(
            system_prompt="system",
            history=history,
            user_message="current question",
            max_history_turns=1,
        )

        self.assertEqual(
            [
                {"role": "system", "content": "system"},
                {"role": "user", "content": "new question"},
                {"role": "assistant", "content": "new answer"},
                {"role": "user", "content": "current question"},
            ],
            messages,
        )


class PromptBuilderTests(unittest.TestCase):
    def test_renders_placeholders_and_truncates_context(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            prompt_path = Path(directory) / "system_prompt.txt"
            prompt_path.write_text(
                "{{projectName}}|{{pageTitle}}|{{context}}",
                encoding="utf-8",
            )
            builder = PromptBuilder(Path(directory), max_context_chars=4)

            result = builder.build("Page", "123456", "Project")

        self.assertEqual(
            "Project|Page|1234\n…(context truncated)",
            result,
        )


if __name__ == "__main__":
    unittest.main()

