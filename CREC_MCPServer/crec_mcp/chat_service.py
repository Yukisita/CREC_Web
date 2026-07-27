"""Application service that coordinates prompts, the LLM, and action policy."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

import logging
from typing import Mapping, Protocol, Sequence

from .actions import ActionPolicy, has_hallucinated_action, strip_actions
from .conversation import ChatMessage, PromptBuilder, build_messages
from .llm_client import LlmResponse


DELETION_BLOCKED_MESSAGE = (
    "⚠️ Collection deletion cannot be performed via AI.\n"
    "To delete, please use the delete button on the page manually."
)

HALLUCINATION_MESSAGE = (
    "⚠️ An operation was attempted but did not actually execute.\n"
    "Please try again or describe the operation more specifically."
)


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
        self._logger = logging.getLogger(__name__)

    async def process(
        self,
        *,
        message: str,
        history: list[Mapping[str, str]],
        page_context: str = "",
        page_title: str = "CREC Web",
        project_name: str = "CREC Web",
    ) -> str:
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
        warnings: list[str] = []

        if not raw_response:
            warnings.append("empty_response")
            self._audit(
                message=message,
                page_title=page_title,
                page_context=page_context,
                project_name=project_name,
                raw_response="",
                final_response="",
                warnings=warnings,
                duration_ms=llm_response.duration_ms,
            )
            return ""

        result = self._action_policy.sanitize_response(raw_response)
        if result.blocked_deletion:
            warnings.append("blocked_deletion")
            self._logger.warning(
                "LLM attempted a blocked collection-deletion action"
            )
            final_response = DELETION_BLOCKED_MESSAGE
        else:
            final_response = result.text
            if not strip_actions(final_response).strip() and final_response.strip():
                final_response = "Executing operation.\n" + final_response

            if has_hallucinated_action(final_response):
                warnings.append("hallucination_detected")
                self._logger.warning(
                    "LLM claimed action completion without an action tag"
                )
                final_response = HALLUCINATION_MESSAGE

        self._audit(
            message=message,
            page_title=page_title,
            page_context=page_context,
            project_name=project_name,
            raw_response=raw_response,
            final_response=final_response,
            warnings=warnings,
            duration_ms=llm_response.duration_ms,
        )
        return final_response

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
