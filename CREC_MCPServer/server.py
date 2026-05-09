"""
CREC Web MCP Server
Copyright (c) [2026] [S.Yukisita]
This software is released under the MIT License.

Python MCP server that provides AI chat support for CREC Web.
The server exposes a `process_chat` tool that:
  - Loads multilingual system prompt templates from the prompts/ directory
  - Calls any OpenAI-compatible LLM backend
  - Validates action IDs in the LLM response against configurable whitelists
  - Returns the validated response

The CREC Web C# backend acts as an MCP client and calls this server's
`process_chat` tool for each user chat message.
"""

import json
import logging
import os
import re
from pathlib import Path
from typing import Any

import httpx
from mcp.server.fastmcp import FastMCP

_logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Configuration (from environment variables with sensible defaults)
# ---------------------------------------------------------------------------

LLM_URL: str = os.getenv("LLM_URL", "http://localhost:1234").rstrip("/")
LLM_MODEL: str = os.getenv("LLM_MODEL", "gemma-3-12b")
MCP_HOST: str = os.getenv("MCP_HOST", "127.0.0.1")
MCP_PORT: int = int(os.getenv("MCP_PORT", "8765"))

# Whitelist of element IDs the AI is permitted to click
_SAFE_BUTTON_IDS_DEFAULT = ",".join([
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
    # ProjectEdit page
    "projectEditSaveBtn",
    # Collection index edit modal
    "saveIndexEdit",
    # View/filter controls
    "toggleAdvancedFiltersButton",
    "gridViewBtn",
    "tableViewBtn",
])

# Whitelist of form field IDs the AI is permitted to fill
_SAFE_INPUT_IDS_DEFAULT = ",".join([
    # Inventory operation modal
    "operationType",
    "operationQuantity",
    "operationComment",
    # Inventory management settings modal
    "safetyStock",
    "reorderPoint",
    "maximumLevel",
    # Search / filter controls
    "searchText",
    "searchField",
    "searchMethod",
    "inventoryStatusFilter",
    # Collection index edit modal
    "editName",
    "editManagementCode",
    "editRegistrationDate",
    "editCategory",
    "editFirstTag",
    "editSecondTag",
    "editThirdTag",
    "editLocation",
    # Project settings page
    "editProjectName",
    "editCollectionNameLabel",
    "editUUIDLabel",
    "editManagementCodeLabel",
    "editCategoryLabel",
    "editTag1Label",
    "editTag2Label",
    "editTag3Label",
])

SAFE_BUTTON_IDS: frozenset[str] = frozenset(
    os.getenv("SAFE_BUTTON_IDS", _SAFE_BUTTON_IDS_DEFAULT).split(",")
)
SAFE_INPUT_IDS: frozenset[str] = frozenset(
    os.getenv("SAFE_INPUT_IDS", _SAFE_INPUT_IDS_DEFAULT).split(",")
)

PROMPTS_DIR: Path = Path(__file__).parent / "prompts"

# Regex to find <action>…</action> blocks in LLM responses
_ACTION_RE: re.Pattern = re.compile(r"<action>([\s\S]*?)</action>")

# Maximum timeout for LLM requests (seconds)
LLM_TIMEOUT: float = float(os.getenv("LLM_TIMEOUT", "120"))

# Maximum characters of page context to inject into the system prompt.
# Keeping this small prevents the system prompt from exceeding the model's
# context window (n_keep >= n_ctx 400 errors).  Clients may send up to
# CHAT_PAGE_CONTEXT_MAX characters; this cap applies an additional server-side
# guard.
MAX_CONTEXT_CHARS: int = int(os.getenv("MAX_CONTEXT_CHARS", "1200"))

# Maximum number of prior conversation turns (user+assistant pairs) to include
# in each LLM request.  Older turns are dropped to stay within n_ctx.
MAX_HISTORY_TURNS: int = int(os.getenv("MAX_HISTORY_TURNS", "8"))

# ---------------------------------------------------------------------------
# Prompt helpers
# ---------------------------------------------------------------------------

_prompt_cache: dict[str, str] = {}


def _load_prompt_template() -> str:
    """Load and cache the single system prompt template."""
    cache_key = "ja"
    if cache_key in _prompt_cache:
        return _prompt_cache[cache_key]

    path = PROMPTS_DIR / "system_prompt.ja.txt"
    if not path.exists():
        return ""

    text = path.read_text(encoding="utf-8")
    _prompt_cache[cache_key] = text
    return text


def _build_system_prompt(lang: str, page_title: str, page_context: str, project_name: str) -> str:
    """Build system prompt by filling template placeholders.

    The page_context is truncated to MAX_CONTEXT_CHARS before injection to
    prevent the system prompt from exceeding the model's context window
    (which causes 400 Bad Request: n_keep >= n_ctx).
    """
    template = _load_prompt_template()

    if not template:
        return ""

    context_raw = page_context.strip() or "（コンテンツなし）"
    # Truncate context to avoid exceeding the model's context window
    if len(context_raw) > MAX_CONTEXT_CHARS:
        context_raw = context_raw[:MAX_CONTEXT_CHARS] + "\n…（コンテキスト省略）"
        _logger.debug("page_context truncated to %d chars", MAX_CONTEXT_CHARS)

    return (
        template
        .replace("{{projectName}}", project_name)
        .replace("{{pageTitle}}", page_title)
        .replace("{{context}}", context_raw)
    )


# ---------------------------------------------------------------------------
# Whitelist sanitizer
# ---------------------------------------------------------------------------

def _sanitize_response(text: str) -> str:
    """
    Remove or strip any <action> blocks that reference element IDs not in the
    server-side whitelist.  Only clickButton and fillInput are ID-gated; other
    action types (search, navigate, showAdminPanel, openCollection) are passed
    through unchanged.
    """
    def _check(match: re.Match) -> str:
        raw = match.group(1).strip()
        try:
            cmd: dict[str, Any] = json.loads(raw)
        except json.JSONDecodeError:
            # Unparseable action – pass through unchanged (client will ignore)
            return match.group(0)

        action_type = cmd.get("type", "")

        if action_type == "clickButton":
            elem_id = str(cmd.get("id", ""))
            if elem_id not in SAFE_BUTTON_IDS:
                return ""  # strip unsafe action

        elif action_type == "fillInput":
            elem_id = str(cmd.get("id", ""))
            if elem_id not in SAFE_INPUT_IDS:
                return ""  # strip unsafe action

        return match.group(0)  # keep safe action unchanged

    return _ACTION_RE.sub(_check, text)


# ---------------------------------------------------------------------------
# Message history sanitizer
# ---------------------------------------------------------------------------

def _sanitize_messages(messages: list[dict[str, str]]) -> list[dict[str, str]]:
    """
    Ensure the conversation messages list has strictly alternating user/assistant
    roles.  System messages are preserved as-is at the front.

    Rules applied (in order):
    1. Consecutive messages of the same non-system role are collapsed — the
       later message replaces the earlier one (keeps the most recent intent).
    2. Leading assistant messages (appearing before the first user message) are
       dropped — some models (e.g. Gemma 4) reject conversations that start
       with an assistant turn.

    This fixes intermittent 400 Bad Request errors that occur when the
    client-side session retains orphaned user messages from previous failed
    requests, creating non-trailing consecutive user turns in history.
    """
    system_msgs = [m for m in messages if m.get("role") == "system"]
    conv_msgs   = [m for m in messages if m.get("role") in ("user", "assistant")]

    # Collapse consecutive same-role messages (last message in each run wins)
    sanitized: list[dict[str, str]] = []
    for msg in conv_msgs:
        if sanitized and sanitized[-1]["role"] == msg["role"]:
            sanitized[-1] = msg
        else:
            sanitized.append(msg)

    # Drop leading assistant messages
    first_user = next(
        (i for i, m in enumerate(sanitized) if m["role"] == "user"),
        len(sanitized),
    )
    sanitized = sanitized[first_user:]

    return system_msgs + sanitized


# ---------------------------------------------------------------------------
# MCP server definition
# ---------------------------------------------------------------------------

mcp = FastMCP("CREC Web AI Server")


@mcp.tool()
async def process_chat(
    message: str,
    history: list[dict[str, str]],
    page_context: str = "",
    page_title: str = "CREC Web",
    lang: str = "ja",
    project_name: str = "CREC Web",
) -> str:
    """
    Process a user chat message by calling an OpenAI-compatible LLM backend and
    returning the validated AI response.

    The response may contain <action>…</action> tags that instruct the CREC Web
    frontend to perform UI operations.  Action IDs are validated against
    server-side whitelists before being returned; disallowed IDs are stripped.

    Args:
        message:      The user's message text.
        history:      Prior conversation turns as a list of
                      {"role": "user"|"assistant", "content": "…"} dicts.
        page_context: Excerpt of the current page's visible text (RAG).
        page_title:   Browser tab title of the current page.
        lang:         UI language code – "ja", "en", or "de".
        project_name: CREC Web project name shown to the user.

    Returns:
        Validated AI response text (may include <action> tags).
    """
    system_prompt = _build_system_prompt(lang, page_title, page_context, project_name)

    messages: list[dict[str, str]] = []
    if system_prompt:
        messages.append({"role": "system", "content": system_prompt})

    # Limit history to the most recent MAX_HISTORY_TURNS turns to stay within
    # the model's context window.  Each "turn" = one user + one assistant message.
    history_turns = history[-MAX_HISTORY_TURNS * 2:] if history else []

    for turn in history_turns:
        role = turn.get("role", "")
        content = turn.get("content", "")
        if role in ("user", "assistant") and content:
            messages.append({"role": role, "content": content})

    # Sanitize to ensure strictly alternating user/assistant roles throughout.
    # The trailing-user guard below only handles the last turn; this handles
    # non-trailing consecutive same-role messages that can accumulate when
    # previous requests failed without cleaning up the client-side session
    # (e.g. page navigation mid-request).  Strict models such as Gemma 4
    # return 400 Bad Request when consecutive same-role messages are present.
    messages = _sanitize_messages(messages)

    # Remove any trailing user messages from history (defensive guard).
    # This can happen when a previous request failed after the user message was
    # already saved to the client-side session – leaving an orphaned user turn
    # with no assistant reply.  Sending two consecutive user messages to the LLM
    # causes a 400 Bad Request.
    removed = 0
    while messages and messages[-1]["role"] == "user":
        messages.pop()
        removed += 1
    if removed:
        _logger.warning(
            "process_chat: removed %d orphaned user message(s) from history "
            "(client-side session was likely corrupted by a previous error)",
            removed,
        )

    messages.append({"role": "user", "content": message})

    payload = {
        "model": LLM_MODEL,
        "messages": messages,
        "stream": False,
    }

    async with httpx.AsyncClient(timeout=LLM_TIMEOUT) as client:
        response = await client.post(
            f"{LLM_URL}/v1/chat/completions",
            json=payload,
            headers={"Content-Type": "application/json"},
        )
        if not response.is_success:
            _logger.error(
                "LLM API returned %d. Response body: %s",
                response.status_code,
                response.text[:500],
            )
        response.raise_for_status()

    data = response.json()
    text: str = (
        data.get("choices", [{}])[0]
        .get("message", {})
        .get("content", "")
        or ""
    )

    if not text:
        return ""

    sanitized = _sanitize_response(text)

    # Ensure the response always contains human-readable text in addition to
    # any <action> tags.  Small models (≤4b) sometimes output only action XML;
    # prepend a short fallback sentence so the chat bubble is never blank.
    text_only = _ACTION_RE.sub("", sanitized).strip()
    if not text_only and sanitized.strip():
        sanitized = "操作を実行します。\n" + sanitized

    return sanitized


@mcp.tool()
def search_collections(keyword: str) -> str:
    """
    Return the action tag that makes the CREC Web frontend search collections
    by the given keyword.  The actual search runs in the browser.

    Args:
        keyword: Search keyword.

    Returns:
        An <action> tag string for the frontend to execute.
    """
    payload = json.dumps({"type": "search", "text": keyword}, ensure_ascii=False)
    return f"<action>{payload}</action>"


@mcp.tool()
def navigate(path: str) -> str:
    """
    Return the action tag that navigates the CREC Web frontend to the given
    same-origin path.

    Args:
        path: Absolute path starting with "/", e.g. "/" or "/ProjectEdit".

    Returns:
        An <action> tag string for the frontend to execute.
    """
    if not path.startswith("/") or path.startswith("//"):
        return f'[ERROR] Invalid path "{path}": must be an absolute same-origin path.'
    payload = json.dumps({"type": "navigate", "path": path}, ensure_ascii=False)
    return f"<action>{payload}</action>"


@mcp.tool()
def show_admin_panel() -> str:
    """
    Return the action tag that opens the CREC Web admin panel.

    Returns:
        An <action> tag string for the frontend to execute.
    """
    return '<action>{"type":"showAdminPanel"}</action>'


@mcp.tool()
def click_button(button_id: str) -> str:
    """
    Return the action tag that clicks a whitelisted button in the CREC Web UI.

    Allowed IDs: addNewCollectionBtn, editProjectBtn, adminPanelToggle,
    searchButton, clearFiltersButton, inventoryOperationBtn,
    inventoryManagementSettingsBtn, inventoryOperationSave,
    inventoryOperationCancel, inventoryManagementSettingsSave,
    inventoryManagementSettingsCancel, editIndexBtn.

    Args:
        button_id: The HTML element ID of the button to click.

    Returns:
        An <action> tag string for the frontend to execute, or an error string
        if the ID is not whitelisted.
    """
    if button_id not in SAFE_BUTTON_IDS:
        return f'[ERROR] Button ID "{button_id}" is not in the allowed list.'
    payload = json.dumps({"type": "clickButton", "id": button_id}, ensure_ascii=False)
    return f"<action>{payload}</action>"


@mcp.tool()
def fill_input(field_id: str, value: str) -> str:
    """
    Return the action tag that fills a whitelisted form field in the CREC Web
    UI.

    Allowed IDs: operationType, operationQuantity, operationComment,
    searchText, safetyStock, reorderPoint, maximumLevel, searchField,
    searchMethod, inventoryStatusFilter.

    Args:
        field_id: The HTML element ID of the form field.
        value:    The value to put in the field.

    Returns:
        An <action> tag string for the frontend to execute, or an error string
        if the ID is not whitelisted.
    """
    if field_id not in SAFE_INPUT_IDS:
        return f'[ERROR] Input ID "{field_id}" is not in the allowed list.'
    payload = json.dumps({"type": "fillInput", "id": field_id, "value": value}, ensure_ascii=False)
    return f"<action>{payload}</action>"


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    import uvicorn

    print(f"Starting CREC Web MCP Server on {MCP_HOST}:{MCP_PORT}")
    print(f"LLM backend: {LLM_URL}  model: {LLM_MODEL}")

    # streamable_http トランスポートでASGIアプリを取得してuvicornで起動
    uvicorn.run(
        mcp.streamable_http_app(),
        host=MCP_HOST,
        port=MCP_PORT,
        log_level="info"
    )
