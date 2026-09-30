"""Transport-independent values shared by chat orchestration and LLM clients."""

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class LlmResponse:
    text: str
    duration_ms: int
