"""System-prompt rendering and conversation-history normalization."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

import logging
from pathlib import Path
from typing import Mapping


ChatMessage = dict[str, str]


class PromptBuilder:
    """Load the prompt template once and render request-specific context."""

    def __init__(self, prompts_dir: Path, max_context_chars: int) -> None:
        self._prompt_path = prompts_dir / "system_prompt.txt"
        self._max_context_chars = max_context_chars
        self._template: str | None = None
        self._logger = logging.getLogger(__name__)

    def build(self, page_title: str, page_context: str, project_name: str) -> str:
        template = self._load_template()
        if not template:
            return ""

        context = page_context.strip() or "(no content)"
        if len(context) > self._max_context_chars:
            context = (
                context[: self._max_context_chars]
                + "\n…(context truncated)"
            )
            self._logger.debug(
                "Page context truncated to %d characters",
                self._max_context_chars,
            )

        return (
            template.replace("{{projectName}}", project_name)
            .replace("{{pageTitle}}", page_title)
            .replace("{{context}}", context)
        )

    def _load_template(self) -> str:
        if self._template is not None:
            return self._template

        if not self._prompt_path.exists():
            self._logger.warning(
                "System prompt template not found: %s",
                self._prompt_path,
            )
            return ""

        self._template = self._prompt_path.read_text(encoding="utf-8")
        return self._template


def build_messages(
    *,
    system_prompt: str,
    history: list[Mapping[str, str]],
    user_message: str,
    max_history_turns: int,
) -> list[ChatMessage]:
    """Build a model-compatible message list from untrusted browser history."""

    recent_history = history[-max_history_turns * 2 :] if history else []
    normalized_history = normalize_history(recent_history)

    messages: list[ChatMessage] = []
    if system_prompt:
        messages.append({"role": "system", "content": system_prompt})
    messages.extend(normalized_history)
    messages.append({"role": "user", "content": user_message})
    return messages


def normalize_history(
    history: list[Mapping[str, str]],
) -> list[ChatMessage]:
    """Return alternating user/assistant messages ending with an assistant.

    Invalid roles and empty content are discarded. Consecutive messages with
    the same role are collapsed by keeping the latest message.
    """

    normalized: list[ChatMessage] = []

    for item in history:
        role = item.get("role", "")
        content = item.get("content", "")
        if role not in {"user", "assistant"} or not content:
            continue

        message = {"role": role, "content": content}
        if normalized and normalized[-1]["role"] == role:
            normalized[-1] = message
        else:
            normalized.append(message)

    while normalized and normalized[0]["role"] == "assistant":
        normalized.pop(0)
    while normalized and normalized[-1]["role"] == "user":
        normalized.pop()

    return normalized
