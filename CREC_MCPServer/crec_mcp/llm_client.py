"""Client for OpenAI-compatible local LLM backends."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

import logging
import time
from dataclasses import dataclass
from typing import Any, Sequence

import httpx

from .conversation import ChatMessage


@dataclass(frozen=True, slots=True)
class LlmResponse:
    text: str
    duration_ms: int


class OpenAIChatClient:
    """Send non-streaming chat-completion requests to the configured backend."""

    def __init__(self, base_url: str, model: str, timeout: float) -> None:
        self._completion_url = f"{base_url.rstrip('/')}/v1/chat/completions"
        self._model = model
        self._timeout = timeout
        self._logger = logging.getLogger(__name__)

    async def complete(self, messages: Sequence[ChatMessage]) -> LlmResponse:
        payload = {
            "model": self._model,
            "messages": list(messages),
            "stream": False,
            # llama.cpp / LM Studio extension: retain the complete system prompt
            # when the backend slides its context window.
            "n_keep": -1,
        }

        started_at = time.perf_counter()
        async with httpx.AsyncClient(timeout=self._timeout) as client:
            response = await client.post(self._completion_url, json=payload)
            if not response.is_success:
                self._logger.error(
                    "LLM API returned %d. Response body: %s",
                    response.status_code,
                    response.text[:500],
                )
            response.raise_for_status()

        duration_ms = int((time.perf_counter() - started_at) * 1000)
        return LlmResponse(
            text=_extract_message_text(response.json()),
            duration_ms=duration_ms,
        )


def _extract_message_text(data: Any) -> str:
    if not isinstance(data, dict):
        return ""

    choices = data.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""

    first_choice = choices[0]
    if not isinstance(first_choice, dict):
        return ""

    message = first_choice.get("message")
    if not isinstance(message, dict):
        return ""

    content = message.get("content")
    return content if isinstance(content, str) else ""
