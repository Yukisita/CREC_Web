# AI chat tests

Run from the repository root:

```powershell
dotnet run --project tests/Chat.Tests
node --test tests/chat-actions.test.cjs
dotnet run --project tests/Chat.Tests -- --integration
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

These tests do not measure real-model instruction understanding. Test the chosen
model's operation plans separately against a disposable project before relying
on it for regular editing.

## Adding an operation

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

For architecture and configuration, see [AI chat](../docs/ai-chat.md).
