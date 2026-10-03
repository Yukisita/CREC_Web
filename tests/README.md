# AI chat regression tests

Run from the repository root:

```powershell
dotnet run --project tests/Chat.Tests
node --test tests/chat-actions.test.cjs
Push-Location CREC_MCPServer
python -m unittest discover -s tests -v
Pop-Location
```

The C# console test project uses the application's existing dependencies and a
fake HTTP handler. It covers initialization, concurrent calls, expired sessions,
JSON/SSE responses, cancellation, timeouts and API error mapping. The transport
follows [MCP Streamable HTTP (2025-03-26)](https://modelcontextprotocol.io/specification/2025-03-26/basic/transports).

The JavaScript tests use Node's built-in test runner and browser stubs. They cover
plan validation, operation ordering, navigation continuation and cancellation.
Python tests cover action validation, conversation history and chat orchestration.
None of these tests call a live LLM or modify collection data.

The integration test starts the real Python MCP application, a temporary ASP.NET
Web API host with the production chat controller/client, and a deterministic
local HTTP endpoint in place of an LLM. It verifies the entire request path and
cleans up its listeners and temporary audit logs:

```powershell
python -m venv CREC_MCPServer/.venv
CREC_MCPServer/.venv/Scripts/python.exe -m pip install -r CREC_MCPServer/requirements.txt
dotnet build tests/Chat.Tests
CREC_MCPServer/.venv/Scripts/python.exe tests/chat_integration.py
```

On macOS/Linux use `CREC_MCPServer/.venv/bin/python` for the Python commands.
These deterministic tests do not measure a real model's ability to follow the prompt.

Source responsibilities:

- `CREC_Web/Controllers/ChatController.cs`: HTTP input and error mapping.
- `CREC_Web/Services/Chat/`: MCP session lifecycle, transport and response parsing.
- `CREC_Web/wwwroot/js/chat.js`: conversation state and chat UI.
- `CREC_Web/wwwroot/js/chat-context.js`: page context sent to the model.
- `CREC_Web/wwwroot/js/chat-actions.js`: browser actions and navigation continuation.
- `CREC_Web/Views/Shared/_Chat.cshtml` and `wwwroot/css/chat.css`: chat markup and styles.

## Adding an operation

1. Add its payload fields and validation to Python `ACTION_FIELDS` and
   `ActionPolicy.is_action_allowed`, and the corresponding browser
   `CHAT_ACTION_FIELDS` and `executeChatAction` handler.
2. Add valid/invalid examples to `tests/chat-action-contract.json`. Both language
   test suites consume this file to keep the supported operations aligned.
3. For an asynchronous button operation, expose `button.chatAction` returning a
   promise of `true` on success or `false` on validation/server failure. Reuse the
   page's existing save handler. The chat executor awaits it and stops on failure.
4. Document the operation in the prompt and operation reference, and test a
   multi-step sequence where the new operation fails before a dependent save.

Validation is all-or-nothing; execution is not a transaction. Successful earlier
operations are not rolled back if a later operation fails. Results are appended
to conversation history. Pending navigation plans are destination-bound, expire
after five minutes, and are consumed once. The v2 storage keys intentionally do
not replay actions from the old tag-based browser implementation.
