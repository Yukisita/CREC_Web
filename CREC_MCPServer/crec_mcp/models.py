"""Transport-independent values shared by chat orchestration and LLM clients."""

import json
from dataclasses import asdict, dataclass, field
from typing import Any


@dataclass(frozen=True, slots=True)
class LlmResponse:
    text: str
    duration_ms: int


@dataclass(frozen=True, slots=True)
class ChatResponse:
    """The JSON contract shared by the MCP tool, Web API and browser."""

    text: str = ""
    actions: list[dict[str, Any]] = field(default_factory=list)
    warning: str | None = None

    def to_json(self) -> str:
        return json.dumps(asdict(self), ensure_ascii=False, allow_nan=False)
