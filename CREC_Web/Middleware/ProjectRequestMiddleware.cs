using CREC_Web.Services;

namespace CREC_Web.Middleware;

public sealed class ProjectRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ProjectRuntime runtime)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        var management = context.Request.Path.StartsWithSegments("/api/projects", StringComparison.OrdinalIgnoreCase);
        var status = path.Equals("/api/projects/status", StringComparison.OrdinalIgnoreCase);
        var switching = path.Equals("/api/projects/switch", StringComparison.OrdinalIgnoreCase);
        var mutation = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method);

        context.Response.Headers.CacheControl = "no-store";
        if (management && !status)
        {
            // Runs BEFORE CORS. A custom header plus origin checks prevents form/CSRF requests;
            // this is separate from authentication, which will be implemented in another issue.
            if (!IsSameOrigin(context.Request)
                || (mutation && context.Request.Headers["X-CREC-Request"] != "1"))
            {
                await Error(context, 403, "projects-origin-denied");
                return;
            }
        }
        if (status || switching)
        {
            await next(context);
            return;
        }
        var revision = context.Request.Headers["X-CREC-Project"].ToString();
        if (revision.Length == 0) revision = context.Request.Query["projectRevision"].ToString();
        using var lease = runtime.TryEnter(revision, mutation, out var error);
        if (lease is null)
        {
            await Error(context, error == "projects-busy" ? 503 : 409, error!);
            return;
        }
        context.Response.Headers["X-CREC-Project"] = runtime.Current.Revision;
        // The lease covers MVC result execution and file streaming, not just the action method.
        await next(context);
    }

    private static bool IsSameOrigin(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin" && site != "none") return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var source)
            && Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var target)
            && source.GetLeftPart(UriPartial.Authority).Equals(target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
    }

    private static async Task Error(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        if (status == 503) context.Response.Headers.RetryAfter = "1";
        await context.Response.WriteAsJsonAsync(new { code });
    }
}
