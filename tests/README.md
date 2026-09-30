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
action parsing, operation ordering, navigation continuation and cancellation.
Python tests cover action validation, conversation history and chat orchestration.
None of these tests call a live LLM or modify collection data.

Source responsibilities:

- `CREC_Web/Controllers/ChatController.cs`: HTTP input and error mapping.
- `CREC_Web/Services/Chat/`: MCP session lifecycle, transport and response parsing.
- `CREC_Web/wwwroot/js/chat.js`: conversation state and chat UI.
- `CREC_Web/wwwroot/js/chat-context.js`: page context sent to the model.
- `CREC_Web/wwwroot/js/chat-actions.js`: browser actions and navigation continuation.
