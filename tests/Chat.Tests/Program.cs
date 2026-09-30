using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CREC_Web.Controllers;
using CREC_Web.Models;
using CREC_Web.Services.Chat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new (string Name, Func<Task> Run)[]
{
    ("JSON tool calls preserve arguments and reuse the initialized session", JsonAndSession),
    ("Stateless servers initialize once", StatelessSession),
    ("SSE handles multiline data, ignores notifications, and finishes before EOF", SseResponse),
    ("Concurrent calls await initialization acknowledgement", ConcurrentInitialization),
    ("Expired sessions are reinitialized and retried once", ExpiredSession),
    ("Late failures cannot invalidate a newer session", LateExpiredSession),
    ("Failed acknowledgement is not cached", FailedAcknowledgement),
    ("Tool failures are errors and do not discard a working session", ToolError),
    ("Invalid JSON, RPC errors and mismatched responses are rejected", InvalidResponses),
    ("Empty text remains an empty response", EmptyResponse),
    ("Cancellation interrupts response body reads", CancelBodyRead),
    ("Deadline includes response body reads", TimeoutBodyRead),
    ("Controller maps upstream errors and propagates caller cancellation", ControllerErrors)
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static async Task JsonAndSession()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    server.Tool = (body, _, _) =>
    {
        var arguments = body.GetProperty("params").GetProperty("arguments");
        Equal("保存", arguments.GetProperty("message").GetString());
        Equal("user", arguments.GetProperty("history")[0].GetProperty("role").GetString());
        Equal("CREC Web", arguments.GetProperty("page_title").GetString());
        Equal("", arguments.GetProperty("page_context").GetString());
        return Task.FromResult(FakeMcpServer.Result(body, new
        {
            content = new[] { new { type = "text", text = "Hello " }, new { type = "text", text = "world" } }
        }));
    };
    var request = new ChatRequest { Message = "保存", History = [new() { Role = "user", Content = "before" }] };
    Equal("Hello world", await client.ProcessChatAsync(request, default));
    Equal("Hello world", await client.ProcessChatAsync(request, default));
    Equal(1, server.Initializations);
    Equal(1, server.Notifications);
    Equal(2, server.ToolCalls);
    Equal(3, server.RequestIds.Distinct().Count());
}

static async Task StatelessSession()
{
    using var server = new FakeMcpServer { Stateful = false };
    using var client = server.CreateClient();
    await client.ProcessChatAsync(new() { Message = "one" }, default);
    await client.ProcessChatAsync(new() { Message = "two" }, default);
    Equal(1, server.Initializations);
}

static async Task SseResponse()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    server.Tool = (body, _, _) =>
    {
        var id = body.GetProperty("id").GetInt64();
        var stream = new PrefixThenWaitStream(
            ": keepalive\n\ndata:{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\n" +
            "data:{\"jsonrpc\":\"2.0\",\"id\":-1,\"result\":{}}\n\n" +
            $"event: message\r\ndata:{{\"jsonrpc\":\"2.0\",\"id\":{id},\r\n" +
            "data: \"result\":{\"content\":[{\"type\":\"text\",\"text\":\"SSE answer\"}]}}\r\n\r\n");
        return Task.FromResult(Sse(stream));
    };
    Equal("SSE answer", await client.ProcessChatAsync(new() { Message = "one" }, default));
}

static async Task ConcurrentInitialization()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    server.Acknowledge = async token => { arrived.SetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.Accepted); };
    var first = client.ProcessChatAsync(new() { Message = "first" }, default);
    await arrived.Task;
    var second = client.ProcessChatAsync(new() { Message = "second" }, default);
    Equal(0, server.ToolCalls);
    release.SetResult();
    await Task.WhenAll(first, second);
    Equal(1, server.Initializations);
    Equal(2, server.ToolCalls);
}

static async Task ExpiredSession()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    server.Tool = (body, session, _) => Task.FromResult(session == "session-1"
        ? new HttpResponseMessage(HttpStatusCode.NotFound) : FakeMcpServer.Text(body, "recovered"));
    Equal("recovered", await client.ProcessChatAsync(new() { Message = "one" }, default));
    Equal(2, server.Initializations);
    Equal(2, server.ToolCalls);

    server.Tool = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    await Throws<HttpRequestException>(() => client.ProcessChatAsync(new() { Message = "two" }, default));
    Equal(4, server.ToolCalls); // bounded to two attempts
}

static async Task LateExpiredSession()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    server.Tool = async (body, session, token) =>
    {
        var message = body.GetProperty("params").GetProperty("arguments").GetProperty("message").GetString();
        if (session == "session-1")
        {
            if (message == "slow") { arrived.SetResult(); await release.Task.WaitAsync(token); }
            return new(HttpStatusCode.NotFound);
        }
        return FakeMcpServer.Text(body, "recovered");
    };
    var slow = client.ProcessChatAsync(new() { Message = "slow" }, default);
    await arrived.Task;
    await client.ProcessChatAsync(new() { Message = "fast" }, default);
    release.SetResult();
    Equal("recovered", await slow);
    Equal(2, server.Initializations);
}

static async Task FailedAcknowledgement()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    server.Acknowledge = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    await Throws<HttpRequestException>(() => client.ProcessChatAsync(new() { Message = "first" }, default));
    Equal(0, server.ToolCalls);
    server.Acknowledge = null;
    await client.ProcessChatAsync(new() { Message = "second" }, default);
    Equal(2, server.Initializations);
}

static async Task ToolError()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    server.Tool = (body, _, _) => Task.FromResult(FakeMcpServer.Result(body, new { isError = true, content = new[] { new { type = "text", text = "failure" } } }));
    await Throws<McpException>(() => client.ProcessChatAsync(new() { Message = "first" }, default));
    server.Tool = null;
    Equal("ok", await client.ProcessChatAsync(new() { Message = "second" }, default));
    Equal(1, server.Initializations);
}

static async Task InvalidResponses()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    foreach (var json in new[] { "invalid", "[]", "{\"id\":-1,\"result\":{}}", "{\"id\":ID,\"error\":{\"message\":\"failed\"}}", "{\"id\":ID,\"result\":{\"content\":[42]}}" })
    {
        server.Tool = (body, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json.Replace("ID", body.GetProperty("id").GetRawText()), Encoding.UTF8, "application/json")
        });
        await Throws<McpException>(() => client.ProcessChatAsync(new() { Message = "test" }, default));
    }
    Equal(1, server.Initializations);
}

static async Task EmptyResponse()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    server.Tool = (body, _, _) => Task.FromResult(FakeMcpServer.Text(body, ""));
    Equal<string?>(null, await client.ProcessChatAsync(new() { Message = "test" }, default));
}

static async Task CancelBodyRead()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient();
    using var cancellation = new CancellationTokenSource();
    var stream = new PrefixThenWaitStream(": waiting\n\n");
    server.Tool = (_, _, _) => Task.FromResult(Sse(stream));
    var request = client.ProcessChatAsync(new() { Message = "test" }, cancellation.Token);
    await stream.Waiting.Task;
    cancellation.Cancel();
    await Throws<OperationCanceledException>(() => request);
    Equal(1, server.ToolCalls);
}

static async Task TimeoutBodyRead()
{
    using var server = new FakeMcpServer();
    using var client = server.CreateClient(timeoutSeconds: 1);
    server.Tool = (_, _, _) => Task.FromResult(Sse(new PrefixThenWaitStream("")));
    await Throws<OperationCanceledException>(() => client.ProcessChatAsync(new() { Message = "test" }, default));
    Equal(1, server.ToolCalls);
}

static async Task ControllerErrors()
{
    foreach (var (exception, status) in new (Exception, int)[]
    {
        (new HttpRequestException("private endpoint"), 503),
        (new McpException("private upstream response"), 502),
        (new OperationCanceledException(), 504)
    })
    {
        var controller = new ChatController(new FailingClient(exception), NullLogger<ChatController>.Instance);
        var result = (ObjectResult)await controller.Chat(new() { Message = "test" }, default);
        Equal(status, result.StatusCode);
        if (JsonSerializer.Serialize(result.Value).Contains("private")) throw new Exception("Upstream detail leaked.");
    }
    var canceled = new ChatController(new FailingClient(new OperationCanceledException()), NullLogger<ChatController>.Instance);
    await Throws<OperationCanceledException>(() => canceled.Chat(new() { Message = "test" }, new CancellationToken(true)));
    Equal(400, ((ObjectResult)await canceled.Chat(new() { Message = " " }, default)).StatusCode);
}

static HttpResponseMessage Sse(Stream stream)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
    return response;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class FailingClient(Exception exception) : IMcpChatClient
{
    public Task<string?> ProcessChatAsync(ChatRequest request, CancellationToken token) => Task.FromException<string?>(exception);
}

sealed class FakeMcpServer : HttpMessageHandler, IHttpClientFactory
{
    public int Initializations;
    public int Notifications;
    public int ToolCalls;
    public bool Stateful = true;
    public System.Collections.Concurrent.ConcurrentBag<long> RequestIds { get; } = [];
    public Func<JsonElement, string?, CancellationToken, Task<HttpResponseMessage>>? Tool;
    public Func<CancellationToken, Task<HttpResponseMessage>>? Acknowledge;

    public McpChatClient CreateClient(int timeoutSeconds = 3) => new(this, Options.Create(new McpClientOptions { TimeoutSeconds = timeoutSeconds }));
    HttpClient IHttpClientFactory.CreateClient(string name) => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Headers.Accept.Count != 2) throw new Exception("Missing MCP Accept headers.");
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var body = document.RootElement;
        if (body.TryGetProperty("id", out var id)) RequestIds.Add(id.GetInt64());
        request.Headers.TryGetValues("Mcp-Session-Id", out var ids);
        var session = ids?.Single();
        switch (body.GetProperty("method").GetString())
        {
            case "initialize":
                if (session != null) throw new Exception("Initialization must omit session ID.");
                var count = Interlocked.Increment(ref Initializations);
                var response = Result(body, new { protocolVersion = "2025-03-26", capabilities = new { }, serverInfo = new { name = "test", version = "1" } });
                if (Stateful) response.Headers.Add("Mcp-Session-Id", $"session-{count}");
                return response;
            case "notifications/initialized":
                if (body.TryGetProperty("id", out _)) throw new Exception("Notification must omit ID.");
                Interlocked.Increment(ref Notifications);
                return Acknowledge == null ? new(HttpStatusCode.Accepted) : await Acknowledge(token);
            case "tools/call":
                if (Stateful && session == null) throw new Exception("Missing session ID.");
                Interlocked.Increment(ref ToolCalls);
                return Tool == null ? Text(body, "ok") : await Tool(body, session, token);
            default: throw new Exception("Unexpected RPC method.");
        }
    }

    public static HttpResponseMessage Text(JsonElement body, string text) => Result(body, new { content = new[] { new { type = "text", text } }, isError = false });
    public static HttpResponseMessage Result(JsonElement body, object result) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = body.GetProperty("id").GetInt64(), result }), Encoding.UTF8, "application/json")
    };
}

sealed class PrefixThenWaitStream(string prefix) : Stream
{
    private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = _prefix.Read(buffer.Span);
        if (count > 0) return count;
        Waiting.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _prefix.Dispose(); base.Dispose(disposing); }
}
