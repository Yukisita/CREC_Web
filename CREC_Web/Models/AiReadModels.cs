namespace CREC_Web.Models;

public sealed record ProjectInfo(string Revision, string? Name, bool HasProject);
public sealed record ProjectSettingsView(ProjectInfo Project, IReadOnlyDictionary<string, string> Labels);
public sealed record ReadPage<T>(T[] Items, int TotalCount, int Page, int PageSize);
public sealed record ProjectListView(ProjectInfo Current, string? ErrorCode, ReadPage<ProjectCandidateView> Projects);
public sealed record ProjectCandidateView(string Id, string Name, bool IsCurrent, string? ErrorCode);
public sealed record CollectionView(string Id, string Name, string ManagementCode,
    string RegistrationDate, string CreatedAt, string Category, string[] Tags, string Location,
    string? CurrentInventory, string InventoryStatus);
public sealed record CollectionQueryResult(ProjectInfo Project, CollectionView[] Collections,
    int TotalCount, int Page, int PageSize);
public sealed record SearchOptionsView(string[] SearchFields, string[] SearchMethods, string[] InventoryStatuses,
    ReadPage<string> Categories, ReadPage<string> Tags);
public sealed record InventorySettingsView(string? SafetyStock, string? ReorderPoint, string? MaximumLevel);
public sealed record InventoryRecordView(string DateTime, string OperationType, string Quantity, string Note);
public sealed record InventoryView(string CollectionId, string? CurrentInventory, string Status,
    InventorySettingsView Settings, ReadPage<InventoryRecordView> History);
public enum CollectionFileArea { Data, Pictures, Videos, ThreeD, Thumbnail }
public enum FileContentEncoding { Auto, Utf8, Base64 }
public sealed record CollectionFilesView(string CollectionId, CollectionFileArea Area, string Path,
    ReadPage<DataFileEntry> Entries);
public sealed record FileContentView(string CollectionId, CollectionFileArea Area, string Path,
    string MediaType, string Version, long TotalBytes, long Offset, int BytesRead, long? NextOffset,
    string Encoding, string Content);
