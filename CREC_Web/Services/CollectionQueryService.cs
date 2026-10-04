using System.Globalization;
using System.Text.Json.Serialization;
using CREC_Web.Models;

namespace CREC_Web.Services;

/// <summary>Web画面とAI連携で共有する、プロジェクトを固定した読み取り処理。</summary>
public sealed class CollectionQueryService(ProjectRuntime runtime, CrecDataService data)
{
    public ProjectInfo GetProject()
    {
        var state = runtime.Current;
        return new(state.Revision, state.Name, state.HasProject);
    }

    public async Task<CollectionQueryResult> SearchAsync(string projectRevision, string? query,
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (query?.Length > 256 || page is < 1 or > 1_000_000 || pageSize is < 1 or > 50)
            throw new CollectionQueryException("invalid-query");

        using var lease = EnterProject(projectRevision);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await data.SearchCollectionsAsync(new SearchCriteria
        {
            SearchText = query?.Trim(), Page = page, PageSize = pageSize
        });
        // データ読み込みが完了するまで受付ハンドルを保持する。
        cancellationToken.ThrowIfCancellationRequested();
        return new(GetProject(), result.Collections.Select(ToView).ToArray(),
            result.TotalCount, result.Page, result.PageSize);
    }

    public async Task<CollectionView> GetAsync(string projectRevision, string collectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId) || collectionId.Length > 256)
            throw new CollectionQueryException("invalid-collection-id");

        using var lease = EnterProject(projectRevision);
        cancellationToken.ThrowIfCancellationRequested();
        var collection = await data.GetCollectionByIdAsync(collectionId);
        cancellationToken.ThrowIfCancellationRequested();
        return collection is null
            ? throw new CollectionQueryException("collection-not-found") : ToView(collection);
    }

    private IDisposable EnterProject(string projectRevision) =>
        runtime.TryEnter(projectRevision, requireRevision: true, out var error)
            ?? throw new CollectionQueryException(error!);

    // ファイルパス、添付ファイルの内容、内部の保存形式は公開しない。
    private static CollectionView ToView(CollectionData collection)
    {
        var values = collection.IndexData.Values;
        return new(collection.IndexData.SystemData.Id, values.Name, values.ManagementCode,
            values.RegistrationDate, values.Category,
            new[] { values.FirstTag, values.SecondTag, values.ThirdTag }, values.Location,
            collection.CollectionCurrentInventory?.ToString(CultureInfo.InvariantCulture),
            collection.CollectionInventoryStatus.ToString());
    }
}

public sealed record ProjectInfo(string Revision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name, bool HasProject);

public sealed record CollectionView(string Id, string Name, string ManagementCode,
    string RegistrationDate, string Category, string[] Tags, string Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CurrentInventory, string InventoryStatus);

public sealed record CollectionQueryResult(ProjectInfo Project, CollectionView[] Collections,
    int TotalCount, int Page, int PageSize);

public sealed class CollectionQueryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
