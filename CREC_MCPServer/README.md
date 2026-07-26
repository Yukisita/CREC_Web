# CREC MCPServer

A Python MCP server for CREC Web.  
Integrates with a local LLM (Ollama, LM Studio, or any OpenAI-compatible API) to provide AI chat functionality for CREC Web.  
The CREC Web C# backend connects as an MCP client and calls the `process_chat` tool.

---

## Architecture

```
Browser (Chat UI)
    ↓ POST /api/Chat
CREC Web C# Backend (MCP Client)
    ↓ MCP tools/call  HTTP POST /mcp
CREC MCPServer (this server, Python)
    ↓ POST /v1/chat/completions
LLM Backend (Ollama / LM Studio / etc.)
```

---

## Prerequisites

- Python 3.11 or higher
- pip
- An OpenAI-compatible LLM backend (Ollama / LM Studio / etc.) running separately

---

## Setup

```bash
# 1. Navigate to the CREC_MCPServer directory from the repository root
cd CREC_MCPServer

# 2. Create a virtual environment (recommended)
python -m venv .venv
source .venv/bin/activate

# 3. Install dependencies
pip install -r requirements.txt
```

On Windows PowerShell, activate the virtual environment with:

```powershell
.\.venv\Scripts\Activate.ps1
```

To override the built-in LLM defaults, set environment variables before
starting the server.

PowerShell:

```powershell
$env:LLM_URL = "http://localhost:11434"
$env:LLM_MODEL = "llama3.2"
```

bash:

```bash
export LLM_URL="http://localhost:11434"
export LLM_MODEL="llama3.2"
```

`.env.example` is a reference template. `server.py` reads process environment
variables directly and does not load `.env` files automatically.

---

## Starting the Server

```bash
# With the virtual environment activated
python server.py
```

On startup, the following message is displayed:

```
Starting CREC Web MCP Server on 127.0.0.1:8765
LLM backend: http://localhost:1234  model: google/gemma-4-e2b
```

---

## Configuration (Environment Variables)

| Variable | Default | Description |
|----------|---------|-------------|
| `LLM_URL` | `http://localhost:1234` | Base URL of the OpenAI-compatible LLM backend (no trailing slash). The default is for LM Studio. For Ollama's OpenAI-compatible API, use `http://localhost:11434`. |
| `LLM_MODEL` | `google/gemma-4-e2b` | Model identifier passed to the OpenAI-compatible backend. Set this to a model available in your backend. |
| `MCP_HOST` | `127.0.0.1` | Bind address for the MCP server. Change to `0.0.0.0` for external access. |
| `MCP_PORT` | `8765` | Port number for the MCP server. Must match `McpServer:Url` in CREC Web's `appsettings.json`. |
| `LLM_TIMEOUT` | `120` | Timeout for LLM requests (seconds). |
| `MAX_CONTEXT_CHARS` | `3000` | Maximum number of page-context characters included in the system prompt. |
| `MAX_HISTORY_TURNS` | `10` | Maximum number of prior user/assistant turn pairs sent to the LLM. |
| `CHAT_LOG_DIR` | `logs` | Directory for the structured XML chat log, relative to the MCP server's working directory unless absolute. |
| `CHAT_LOG_RETENTION_DAYS` | `90` | Number of days to retain rotated chat logs. Use `0` for unlimited retention. |
| `CHAT_LOG_MAX_FIELD_LENGTH` | `10000` | Maximum number of characters stored per chat-log field. |
| `SAFE_BUTTON_IDS` | (see below) | Comma-separated list of button IDs the AI is allowed to click. |
| `SAFE_INPUT_IDS` | (see below) | Comma-separated list of form field IDs the AI is allowed to fill. |

Set these values in the environment of the `server.py` process. `.env` files are
not loaded automatically.

---

## Default Whitelists

### Button IDs (`SAFE_BUTTON_IDS`)

| ID | Description |
|----|-------------|
| `addNewCollectionBtn` | Create new collection |
| `editProjectBtn` | Open project settings |
| `adminPanelToggle` | Toggle admin panel |
| `searchButton` | Execute search |
| `clearFiltersButton` | Clear filters |
| `inventoryOperationBtn` | Open inventory operation modal |
| `inventoryManagementSettingsBtn` | Open inventory management settings modal |
| `inventoryOperationSave` | Save inventory operation |
| `inventoryOperationCancel` | Cancel inventory operation |
| `inventoryManagementSettingsSave` | Save inventory management settings |
| `inventoryManagementSettingsCancel` | Cancel inventory management settings |
| `editIndexBtn` | Open index edit modal |
| `projectEditSaveBtn` | Save project settings |
| `saveIndexEdit` | Save collection index changes |
| `toggleAdvancedFiltersButton` | Toggle advanced search filters |
| `gridViewBtn` | Switch to grid view |
| `tableViewBtn` | Switch to table view |

`deleteCollectionBtn` is hard-blocked and cannot be enabled through
`SAFE_BUTTON_IDS`.

### Form Field IDs (`SAFE_INPUT_IDS`)

| ID | Description | Value |
|----|-------------|-------|
| `operationType` | Inventory operation type | `0`=Receive / `1`=Ship / `2`=Count |
| `operationQuantity` | Inventory operation quantity | Number |
| `operationComment` | Inventory operation comment | Text |
| `searchText` | Search keyword | Text |
| `safetyStock` | Safety stock level | Number |
| `reorderPoint` | Reorder point | Number |
| `maximumLevel` | Maximum stock level | Number |
| `searchField` | Search target field | Text |
| `searchMethod` | Search method | Text |
| `inventoryStatusFilter` | Inventory status filter | Text |
| `editName` | Collection name | Text |
| `editManagementCode` | Collection management code | Text |
| `editRegistrationDate` | Collection registration date | Date |
| `editCategory` | Collection category | Text |
| `editFirstTag` | Collection tag 1 | Text |
| `editSecondTag` | Collection tag 2 | Text |
| `editThirdTag` | Collection tag 3 | Text |
| `editLocation` | Collection location | Text |
| `editProjectName` | Project name | Text |
| `editCollectionNameLabel` | Collection-name field label | Text |
| `editUUIDLabel` | UUID field label | Text |
| `editManagementCodeLabel` | Management-code field label | Text |
| `editCategoryLabel` | Category field label | Text |
| `editTag1Label` | Tag 1 field label | Text |
| `editTag2Label` | Tag 2 field label | Text |
| `editTag3Label` | Tag 3 field label | Text |

---

## Exposed Tools

| Tool | Description |
|------|-------------|
| `process_chat(message, history, page_context, page_title, project_name)` | Main tool. Calls the LLM to generate a chat response, validates action IDs against the whitelist, and returns the result. |
| `search_collections(keyword)` | Returns a collection search action tag. |
| `navigate(path)` | Returns a page navigation action tag. |
| `show_admin_panel()` | Returns an admin panel display action tag. |
| `click_button(button_id)` | Returns a whitelist-validated button click action tag. |
| `fill_input(field_id, value)` | Returns a whitelist-validated form input action tag. |

---

## Customizing the System Prompt

Edit `prompts/system_prompt.txt` directly to modify the prompt.  
The file is cached on startup, so restart the server after making changes.

Placeholders:

| Placeholder | Replaced With |
|-------------|---------------|
| `{{projectName}}` | CREC Web project name |
| `{{pageTitle}}` | Current page title |
| `{{context}}` | Current page content (excerpt) |

---

## Connection Settings for CREC Web

Configure the following in CREC Web's `appsettings.json`:

```json
{
  "McpServer": {
    "Url": "http://127.0.0.1:8765"
  }
}
```

> **Note**: `McpServer:Url` must match this server's `MCP_HOST:MCP_PORT`.
> LLM configuration belongs to the MCP server process and is not read from
> CREC Web's `appsettings.json`.

---

## Troubleshooting

### Cannot connect to LLM backend

- Verify `LLM_URL` is correct (no trailing slash)
- For Ollama: ensure `ollama serve` is running and the model has been pulled with `ollama pull <model>`
- For LM Studio: ensure "Start Server" has been executed

### Cannot connect from CREC Web

- Verify `MCP_PORT` matches the port in CREC Web's `McpServer:Url`
- Check that the port is allowed through the firewall
- Set `MCP_HOST=0.0.0.0` when connecting from an external host

### Actions are not executed

- Verify the button/field ID is included in `SAFE_BUTTON_IDS` / `SAFE_INPUT_IDS`
- Check server logs for `[ERROR]` messages
