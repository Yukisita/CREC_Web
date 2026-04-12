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
import os
import re
from pathlib import Path
from typing import Any

import httpx
from mcp.server.fastmcp import FastMCP

# ---------------------------------------------------------------------------
# Configuration (from environment variables with sensible defaults)
# ---------------------------------------------------------------------------

LLM_URL: str = os.getenv("LLM_URL", "http://localhost:11434").rstrip("/")
LLM_MODEL: str = os.getenv("LLM_MODEL", "llama3.2")
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
])

# Whitelist of form field IDs the AI is permitted to fill
_SAFE_INPUT_IDS_DEFAULT = ",".join([
    "operationType",
    "operationQuantity",
    "operationComment",
    "searchText",
    "safetyStock",
    "reorderPoint",
    "maximumLevel",
    "searchField",
    "searchMethod",
    "inventoryStatusFilter",
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

# ---------------------------------------------------------------------------
# Prompt helpers
# ---------------------------------------------------------------------------

_prompt_cache: dict[str, str] = {}


def _load_prompt_template(lang: str) -> str:
    """Load and cache system prompt template for the given language."""
    if lang in _prompt_cache:
        return _prompt_cache[lang]

    path = PROMPTS_DIR / f"system_prompt.{lang}.txt"
    if not path.exists():
        return ""

    text = path.read_text(encoding="utf-8")
    _prompt_cache[lang] = text
    return text


def _build_system_prompt(lang: str, page_title: str, page_context: str, project_name: str) -> str:
    """Build system prompt by filling template placeholders."""
    resolved_lang = lang if lang in ("en", "de") else "ja"
    template = _load_prompt_template(resolved_lang)

    if not template:
        return ""

    no_content = {"de": "(kein Inhalt)", "en": "(no content)"}.get(resolved_lang, "（コンテンツなし）")
    context = page_context.strip() or no_content

    return (
        template
        .replace("{{projectName}}", project_name)
        .replace("{{pageTitle}}", page_title)
        .replace("{{context}}", context)
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

    for turn in history:
        role = turn.get("role", "")
        content = turn.get("content", "")
        if role in ("user", "assistant") and content:
            messages.append({"role": role, "content": content})

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

    return _sanitize_response(text)


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
    print(f"Starting CREC Web MCP Server on {MCP_HOST}:{MCP_PORT}")
    print(f"LLM backend: {LLM_URL}  model: {LLM_MODEL}")
    mcp.run(transport="streamable-http", host=MCP_HOST, port=MCP_PORT)
