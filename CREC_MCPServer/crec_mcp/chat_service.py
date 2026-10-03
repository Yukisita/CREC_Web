"""Application service that coordinates prompts, the LLM, and action policy."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

from typing import Mapping, Protocol, Sequence

from .actions import ActionPolicy
from .conversation import ChatMessage, PromptBuilder, build_messages
from .models import ChatResponse, LlmResponse


class LlmClient(Protocol):
    async def complete(self, messages: Sequence[ChatMessage]) -> LlmResponse:
        """Return one non-streaming assistant response."""


class AuditLogger(Protocol):
    def interaction(
        self,
        *,
        user_message: str,
        page_title: str = "",
        page_context: str = "",
        project_name: str = "",
        llm_raw_output: str = "",
        final_response: str = "",
        warnings: list[str] | None = None,
        duration_ms: int | None = None,
    ) -> None:
        """Record one completed interaction."""


class ChatService:
    """Process one browser chat request from prompt construction to auditing."""

    def __init__(
        self,
        *,
        prompt_builder: PromptBuilder,
        action_policy: ActionPolicy,
        llm_client: LlmClient,
        audit_logger: AuditLogger,
        max_history_turns: int,
    ) -> None:
        self._prompt_builder = prompt_builder
        self._action_policy = action_policy
        self._llm_client = llm_client
        self._audit_logger = audit_logger
        self._max_history_turns = max_history_turns

    async def process(
        self,
        *,
        message: str,
        history: list[Mapping[str, str]],
        page_context: str = "",
        page_title: str = "CREC Web",
        project_name: str = "CREC Web",
    ) -> ChatResponse:
        system_prompt = self._prompt_builder.build(
            page_title,
            page_context,
            project_name,
        )
        messages = build_messages(
            system_prompt=system_prompt,
            history=history,
            user_message=message,
            max_history_turns=self._max_history_turns,
        )

        llm_response = await self._llm_client.complete(messages)
        raw_response = llm_response.text
        response = self._action_policy.parse_response(raw_response)
        warnings = [response.warning] if response.warning else []
        if not response.text and not response.actions and not response.warning:
            warnings.append("empty_response")

        self._audit(
            message=message,
            page_title=page_title,
            page_context=page_context,
            project_name=project_name,
            raw_response=raw_response,
            final_response=response.to_json(),
            warnings=warnings,
            duration_ms=llm_response.duration_ms,
        )
        return response

    def _audit(
        self,
        *,
        message: str,
        page_title: str,
        page_context: str,
        project_name: str,
        raw_response: str,
        final_response: str,
        warnings: list[str],
        duration_ms: int,
    ) -> None:
        self._audit_logger.interaction(
            user_message=message,
            page_title=page_title,
            page_context=page_context,
            project_name=project_name,
            llm_raw_output=raw_response,
            final_response=final_response,
            warnings=warnings or None,
            duration_ms=duration_ms,
        )
