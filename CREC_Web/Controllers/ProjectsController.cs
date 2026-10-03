using CREC_Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers;

/// <summary>プロジェクトの状態確認・候補一覧・切り替えを提供する。</summary>
/// <param name="runtime">世代と切り替え処理を管理するサービス</param>
/// <param name="catalog">サーバー側の候補を探索・検証するサービス</param>
/// <param name="logger">切り替え失敗の詳細を記録するロガー</param>
[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(ProjectRuntime runtime, ProjectCatalogService catalog,
    ILogger<ProjectsController> logger) : ControllerBase
{
    /// <summary>現在の世代と名前を返す。</summary>
    /// <returns>世代とプロジェクト名を含む HTTP 200 応答。実パスは公開しない。</returns>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var state = runtime.Current;
        return Ok(new { state.Revision, state.Name });
    }

    /// <summary>Projects 内の候補と、選択できない場合の理由を取得する。</summary>
    /// <returns>候補一覧と一覧全体のエラー理由を含む HTTP 200 応答</returns>
    [HttpGet]
    public IActionResult List() => Ok(catalog.List(runtime.Current.FilePath));

    /// <summary>指定した候補へ切り替える。</summary>
    /// <param name="request">候補の識別子と操作元の世代</param>
    /// <returns>成功は200、世代不一致・競合は409、候補不正は400、予期しない失敗は500の応答</returns>
    [HttpPost("switch")]
    public async Task<IActionResult> Switch([FromBody] SwitchProjectRequest request)
    {
        try
        {
            var result = await runtime.SwitchAsync(request.Id, request.Revision, HttpContext.RequestAborted);
            // 実パスは応答に含めない。
            var payload = new { code = result.Code, revision = result.State.Revision, name = result.State.Name };
            return result.Code switch
            {
                "projects-switched" or "projects-already-current" => Ok(payload),
                "projects-busy" or "projects-stale" => Conflict(payload),
                _ => BadRequest(payload)
            };
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // 切断済みの要求へエラー本文を書き込まない。受付再開は ProjectRuntime が行う。
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Project switch failed; previous project restored");
            return StatusCode(500, new { code = "projects-switch-failed" });
        }
    }
}

/// <summary>画面から送信する切り替え要求。任意のファイルパスは受け付けない。</summary>
/// <param name="Id">候補一覧で発行された識別子</param>
/// <param name="Revision">操作元の画面が保持する世代</param>
public sealed record SwitchProjectRequest(string Id, string Revision);
