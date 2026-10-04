using System.Net;
using Microsoft.AspNetCore.Cors;
using ModelContextProtocol.AspNetCore;

namespace CREC_Web.Services.AiTools;

/// <summary>MCPの通信は公式SDKに任せ、ローカル接続だけを受け付ける。</summary>
public static class CrecMcpHosting
{
    public static IServiceCollection AddCrecMcp(this IServiceCollection services)
    {
        services.AddSingleton<CrecReadService>();
        services.AddSingleton<CollectionFileReader>();
        services.AddSingleton<CrecMcpTools>();
        services.AddSingleton<CrecToolCatalog>();
        services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<CrecMcpTools>(CrecToolCatalog.JsonOptions);
        return services;
    }

    // UseRoutingの後、ProjectRequestMiddlewareとCORSの前に登録する。
    public static void MapCrecMcp(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<CrecMcpEndpoint>() is not null)
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!IsLocalRequest(context))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { code = "mcp-local-only" });
                    return;
                }
            }
            await next(context);
        });
        app.MapMcp("/mcp").WithMetadata(new CrecMcpEndpoint(), new DisableCorsAttribute());
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            return false;

        // 接続元だけでなくHostも確認し、外部ドメイン経由のローカルアクセスを拒否する。
        var host = context.Request.Host.Host;
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !(IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)))
            return false;

        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length == 0 || origin.Equals($"{context.Request.Scheme}://{context.Request.Host}",
            StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>MCPのPOSTは業務の変更要求ではない。各ツールが共有サービスで世代を検証する。</summary>
public sealed class CrecMcpEndpoint;
