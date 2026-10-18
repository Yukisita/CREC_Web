using System.Net;
using System.Net.Sockets;
using System.Text;
using CREC_Web.Controllers;
using CREC_Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"upload-test-{Guid.NewGuid():N}"));
Directory.CreateDirectory(Path.Combine(testRoot, "collection"));
try
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.Configuration["ProjectDataPath"] = testRoot;
    builder.Services.AddSingleton<DataFileManagerService>();
    builder.Services.AddSingleton<CrecDataService>();
    builder.Services.AddControllers(options => options.Filters.Add(new TestBodyLimitFilter()))
        .AddApplicationPart(typeof(CollectionDataFilesController).Assembly);
    await using var app = builder.Build();
    app.MapControllers();
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    const string uploadUrl = "/api/collections/collection/data/files";
    var service = app.Services.GetRequiredService<DataFileManagerService>();

    // 論理サイズだけを持つファイルで境界値を検証し、数GBのディスク使用を避ける。
    foreach (var size in new[]
    {
        (long)(4.8 * 1024 * 1024 * 1024),
        8L * 1024 * 1024 * 1024 + 1,
        16L * 1024 * 1024 * 1024 - 1,
        16L * 1024 * 1024 * 1024
    })
    {
        var file = new ProbeFormFile(size);
        try
        {
            await service.UploadFileAsync("collection", null, file, default);
            throw new Exception("Expected the copy probe to run.");
        }
        catch (CopyProbeException) { }
        Check(file.CopyStarted, $"A {size}-byte file reaches the copy operation");
        Check(!File.Exists(Path.Combine(testRoot, "collection", "data", file.FileName)),
            "An interrupted copy removes the incomplete file");
    }

    var oversizedFile = new ProbeFormFile(16L * 1024 * 1024 * 1024 + 1);
    try
    {
        await service.UploadFileAsync("collection", null, oversizedFile, default);
        throw new Exception("Expected an upload size error.");
    }
    catch (DataFileManagerException exception)
    {
        Check(exception.StatusCode == 413 && exception.ErrorCode == "upload_size_limit_exceeded",
            "A file one byte over 16 GB is rejected with the size error");
    }
    Check(!oversizedFile.CopyStarted, "Oversized files are rejected before copying");

    var payload = Encoding.UTF8.GetBytes("ISO upload regression test");
    using (var form = CreateForm(payload, "kali-linux-2026.2-installer-amd64.iso"))
    using (var response = await client.PostAsync(uploadUrl, form))
    {
        Check(response.StatusCode == HttpStatusCode.Created, "A valid ISO filename uploads successfully");
        var saved = await File.ReadAllBytesAsync(Path.Combine(testRoot, "collection", "data",
            "kali-linux-2026.2-installer-amd64.iso"));
        Check(saved.SequenceEqual(payload), "Uploaded contents are saved intact");
    }
    using (var form = CreateForm(payload, "kali-linux-2026.2-installer-amd64.iso"))
    using (var response = await client.PostAsync(uploadUrl, form))
        Check(response.StatusCode == HttpStatusCode.Conflict, "Existing files are not overwritten");

    using (var form = new MultipartFormDataContent())
    using (var response = await client.PostAsync(uploadUrl, form))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "A missing file remains a bad request");
    using (var form = CreateForm([], "empty.iso"))
    using (var response = await client.PostAsync(uploadUrl, form))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "An empty file remains a bad request");
    using (var form = CreateForm(payload, "invalid?.iso"))
    using (var response = await client.PostAsync(uploadUrl, form))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "An invalid filename remains a bad request");

    var largeLength = 16L * 1024 * 1024 * 1024 + 2 * 1024 * 1024;
    await CheckRawRequestAsync($"Content-Length: {largeLength}\r\n", "", 413,
        "An oversized Content-Length is rejected without sending the file");

    // テスト専用フィルターで受信上限を下げ、長さ未指定の超過も実際のKestrelで検証する。
    var multipart = "--test\r\nContent-Disposition: form-data; name=\"file\"; filename=\"chunked.iso\"\r\n\r\n"
        + new string('x', 8192) + "\r\n--test--\r\n";
    var chunked = $"{Encoding.UTF8.GetByteCount(multipart):X}\r\n{multipart}\r\n0\r\n\r\n";
    await CheckRawRequestAsync("Transfer-Encoding: chunked\r\nX-Test-Body-Limit: 4096\r\n", chunked, 413,
        "A chunked upload that exceeds the receiving limit returns 413");
    Check(!File.Exists(Path.Combine(testRoot, "collection", "data", "chunked.iso")),
        "A rejected chunked upload leaves no destination file");
    await CheckRawRequestAsync("Content-Length: 6\r\n", "broken", 400,
        "A malformed multipart upload remains a bad request");

    await app.StopAsync();
    Console.WriteLine("All data upload checks passed.");

    async Task CheckRawRequestAsync(string headers, string body, int expectedStatus, string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var connection = new TcpClient();
        await connection.ConnectAsync(client.BaseAddress!.Host, client.BaseAddress.Port, timeout.Token);
        await using var stream = connection.GetStream();
        var request = $"POST {uploadUrl} HTTP/1.1\r\nHost: {client.BaseAddress.Authority}\r\n"
            + "Connection: close\r\nContent-Type: multipart/form-data; boundary=test\r\n"
            + headers + "\r\n" + body;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token);
        using var reader = new StreamReader(stream);
        var statusLine = await reader.ReadLineAsync(timeout.Token);
        Check(statusLine?.StartsWith($"HTTP/1.1 {expectedStatus} ") == true,
            $"{description} (received {statusLine})");
    }
}
finally
{
    if (Path.GetDirectoryName(testRoot) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)))
        throw new InvalidOperationException("The test directory is outside the expected output directory.");
    Directory.Delete(testRoot, recursive: true);
}

static MultipartFormDataContent CreateForm(byte[] contents, string filename)
{
    var form = new MultipartFormDataContent();
    form.Add(new ByteArrayContent(contents), "file", filename);
    return form;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception($"FAIL: {message}");
    Console.WriteLine($"PASS: {message}");
}

sealed class CopyProbeException : Exception { }

sealed class ProbeFormFile(long length) : IFormFile
{
    public bool CopyStarted { get; private set; }
    public long Length => length;
    public string FileName => "kali-linux-2026.2-installer-amd64.iso";
    public string Name => "file";
    public string ContentType => "application/octet-stream";
    public string ContentDisposition => "form-data";
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream OpenReadStream() => throw new NotSupportedException();
    public void CopyTo(Stream target) => throw new NotSupportedException();
    public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
    {
        CopyStarted = true;
        throw new CopyProbeException();
    }
}

sealed class TestBodyLimitFilter : IResourceFilter, IOrderedFilter
{
    public int Order => int.MaxValue;
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (long.TryParse(context.HttpContext.Request.Headers["X-Test-Body-Limit"], out var limit))
            context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = limit;
    }
    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
