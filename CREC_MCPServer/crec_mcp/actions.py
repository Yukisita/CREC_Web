"""Parse model output into display text and a validated, indivisible action plan."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

import json
import math
import re
from typing import Any, Mapping

from .models import ChatResponse

ACTION_PATTERN = re.compile(r"<action>(.*?)</action>", re.DOTALL)
ACTION_FIELDS = {
    "search": {"type", "text"},
    "showCollectionPanel": {"type", "id"},
    "openCollectionByName": {"type", "name"},
    "navigateToCollectionByName": {"type", "name"},
    "showAdminPanel": {"type"},
    "createNewCollection": {"type"},
    "navigateHome": {"type"},
    "navigate": {"type", "path"},
    "clickButton": {"type", "id"},
    "fillInput": {"type", "id", "value"},
    "switchLanguage": {"type", "lang"},
}
MAX_ACTIONS = 32


class ActionPolicy:
    """Validate the entire plan before any part of it reaches the browser."""

    def __init__(
        self,
        safe_button_ids: frozenset[str],
        safe_input_ids: frozenset[str],
        blocked_button_ids: frozenset[str],
    ) -> None:
        self.safe_button_ids = safe_button_ids
        self.safe_input_ids = safe_input_ids
        self.blocked_button_ids = blocked_button_ids

    def parse_response(self, text: str) -> ChatResponse:
        actions: list[dict[str, Any]] = []
        invalid = False
        blocked_deletion = False
        for match in ACTION_PATTERN.finditer(text):
            try:
                command = json.loads(match.group(1), object_pairs_hook=_unique_object)
            except ValueError:
                invalid = True
                continue
            if (
                isinstance(command, dict)
                and command.get("type") == "clickButton"
                and isinstance(command.get("id"), str)
                and command["id"] in self.blocked_button_ids
            ):
                blocked_deletion = True
            if not self.is_action_allowed(command):
                invalid = True
            else:
                actions.append(command)

        display_text = ACTION_PATTERN.sub("", text)
        # A truncated tag must never leave a later save button executable.
        invalid |= "<action" in display_text.lower() or "</action" in display_text.lower()
        if blocked_deletion:
            return ChatResponse(warning="deletion_blocked")
        if invalid or len(actions) > MAX_ACTIONS:
            return ChatResponse(warning="invalid_actions")

        display_text = re.sub(r"```[^\n]*\n\s*```", "", display_text)
        display_text = re.sub(r"`\s*`", "", display_text)
        display_text = re.sub(r"^[ \t]*[`{}\[\]]+[ \t]*$", "", display_text, flags=re.MULTILINE)
        display_text = re.sub(r"\n{3,}", "\n\n", display_text).strip()
        return ChatResponse(text=display_text, actions=actions)

    def is_action_allowed(self, command: Any) -> bool:
        if not isinstance(command, dict):
            return False
        action_type = command.get("type")
        if not isinstance(action_type, str) or action_type not in ACTION_FIELDS:
            return False
        if command.keys() != ACTION_FIELDS[action_type]:
            return False
        if action_type == "clickButton":
            return isinstance(command["id"], str) and self.is_button_allowed(command["id"])
        if action_type == "fillInput":
            value = command["value"]
            return (
                isinstance(command["id"], str)
                and self.is_input_allowed(command["id"])
                and not isinstance(value, bool)
                and isinstance(value, (str, int, float))
                and (not isinstance(value, (int, float)) or abs(value) <= 9007199254740991 and math.isfinite(value))
            )
        if action_type == "search":
            return isinstance(command["text"], str)
        if action_type == "showCollectionPanel":
            return _has_non_empty_string(command, "id")
        if action_type in {"openCollectionByName", "navigateToCollectionByName"}:
            return _has_non_empty_string(command, "name")
        if action_type == "navigate":
            return is_local_path(command["path"])
        if action_type == "switchLanguage":
            return isinstance(command["lang"], str) and command["lang"] in {"ja", "en", "de"}
        return True

    def is_button_allowed(self, button_id: str) -> bool:
        return button_id in self.safe_button_ids and button_id not in self.blocked_button_ids

    def is_input_allowed(self, field_id: str) -> bool:
        return field_id in self.safe_input_ids


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate action field: {key}")
        result[key] = value
    return result


def _has_non_empty_string(command: Mapping[str, Any], key: str) -> bool:
    value = command.get(key)
    return isinstance(value, str) and bool(value.strip())


def is_local_path(path: Any) -> bool:
    return (
        isinstance(path, str)
        and path.startswith("/")
        and not path.startswith("//")
        and "\\" not in path
        and not any(ord(character) < 32 or ord(character) == 127 for character in path)
    )


def format_action(action_type: str, **arguments: Any) -> str:
    payload = json.dumps(
        {"type": action_type, **arguments}, ensure_ascii=False,
        allow_nan=False, separators=(",", ":"),
    ).replace("<", r"\u003c")
    return f"<action>{payload}</action>"
