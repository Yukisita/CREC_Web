using System.Globalization;
using CREC_Web.Models;

namespace CREC_Web.Services;

/// <summary>MCPとWebMCPの業務処理。すべての読み取りを指定されたプロジェクト世代に固定する。</summary>
public sealed class CrecReadService(ProjectRuntime runtime, CrecDataService data,
    ProjectCatalogService catalog, IConfiguration configuration, CollectionFileReader files)
{
    public ProjectInfo GetProject()
    {
        var state = runtime.Current;
        return new(state.Revision, state.Name, state.HasProject);
    }

    public ProjectListView ListProjects(string revision, int page, int pageSize)
    {
        using var lease = EnterProject(revision, requireProject: false);
        ValidatePage(page, pageSize);
        var result = catalog.List(runtime.Current.FilePath);
        return new(GetProject(), result.ErrorCode, Page(result.Projects.Select(p =>
            new ProjectCandidateView(p.Id, p.Name, p.IsCurrent, p.ErrorCode)).ToArray(), page, pageSize));
    }

    public ProjectSettingsView GetSettings(string revision)
    {
        using var lease = EnterProject(revision);
        return new(GetProject(), new Dictionary<string, string>
        {
            ["name"] = configuration["CollectionNameLabel"] ?? "Name",
            ["id"] = configuration["UUIDLabel"] ?? "ID",
            ["managementCode"] = configuration["ManagementCodeLabel"] ?? "MC",
            ["category"] = configuration["CategoryLabel"] ?? "Category",
            ["tag1"] = configuration["FirstTagLabel"] ?? "Tag 1",
            ["tag2"] = configuration["SecondTagLabel"] ?? "Tag 2",
            ["tag3"] = configuration["ThirdTagLabel"] ?? "Tag 3"
        });
    }

    public async Task<CollectionQueryResult> SearchAsync(string revision, string? query, SearchField field,
        SearchMethod method, InventoryStatus? inventoryStatus, int page, int pageSize, CancellationToken token)
    {
        using var lease = EnterProject(revision);
        ValidatePage(page, pageSize);
        if (query?.Length > 256 || !Enum.IsDefined(field) || !Enum.IsDefined(method)
            || (inventoryStatus.HasValue && !Enum.IsDefined(inventoryStatus.Value)))
            throw new CrecReadException("invalid-query");
        token.ThrowIfCancellationRequested();
        var result = await data.SearchCollectionsAsync(new SearchCriteria
        {
            SearchText = query?.Trim(), SearchField = field, SearchMethod = method,
            InventoryStatus = inventoryStatus, Page = page, PageSize = pageSize
        });
        token.ThrowIfCancellationRequested();
        return new(GetProject(), result.Collections.Select(ToView).ToArray(), result.TotalCount, page, pageSize);
    }

    public async Task<SearchOptionsView> GetSearchOptionsAsync(string revision, int page, int pageSize, CancellationToken token)
    {
        using var lease = EnterProject(revision);
        ValidatePage(page, pageSize);
        token.ThrowIfCancellationRequested();
        var categories = await data.GetCategoriesAsync();
        var tags = await data.GetTagsAsync();
        token.ThrowIfCancellationRequested();
        return new(Enum.GetNames<SearchField>(), Enum.GetNames<SearchMethod>(), Enum.GetNames<InventoryStatus>(),
            Page(categories, page, pageSize), Page(tags, page, pageSize));
    }

    public async Task<CollectionView> GetAsync(string revision, string collectionId, CancellationToken token)
    {
        using var lease = EnterProject(revision);
        return ToView(await FindAsync(collectionId, token));
    }

    public async Task<InventoryView> GetInventoryAsync(string revision, string collectionId,
        int page, int pageSize, CancellationToken token)
    {
        using var lease = EnterProject(revision);
        ValidatePage(page, pageSize);
        var collection = await FindAsync(collectionId, token);
        var inventory = collection.InventoryData;
        return new(collection.IndexData.SystemData.Id, Number(collection.CollectionCurrentInventory),
            collection.CollectionInventoryStatus.ToString(),
            new(Number(inventory.Setting.SafetyStock), Number(inventory.Setting.ReorderPoint), Number(inventory.Setting.MaximumLevel)),
            Page(inventory.Operations.Select(op => new InventoryRecordView(op.DateTime, op.OperationType.ToString(),
                Number(op.Quantity)!, op.Note)).ToArray(), page, pageSize));
    }

    public async Task<CollectionFilesView> ListFilesAsync(string revision, string collectionId, CollectionFileArea area,
        string? path, int page, int pageSize, CancellationToken token)
    {
        using var lease = EnterProject(revision);
        ValidatePage(page, pageSize);
        var collection = await FindAsync(collectionId, token);
        var listing = files.List(collection, area, path);
        token.ThrowIfCancellationRequested();
        return new(collection.IndexData.SystemData.Id, area, listing.CurrentPath, Page(listing.Entries, page, pageSize));
    }

    public async Task<FileContentView> ReadFileAsync(string revision, string collectionId, CollectionFileArea area,
        string path, long offset, int maxBytes, FileContentEncoding encoding, string? version, CancellationToken token)
    {
        using var lease = EnterProject(revision);
        var collection = await FindAsync(collectionId, token);
        // 読み取りが実際に完了するまで、キャンセル時もプロジェクト受付を保持する。
        return await files.ReadAsync(collection, area, path, offset, maxBytes, encoding, version, token);
    }

    private async Task<CollectionData> FindAsync(string collectionId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(collectionId) || collectionId.Length > 256)
            throw new CrecReadException("invalid-collection-id");
        token.ThrowIfCancellationRequested();
        var collection = await data.GetCollectionByIdAsync(collectionId);
        token.ThrowIfCancellationRequested();
        return collection ?? throw new CrecReadException("collection-not-found");
    }

    private IDisposable EnterProject(string revision, bool requireProject = true) =>
        runtime.TryEnter(revision, requireRevision: true, out var error, requireProject)
            ?? throw new CrecReadException(error!);

    private static void ValidatePage(int page, int pageSize)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
            throw new CrecReadException("invalid-page");
    }

    private static ReadPage<T> Page<T>(IReadOnlyCollection<T> items, int page, int pageSize) =>
        new(items.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), items.Count, page, pageSize);

    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static CollectionView ToView(CollectionData collection)
    {
        var values = collection.IndexData.Values;
        return new(collection.IndexData.SystemData.Id, values.Name, values.ManagementCode,
            values.RegistrationDate, collection.IndexData.SystemData.SystemCreateDate, values.Category,
            [values.FirstTag, values.SecondTag, values.ThirdTag], values.Location,
            Number(collection.CollectionCurrentInventory), collection.CollectionInventoryStatus.ToString());
    }
}

public sealed class CrecReadException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
