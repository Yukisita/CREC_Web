"""Environment-based configuration for the CREC Web MCP server."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping


DEFAULT_SAFE_BUTTON_IDS = frozenset(
    {
        "addNewCollectionBtn",
        "editProjectBtn",
        "adminPanelToggle",
        "searchButton",
        "clearFiltersButton",
        "inventoryOperationBtn",
        "inventoryManagementSettingsBtn",
        "inventoryOperationSave",
        "inventoryOperationCancel",
        "inventoryManagementSettingsSave",
        "inventoryManagementSettingsCancel",
        "editIndexBtn",
        "projectEditSaveBtn",
        "saveIndexEdit",
        "toggleAdvancedFiltersButton",
        "gridViewBtn",
        "tableViewBtn",
    }
)

DEFAULT_SAFE_INPUT_IDS = frozenset(
    {
        "operationType",
        "operationQuantity",
        "operationComment",
        "safetyStock",
        "reorderPoint",
        "maximumLevel",
        "searchText",
        "searchField",
        "searchMethod",
        "inventoryStatusFilter",
        "editName",
        "editManagementCode",
        "editRegistrationDate",
        "editCategory",
        "editFirstTag",
        "editSecondTag",
        "editThirdTag",
        "editLocation",
        "editProjectName",
        "editCollectionNameLabel",
        "editUUIDLabel",
        "editManagementCodeLabel",
        "editCategoryLabel",
        "editTag1Label",
        "editTag2Label",
        "editTag3Label",
    }
)

# This list is intentionally not configurable.
BLOCKED_BUTTON_IDS = frozenset({"deleteCollectionBtn"})


def _read_csv_set(
    environ: Mapping[str, str],
    name: str,
    default: frozenset[str],
) -> frozenset[str]:
    raw_value = environ.get(name)
    if raw_value is None:
        return default
    return frozenset(value.strip() for value in raw_value.split(",") if value.strip())


@dataclass(frozen=True, slots=True)
class Settings:
    """Runtime settings loaded once when the MCP server starts."""

    llm_url: str
    llm_model: str
    llm_timeout: float
    mcp_host: str
    mcp_port: int
    max_context_chars: int
    max_history_turns: int
    prompts_dir: Path
    chat_log_dir: Path
    chat_log_retention_days: int
    chat_log_max_field_length: int
    safe_button_ids: frozenset[str]
    safe_input_ids: frozenset[str]
    blocked_button_ids: frozenset[str] = BLOCKED_BUTTON_IDS

    @classmethod
    def from_environment(
        cls,
        environ: Mapping[str, str] | None = None,
        project_dir: Path | None = None,
    ) -> "Settings":
        source = os.environ if environ is None else environ
        root = (
            Path(__file__).resolve().parent.parent
            if project_dir is None
            else project_dir
        )

        return cls(
            llm_url=source.get("LLM_URL", "http://localhost:1234").rstrip("/"),
            llm_model=source.get("LLM_MODEL", "google/gemma-4-e2b"),
            llm_timeout=float(source.get("LLM_TIMEOUT", "120")),
            mcp_host=source.get("MCP_HOST", "127.0.0.1"),
            mcp_port=int(source.get("MCP_PORT", "8765")),
            max_context_chars=int(source.get("MAX_CONTEXT_CHARS", "3000")),
            max_history_turns=int(source.get("MAX_HISTORY_TURNS", "10")),
            prompts_dir=root / "prompts",
            chat_log_dir=Path(source.get("CHAT_LOG_DIR", "logs")),
            chat_log_retention_days=int(
                source.get("CHAT_LOG_RETENTION_DAYS", "90")
            ),
            chat_log_max_field_length=int(
                source.get("CHAT_LOG_MAX_FIELD_LENGTH", "10000")
            ),
            safe_button_ids=_read_csv_set(
                source,
                "SAFE_BUTTON_IDS",
                DEFAULT_SAFE_BUTTON_IDS,
            ),
            safe_input_ids=_read_csv_set(
                source,
                "SAFE_INPUT_IDS",
                DEFAULT_SAFE_INPUT_IDS,
            ),
        )
