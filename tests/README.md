# AI chat and tool tests

Run from the repository root:

```powershell
dotnet run --project tests/Chat.Tests
node --test tests/chat-actions.test.cjs tests/webmcp.test.cjs
dotnet run --project tests/Chat.Tests -- --integration
dotnet run --project tests/Chat.Tests -- --ai-tools
```

The C# console tests use the application's existing dependencies and a fake HTTP
handler. They cover direct LLM requests, structured output, history normalization,
plan validation, complete-response checks, concurrency, cancellation, deadlines
and API error mapping. `ChatActionPolicy` and the browser both consume the
examples in `tests/chat-action-contract.json`.

The Node tests use the built-in test runner and browser stubs. They cover plan
validation, awaited saves, ordering, navigation continuation, cancellation and
project revision changes. Project settings save tests execute the actual page
script to verify that defaults refresh before the next operation.

The integration test starts the production controller, chat service and project
middleware in a temporary Web API host. A second local HTTP listener stands in
for the LLM. It checks valid/rejected plans, Unicode values, project selection and
switching, and stale requests. It uses temporary project files and closes both
listeners and removes those files on completion. It needs neither Python nor an
installed model and never modifies real collection data.

The `--ai-tools` integration test uses the official MCP client against the real
MCP endpoint. It checks all nine tools and compares their schemas and results to
the WebMCP API. Coverage includes advanced search, project settings and candidates,
inventory precision and history pagination, all attachment areas, UTF-8 chunks,
binary content, changed files, path/link isolation, stale requests and Host/Origin
restrictions. It checks that reads do not change any fixture files or directories.
The WebMCP Node tests execute the page script with browser API stubs and check
catalog discovery, revision binding, cancellation, and back/forward cache restore.
These tests use disposable data and do not contact an AI provider.

These tests do not measure real-model instruction understanding. Test the chosen
model's operation plans separately against a disposable project before relying
on it for regular editing.

## Adding an app chat operation

1. Update `ChatActionPolicy` and its allowed IDs. The same operation definitions
   generate the JSON Schema sent to the model. Update the browser's
   `CHAT_ACTION_FIELDS`, validation and `executeChatAction` handler.
2. Add valid/invalid examples to `tests/chat-action-contract.json`.
3. Expose an asynchronous UI operation as `button.chatAction`, returning `true`
   on success or `false` on validation/server failure. Reuse the existing handler
   so the executor waits for the actual operation and stops on failure.
4. Update `ChatSystem.txt` and the operation reference, and verify a failed
   operation stops a dependent save.

Plans are validated in full; execution is not a transaction. Successful earlier
operations are not rolled back. History and pending plans are bound to the
project revision. Pending plans also match the destination, expire after five
minutes and are consumed once. A stale notification cancels the request and
removes the old conversation/plan. The current storage format discards older
history without a project revision.

For architecture and configuration, see [AI chat](../docs/ai-chat.md) and
[MCP / WebMCP](../docs/ai-tools.md).

For a new external read tool, add its business operation to `CrecReadService` and
its public definition to `CrecMcpTools`. WebMCP consumes the same catalog without
a second JavaScript definition. Extend the integration test with real fixture data
and verify the MCP and Web results agree and leave the data unchanged.
