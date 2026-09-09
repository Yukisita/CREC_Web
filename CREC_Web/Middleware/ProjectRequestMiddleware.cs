using CREC_Web.Services;

namespace CREC_Web.Middleware;

/// <summary>世代を確認し、要求の処理中は切り替えを待機させる。</summary>
/// <param name="next">検証を通過した要求を渡す次のミドルウェア。</param>
public sealed class ProjectRequestMiddleware(RequestDelegate next)
{
    /// <summary>要求元と世代を検証し、要求を処理する。</summary>
    /// <param name="context">処理対象の HTTP 要求と応答。</param>
    /// <param name="runtime">現在の世代と切り替え状態を管理するサービス。</param>
    /// <returns>ファイル送信を含む応答の完了を待つタスク。</returns>
    public async Task InvokeAsync(HttpContext context, ProjectRuntime runtime)
    {
        var request = context.Request;
        var normalizedPath = request.Path.Value?.TrimEnd('/') ?? "";
        var isManagementRequest = request.Path.StartsWithSegments("/api/projects", StringComparison.OrdinalIgnoreCase);
        var isStatusRequest = normalizedPath.Equals("/api/projects/status", StringComparison.OrdinalIgnoreCase);
        var isSwitchRequest = normalizedPath.Equals("/api/projects/switch", StringComparison.OrdinalIgnoreCase);
        var isMutation = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method);
        var hasRequiredHeader = !isMutation || request.Headers["X-CREC-Request"] == "1";

        // 別プロジェクトの応答がキャッシュから混ざるのを防ぐ。
        context.Response.Headers.CacheControl = "no-store";
        // 同一オリジンとカスタムヘッダーで、外部サイトからの管理操作を拒否する。
        if (isManagementRequest && !isStatusRequest && (!IsSameOrigin(request) || !hasRequiredHeader))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "projects-origin-denied");
            return;
        }

        // 状態確認は切り替え待ち中も可能にする。切り替え要求自身は完了待ちの対象に含めない。
        if (isStatusRequest || isSwitchRequest)
        {
            await next(context);
            return;
        }

        var revision = request.Headers["X-CREC-Project"].ToString();
        if (revision.Length == 0)
        {
            // 動画やダウンロードではカスタムヘッダーを付けられないため、URL の世代を使う。
            revision = request.Query["projectRevision"].ToString();
        }

        using var requestLease = runtime.TryEnter(revision, isMutation, out var error);
        if (requestLease is null)
        {
            var statusCode = error == "projects-busy" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
            await WriteErrorAsync(context, statusCode, error!);
            return;
        }

        context.Response.Headers["X-CREC-Project"] = runtime.Current.Revision;
        // MVC アクションの終了後に行われるファイル送信まで、この受付ハンドルを保持する。
        await next(context);
    }

    /// <summary>外部サイトからの要求を示すヘッダーがないか確認する。</summary>
    /// <param name="request">要求元と接続先を含む HTTP 要求。</param>
    /// <returns>要求元が未指定、または同一オリジンなら true。</returns>
    private static bool IsSameOrigin(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && fetchSite != "same-origin" && fetchSite != "none")
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var sourceUri)
            || !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var targetUri))
        {
            return false;
        }

        return sourceUri.GetLeftPart(UriPartial.Authority)
            .Equals(targetUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>拒否理由を JSON で返す。</summary>
    /// <param name="context">エラーを書き込む HTTP 応答を含むコンテキスト。</param>
    /// <param name="statusCode">拒否理由に対応する HTTP ステータスコード。</param>
    /// <param name="code">画面に表示する翻訳キー。</param>
    /// <returns>JSON 応答の書き込み完了を待つタスク。</returns>
    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code)
    {
        context.Response.StatusCode = statusCode;
        if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.RetryAfter = "1";
        }

        await context.Response.WriteAsJsonAsync(new { code });
    }
}
