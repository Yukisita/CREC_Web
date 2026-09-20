"""MCP entry point for CREC Web AI chat and browser actions."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

from mcp.server.fastmcp import FastMCP

from crec_mcp.actions import ActionPolicy, format_action
from crec_mcp.audit_log import ChatLogger
from crec_mcp.chat_service import ChatService
from crec_mcp.config import Settings
from crec_mcp.conversation import PromptBuilder
from crec_mcp.llm_client import OpenAIChatClient


settings = Settings.from_environment()
chat_log = ChatLogger(
    log_dir=settings.chat_log_dir,
    retention_days=settings.chat_log_retention_days,
    max_field_length=settings.chat_log_max_field_length,
)
action_policy = ActionPolicy(
    safe_button_ids=settings.safe_button_ids,
    safe_input_ids=settings.safe_input_ids,
    blocked_button_ids=settings.blocked_button_ids,
)
chat_service = ChatService(
    prompt_builder=PromptBuilder(
        prompts_dir=settings.prompts_dir,
        max_context_chars=settings.max_context_chars,
    ),
    action_policy=action_policy,
    llm_client=OpenAIChatClient(
        base_url=settings.llm_url,
        model=settings.llm_model,
        timeout=settings.llm_timeout,
    ),
    audit_logger=chat_log,
    max_history_turns=settings.max_history_turns,
)

mcp = FastMCP("CREC Web AI Server")


@mcp.tool()
async def process_chat(
    message: str,
    history: list[dict[str, str]],
    page_context: str = "",
    page_title: str = "CREC Web",
    project_name: str = "CREC Web",
) -> str:
    """Generate a validated AI response for one CREC Web chat message."""

    return await chat_service.process(
        message=message,
        history=history,
        page_context=page_context,
        page_title=page_title,
        project_name=project_name,
    )


@mcp.tool()
def search_collections(keyword: str) -> str:
    """Return a browser action that searches collections by keyword."""

    return format_action("search", text=keyword)


@mcp.tool()
def navigate(path: str) -> str:
    """Return a browser action for a same-origin absolute path."""

    if not path.startswith("/") or path.startswith("//"):
        return (
            f'[ERROR] Invalid path "{path}": '
            "must be an absolute same-origin path."
        )
    return format_action("navigate", path=path)


@mcp.tool()
def show_admin_panel() -> str:
    """Return a browser action that opens the administration panel."""

    return format_action("showAdminPanel")


@mcp.tool()
def click_button(button_id: str) -> str:
    """Return a browser action that clicks an allowed button."""

    if not action_policy.is_button_allowed(button_id):
        return f'[ERROR] Button ID "{button_id}" is not in the allowed list.'
    return format_action("clickButton", id=button_id)


@mcp.tool()
def fill_input(field_id: str, value: str) -> str:
    """Return a browser action that fills an allowed form field."""

    if not action_policy.is_input_allowed(field_id):
        return f'[ERROR] Input ID "{field_id}" is not in the allowed list.'
    return format_action("fillInput", id=field_id, value=value)


def main() -> None:
    """Run the Streamable HTTP MCP transport."""

    import uvicorn

    print(
        "Starting CREC Web MCP Server on "
        f"{settings.mcp_host}:{settings.mcp_port}"
    )
    print(f"LLM backend: {settings.llm_url}  model: {settings.llm_model}")

    uvicorn.run(
        mcp.streamable_http_app(),
        host=settings.mcp_host,
        port=settings.mcp_port,
        log_level="info",
    )


if __name__ == "__main__":
    main()
