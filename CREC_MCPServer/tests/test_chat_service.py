import unittest
from pathlib import Path
from typing import Any, Sequence

from crec_mcp.actions import ActionPolicy
from crec_mcp.chat_service import (
    DELETION_BLOCKED_MESSAGE,
    ChatService,
)
from crec_mcp.conversation import ChatMessage, PromptBuilder
from crec_mcp.llm_client import LlmResponse


class FakeLlmClient:
    def __init__(self, response: str) -> None:
        self.response = response
        self.messages: list[ChatMessage] = []

    async def complete(self, messages: Sequence[ChatMessage]) -> LlmResponse:
        self.messages = list(messages)
        return LlmResponse(self.response, duration_ms=12)


class FakeAuditLogger:
    def __init__(self) -> None:
        self.entries: list[dict[str, Any]] = []

    def interaction(self, **entry: Any) -> None:
        self.entries.append(entry)


class FakePromptBuilder(PromptBuilder):
    def __init__(self) -> None:
        super().__init__(Path("."), max_context_chars=100)

    def build(self, page_title: str, page_context: str, project_name: str) -> str:
        return "system prompt"


class ChatServiceTests(unittest.IsolatedAsyncioTestCase):
    def create_service(
        self,
        llm_response: str,
    ) -> tuple[ChatService, FakeLlmClient, FakeAuditLogger]:
        llm_client = FakeLlmClient(llm_response)
        audit_logger = FakeAuditLogger()
        service = ChatService(
            prompt_builder=FakePromptBuilder(),
            action_policy=ActionPolicy(
                safe_button_ids=frozenset({"saveButton"}),
                safe_input_ids=frozenset(),
                blocked_button_ids=frozenset({"deleteButton"}),
            ),
            llm_client=llm_client,
            audit_logger=audit_logger,
            max_history_turns=10,
        )
        return service, llm_client, audit_logger

    async def test_adds_text_when_model_returns_only_an_action(self) -> None:
        action = '<action>{"type":"clickButton","id":"saveButton"}</action>'
        service, _, audit_logger = self.create_service(action)

        response = await service.process(message="save", history=[])

        self.assertEqual(f"Executing operation.\n{action}", response)
        self.assertEqual(response, audit_logger.entries[0]["final_response"])

    async def test_replaces_blocked_deletion_response(self) -> None:
        action = '<action>{"type":"clickButton","id":"deleteButton"}</action>'
        service, _, audit_logger = self.create_service(action)

        response = await service.process(message="delete", history=[])

        self.assertEqual(DELETION_BLOCKED_MESSAGE, response)
        self.assertEqual(
            ["blocked_deletion"],
            audit_logger.entries[0]["warnings"],
        )


if __name__ == "__main__":
    unittest.main()

