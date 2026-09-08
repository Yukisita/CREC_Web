using CREC_Web.Middleware;
using CREC_Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

internal static class RequestTests
{
    public static async Task Run(ProjectRuntime runtime)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        async Task Check(string path, string method, int expected, Action<HttpContext>? configure = null)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Path = path;
            context.Request.Method = method;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost", 5000);
            context.Response.Body = new MemoryStream();
            configure?.Invoke(context);
            var called = false;
            await new ProjectRequestMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, runtime);
            if (context.Response.StatusCode != expected || called != (expected == 200))
                throw new Exception($"Unexpected request result: {method} {path}: {context.Response.StatusCode}, expected {expected}");
            Console.WriteLine($"PASS: {method} {path}: {expected}");
        }
        await Check("/api/projects", "GET", 200);
        await Check("/api/projects/status", "GET", 200);
        await Check("/api/projects/switch", "POST", 403);
        await Check("/api/projects/switch", "POST", 200, c => {
            c.Request.Headers["X-CREC-Request"] = "1";
        });
        foreach (var origin in new[] { "https://evil.example", "http://localhost:9999", "null" })
            await Check("/api/projects/switch", "POST", 403, c => {
                c.Request.Headers["X-CREC-Request"] = "1";
                c.Request.Headers.Origin = origin;
            });
        await Check("/api/projects", "OPTIONS", 403, c => c.Request.Headers.Origin = "https://evil.example");
        await Check("/api/projects/switch", "POST", 403, c => {
            c.Request.Headers["X-CREC-Request"] = "1";
            c.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        });
        await Check("/api/projects/switch", "POST", 200, c => {
            c.Request.Headers["X-CREC-Request"] = "1";
            c.Request.Headers.Origin = "http://localhost:5000";
        });
        await Check("/api/CollectionIndex/same-id", "PUT", 409);
        await Check("/api/CollectionIndex/same-id", "PUT", 409, c => c.Request.Headers["X-CREC-Project"] = "old");
        await Check("/api/CollectionIndex/same-id", "PUT", 200, c => c.Request.Headers["X-CREC-Project"] = runtime.Current.Revision);
        await Check("/api/File/same-id/video/a.mp4", "GET", 409, c => c.Request.QueryString = new("?projectRevision=old"));
        await Check("/api/File/same-id/video/a.mp4", "GET", 200, c => c.Request.QueryString = new("?projectRevision=" + runtime.Current.Revision));
    }
}
