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
source .venv/bin/activate       # Windows: .venv\Scripts\activate

# 3. Install dependencies
pip install -r requirements.txt

# 4. Configure environment variables
cp .env.example .env
# Open .env in a text editor and set LLM_URL / LLM_MODEL etc.
```

---

## Starting the Server

```bash
# With the virtual environment activated
python server.py
```

On startup, the following message is displayed:

```
Starting CREC Web MCP Server on 127.0.0.1:8765
LLM backend: http://localhost:11434  model: llama3.2
```

---

## Configuration (Environment Variables)

| Variable | Default | Description |
|----------|---------|-------------|
| `LLM_URL` | `http://localhost:11434` | Base URL of the LLM backend (no trailing slash). Default is for Ollama. For LM Studio use `http://localhost:1234`. |
| `LLM_MODEL` | `llama3.2` | Model name to use. For Ollama, use a model that has been pulled with `ollama pull`. |
| `MCP_HOST` | `127.0.0.1` | Bind address for the MCP server. Change to `0.0.0.0` for external access. |
| `MCP_PORT` | `8765` | Port number for the MCP server. Must match `McpServer:Url` in CREC Web's `appsettings.json`. |
| `LLM_TIMEOUT` | `120` | Timeout for LLM requests (seconds). |
| `SAFE_BUTTON_IDS` | (see below) | Comma-separated list of button IDs the AI is allowed to click. |
| `SAFE_INPUT_IDS` | (see below) | Comma-separated list of form field IDs the AI is allowed to fill. |

Environment variables can be specified in a `.env` file or set via OS shell configuration.

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

---

## Exposed Tools

| Tool | Description |
|------|-------------|
| `process_chat(message, history, page_context, page_title, lang, project_name)` | Main tool. Calls the LLM to generate a chat response, validates action IDs against the whitelist, and returns the result. |
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
  },
  "LlmBackend": {
    "Url": "http://localhost:11434",
    "Model": "llama3.2"
  }
}
```

> **Note**: `McpServer:Url` must match this server's `MCP_HOST:MCP_PORT`.  
> The `LlmBackend` settings are not required on the CREC Web side but are kept for reference (LLM configuration is managed via this server's environment variables).

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
