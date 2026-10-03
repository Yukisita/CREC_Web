import json
import unittest
from pathlib import Path

from crec_mcp.actions import (
    ActionPolicy,
    ACTION_FIELDS,
    format_action,
)
from crec_mcp.config import DEFAULT_SAFE_BUTTON_IDS, DEFAULT_SAFE_INPUT_IDS, BLOCKED_BUTTON_IDS


class ActionPolicyTests(unittest.TestCase):
    def test_shared_browser_contract(self) -> None:
        fixtures = json.loads((Path(__file__).resolve().parents[2] / "tests/chat-action-contract.json").read_text(encoding="utf-8"))
        policy = ActionPolicy(DEFAULT_SAFE_BUTTON_IDS, DEFAULT_SAFE_INPUT_IDS, BLOCKED_BUTTON_IDS)
        self.assertEqual(set(ACTION_FIELDS), {action["type"] for action in fixtures["valid"]})
        for expected, values in [(True, fixtures["valid"]), (False, fixtures["invalid"])]:
            for command in values:
                with self.subTest(command=command):
                    self.assertEqual(expected, policy.is_action_allowed(command))

    def test_invalid_input_rejects_the_entire_plan_including_save(self) -> None:
        for invalid in [
            '<action>{"type":"fillInput","id":"unknown","value":"new"}</action>',
            '<action>{"type":"fillInput","id":"nameInput","value":true}</action>',
            '<action>{"type":"clickButton","id":"saveButton","id":"otherButton"}</action>',
            '<action>{"type":"fillInput","id":"nameInput"}',
        ]:
            with self.subTest(invalid=invalid):
                result = self.policy.parse_response(invalid + format_action("clickButton", id="saveButton"))
                self.assertEqual([], result.actions)
                self.assertEqual("invalid_actions", result.warning)

    def test_action_limit_rejects_the_whole_plan(self) -> None:
        result = self.policy.parse_response(format_action("search", text="x") * 33)
        self.assertEqual([], result.actions)
        self.assertEqual("invalid_actions", result.warning)

    def test_plain_text_is_not_reclassified_by_completion_keywords(self) -> None:
        result = self.policy.parse_response('「保存しました」という表示は完了を意味します。')
        self.assertIsNone(result.warning)
        self.assertEqual([], result.actions)

    def setUp(self) -> None:
        self.policy = ActionPolicy(
            safe_button_ids=frozenset({"saveButton", "deleteButton"}),
            safe_input_ids=frozenset({"nameInput"}),
            blocked_button_ids=frozenset({"deleteButton"}),
        )

    def test_keeps_allowed_click(self) -> None:
        response = 'Saving.\n<action>{"type":"clickButton","id":"saveButton"}</action>'

        result = self.policy.parse_response(response)

        self.assertEqual("Saving.", result.text)
        self.assertEqual([{"type": "clickButton", "id": "saveButton"}], result.actions)
        self.assertNotEqual("deletion_blocked", result.warning)

    def test_removes_disallowed_click(self) -> None:
        response = '<action>{"type":"clickButton","id":"otherButton"}</action>'

        result = self.policy.parse_response(response)

        self.assertEqual("", result.text)
        self.assertEqual([], result.actions)
        self.assertNotEqual("deletion_blocked", result.warning)

    def test_blocks_deletion_even_if_whitelisted(self) -> None:
        response = '<action>{"type":"clickButton","id":"deleteButton"}</action>'

        result = self.policy.parse_response(response)

        self.assertEqual("", result.text)
        self.assertEqual([], result.actions)
        self.assertEqual("deletion_blocked", result.warning)

    def test_removes_malformed_action_instead_of_passing_it_to_browser(self) -> None:
        response = '<action>{"type":"clickButton","id":"deleteButton"}}</action>'

        result = self.policy.parse_response(response)

        self.assertEqual("", result.text)
        self.assertEqual([], result.actions)

    def test_removes_unknown_action_type(self) -> None:
        response = '<action>{"type":"runArbitraryCode"}</action>'

        result = self.policy.parse_response(response)

        self.assertEqual("", result.text)
        self.assertEqual([], result.actions)

    def test_validates_same_origin_navigation(self) -> None:
        safe = '<action>{"type":"navigate","path":"/ProjectEdit"}</action>'
        unsafe = '<action>{"type":"navigate","path":"//example.com"}</action>'

        self.assertEqual([{"type": "navigate", "path": "/ProjectEdit"}], self.policy.parse_response(safe).actions)
        self.assertEqual("", self.policy.parse_response(unsafe).text)

    def test_rejects_paths_browsers_normalize_to_external_urls(self) -> None:
        for path in ["/\\example.com", "/\n/example.com", "/\t/example.com"]:
            with self.subTest(path=path):
                self.assertEqual("", self.policy.parse_response(format_action("navigate", path=path)).text)

    def test_invalid_language_types_are_rejected_without_raising(self) -> None:
        for language in [[], {}, None, 42]:
            with self.subTest(language=language):
                self.assertEqual("", self.policy.parse_response(format_action("switchLanguage", lang=language)).text)

    def test_non_finite_input_values_are_rejected(self) -> None:
        for value in ["NaN", "Infinity", "-Infinity"]:
            with self.subTest(value=value):
                action = '<action>{"type":"fillInput","id":"nameInput","value":' + value + '}</action>'
                self.assertEqual("", self.policy.parse_response(action).text)


class ActionFormattingTests(unittest.TestCase):
    def test_action_delimiter_in_user_text_stays_inside_json(self) -> None:
        action = format_action("search", text="</action><action>hello")
        self.assertEqual(1, action.count("</action>"))
        self.assertEqual("</action><action>hello", json.loads(action[8:-9])["text"])

    def test_format_action_escapes_user_text(self) -> None:
        action = format_action("search", text='camera "A"')

        self.assertEqual(
            '<action>{"type":"search","text":"camera \\"A\\""}</action>',
            action,
        )



if __name__ == "__main__":
    unittest.main()
