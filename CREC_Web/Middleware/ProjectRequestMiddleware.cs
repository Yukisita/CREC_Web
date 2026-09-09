using CREC_Web.Services;

namespace CREC_Web.Middleware;

/// <summary>プロジェクトの世代と切り替え状態を確認し、要求全体を受付ハンドルで保護する。</summary>
/// <param name="next">検証を通過した要求を渡す次のミドルウェア。</param>
public sealed class ProjectRequestMiddleware(RequestDelegate next)
{
    /// <summary>外部サイトからの管理操作と古い世代の要求を拒否し、受付可能な要求を処理する。</summary>
    /// <param name="context">処理対象の HTTP 要求と応答。</param>
    /// <param name="runtime">現在の世代と切り替え状態を管理するサービス。</param>
    /// <returns>後続の処理と、ファイル送信を含む応答の完了を待つタスク。</returns>
    public async Task InvokeAsync(HttpContext context, ProjectRuntime runtime)
    {
        var request = context.Request;
        var normalizedPath = request.Path.Value?.TrimEnd('/') ?? "";
        var isManagementRequest = request.Path.StartsWithSegments("/api/projects", StringComparison.OrdinalIgnoreCase);
        var isStatusRequest = normalizedPath.Equals("/api/projects/status", StringComparison.OrdinalIgnoreCase);
        var isSwitchRequest = normalizedPath.Equals("/api/projects/switch", StringComparison.OrdinalIgnoreCase);
        var isMutation = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method);

        // 同じ URL を使う別プロジェクトの応答が、ブラウザキャッシュから混在するのを防ぐ。
        context.Response.Headers.CacheControl = "no-store";
        if (isManagementRequest && !isStatusRequest && !IsAllowedManagementRequest(request, isMutation))
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

    /// <summary>管理 API の要求元と、更新要求に必要なカスタムヘッダーを確認する。</summary>
    /// <param name="request">一覧取得または切り替えの要求。</param>
    /// <param name="isMutation">データ更新を伴う HTTP メソッドの場合は true。</param>
    /// <returns>同一オリジンの条件と必要なヘッダーを満たす場合は true。</returns>
    private static bool IsAllowedManagementRequest(HttpRequest request, bool isMutation)
    {
        // CORS より前に実行する CSRF 対策。利用者の認証・権限制御は別 Issue で扱う。
        return IsSameOrigin(request) && (!isMutation || request.Headers["X-CREC-Request"] == "1");
    }

    /// <summary>ブラウザが送るオリジン情報に、外部サイトからのアクセスを示す値がないか確認する。</summary>
    /// <param name="request">要求元のヘッダーと、接続先のスキーム・ホストを含む要求。</param>
    /// <returns>オリジン情報が未指定、または同じスキーム・ホスト・ポートの場合は true。</returns>
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

    /// <summary>要求を拒否した理由を、フロントエンドが翻訳できる形式で返す。</summary>
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
