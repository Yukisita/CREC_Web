"""Action formatting and validation for browser-executable AI responses."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from typing import Any, Mapping


ACTION_PATTERN = re.compile(r"<action>([\s\S]*?)</action>")

FRONTEND_ACTION_TYPES = frozenset(
    {
        "search",
        "showCollectionPanel",
        "openCollectionByName",
        "navigateToCollectionByName",
        "showAdminPanel",
        "createNewCollection",
        "navigateHome",
        "navigate",
        "clickButton",
        "fillInput",
        "switchLanguage",
    }
)

_HALLUCINATION_PHRASES = (
    "保存しました",
    "クリックしました",
    "入力しました",
    "実行しました",
    "操作しました",
    "変更しました",
    "設定しました",
    "登録しました",
    "削除しました",
    "追加しました",
    "更新しました",
    "検索しました",
    "遷移しました",
    "切り替えました",
    "押しました",
    "開きました",
    "閉じました",
)

_QUESTION_LOOKAHEAD = 5
_LANGUAGE_CODES = frozenset({"ja", "en", "de"})


@dataclass(frozen=True, slots=True)
class SanitizationResult:
    """Result of validating all action tags in an LLM response."""

    text: str
    blocked_deletion: bool = False


class ActionPolicy:
    """Validate LLM-generated actions against the frontend contract."""

    def __init__(
        self,
        safe_button_ids: frozenset[str],
        safe_input_ids: frozenset[str],
        blocked_button_ids: frozenset[str],
    ) -> None:
        self.safe_button_ids = safe_button_ids
        self.safe_input_ids = safe_input_ids
        self.blocked_button_ids = blocked_button_ids

    def sanitize_response(self, text: str) -> SanitizationResult:
        """Remove malformed, unknown, or disallowed actions.

        Validation is fail-closed: an action is returned to the browser only
        when its JSON payload and required fields are valid.
        """

        blocked_deletion = False

        def validate_match(match: re.Match[str]) -> str:
            nonlocal blocked_deletion

            try:
                command = json.loads(match.group(1).strip())
            except json.JSONDecodeError:
                return ""

            if not isinstance(command, dict):
                return ""

            action_type = command.get("type")
            if (
                not isinstance(action_type, str)
                or action_type not in FRONTEND_ACTION_TYPES
            ):
                return ""

            if action_type == "clickButton":
                button_id = command.get("id")
                if not isinstance(button_id, str):
                    return ""
                if button_id in self.blocked_button_ids:
                    blocked_deletion = True
                    return ""
                if button_id not in self.safe_button_ids:
                    return ""
            elif action_type == "fillInput":
                field_id = command.get("id")
                if (
                    not isinstance(field_id, str)
                    or field_id not in self.safe_input_ids
                ):
                    return ""
                value = command.get("value")
                if isinstance(value, bool) or not isinstance(value, (str, int, float)):
                    return ""
            elif not _has_valid_payload(action_type, command):
                return ""

            return match.group(0)

        sanitized = ACTION_PATTERN.sub(validate_match, text)
        return SanitizationResult(sanitized, blocked_deletion)

    def is_button_allowed(self, button_id: str) -> bool:
        return (
            button_id in self.safe_button_ids
            and button_id not in self.blocked_button_ids
        )

    def is_input_allowed(self, field_id: str) -> bool:
        return field_id in self.safe_input_ids


def _has_valid_payload(action_type: str, command: Mapping[str, Any]) -> bool:
    if action_type == "search":
        return isinstance(command.get("text"), str)
    if action_type == "showCollectionPanel":
        return _has_non_empty_string(command, "id")
    if action_type in {"openCollectionByName", "navigateToCollectionByName"}:
        return _has_non_empty_string(command, "name")
    if action_type == "navigate":
        path = command.get("path")
        return (
            isinstance(path, str)
            and path.startswith("/")
            and not path.startswith("//")
        )
    if action_type == "switchLanguage":
        return command.get("lang") in _LANGUAGE_CODES
    return action_type in {"showAdminPanel", "createNewCollection", "navigateHome"}


def _has_non_empty_string(command: Mapping[str, Any], key: str) -> bool:
    value = command.get(key)
    return isinstance(value, str) and bool(value.strip())


def format_action(action_type: str, **arguments: Any) -> str:
    """Create an action tag using a compact, correctly escaped JSON payload."""

    payload = json.dumps(
        {"type": action_type, **arguments},
        ensure_ascii=False,
        separators=(",", ":"),
    )
    return f"<action>{payload}</action>"


def contains_actions(text: str) -> bool:
    return ACTION_PATTERN.search(text) is not None


def strip_actions(text: str) -> str:
    return ACTION_PATTERN.sub("", text)


def has_hallucinated_action(text: str) -> bool:
    """Detect Japanese completion claims that contain no executable action."""

    if contains_actions(text):
        return False

    for phrase in _HALLUCINATION_PHRASES:
        index = text.find(phrase)
        if index == -1:
            continue
        suffix_start = index + len(phrase)
        suffix = text[suffix_start : suffix_start + _QUESTION_LOOKAHEAD]
        if "か" not in suffix and "？" not in suffix:
            return True

    return False
