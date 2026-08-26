import unittest

from crec_mcp.actions import (
    ActionPolicy,
    format_action,
    has_hallucinated_action,
)


class ActionPolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.policy = ActionPolicy(
            safe_button_ids=frozenset({"saveButton", "deleteButton"}),
            safe_input_ids=frozenset({"nameInput"}),
            blocked_button_ids=frozenset({"deleteButton"}),
        )

    def test_keeps_allowed_click(self) -> None:
        response = 'Saving.\n<action>{"type":"clickButton","id":"saveButton"}</action>'

        result = self.policy.sanitize_response(response)

        self.assertEqual(response, result.text)
        self.assertFalse(result.blocked_deletion)

    def test_removes_disallowed_click(self) -> None:
        response = '<action>{"type":"clickButton","id":"otherButton"}</action>'

        result = self.policy.sanitize_response(response)

        self.assertEqual("", result.text)
        self.assertFalse(result.blocked_deletion)

    def test_blocks_deletion_even_if_whitelisted(self) -> None:
        response = '<action>{"type":"clickButton","id":"deleteButton"}</action>'

        result = self.policy.sanitize_response(response)

        self.assertEqual("", result.text)
        self.assertTrue(result.blocked_deletion)

    def test_removes_malformed_action_instead_of_passing_it_to_browser(self) -> None:
        response = '<action>{"type":"clickButton","id":"deleteButton"}}</action>'

        result = self.policy.sanitize_response(response)

        self.assertEqual("", result.text)

    def test_removes_unknown_action_type(self) -> None:
        response = '<action>{"type":"runArbitraryCode"}</action>'

        result = self.policy.sanitize_response(response)

        self.assertEqual("", result.text)

    def test_validates_same_origin_navigation(self) -> None:
        safe = '<action>{"type":"navigate","path":"/ProjectEdit"}</action>'
        unsafe = '<action>{"type":"navigate","path":"//example.com"}</action>'

        self.assertEqual(safe, self.policy.sanitize_response(safe).text)
        self.assertEqual("", self.policy.sanitize_response(unsafe).text)


class ActionFormattingTests(unittest.TestCase):
    def test_format_action_escapes_user_text(self) -> None:
        action = format_action("search", text='camera "A"')

        self.assertEqual(
            '<action>{"type":"search","text":"camera \\"A\\""}</action>',
            action,
        )

    def test_detects_completion_claim_without_action(self) -> None:
        self.assertTrue(has_hallucinated_action("保存しました。"))
        self.assertFalse(has_hallucinated_action("保存しましたか？"))
        self.assertFalse(
            has_hallucinated_action(
                '保存しました。<action>{"type":"clickButton","id":"saveButton"}</action>'
            )
        )


if __name__ == "__main__":
    unittest.main()
