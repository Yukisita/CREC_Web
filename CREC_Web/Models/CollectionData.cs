/*
CREC Web - Collection Data Model
Copyright (c) [2025] [S.Yukisita]
This software is released under the MIT License.
*/

using System.Runtime.Serialization;

namespace CREC_Web.Models
{
    /// <summary>
    /// 在庫状況の種類
    /// </summary>
    public enum InventoryStatus
    {
        StockOut,        // 在庫切れ
        UnderStocked,    // 在庫不足
        AppropriateNeedReorder, // 在庫適正だが発注点以下
        Appropriate,     // 在庫適正
        OverStocked,     // 在庫過剰
        NotSet           // 未設定
    }

    /// <summary>
    /// 在庫操作の種類
    /// </summary>
    public enum InventoryOperationType
    {
        EntryOperation,   // 入庫
        ExitOperation,     // 出庫
        Stocktaking         // 棚卸
    }

    /// <summary>
    /// 在庫メタデータ
    /// </summary>
    [DataContract]
    public class InventoryMetaData
    {
        /// <summary>
        /// コレクションID
        /// </summary>
        [DataMember(Name = "collectionId")]
        public string CollectionId { get; set; }

        public InventoryMetaData()
        {
            CollectionId = string.Empty;
        }

        public InventoryMetaData(string collectionId)
        {
            CollectionId = collectionId ?? string.Empty;
        }
    }

    /// <summary>
    /// 在庫管理設定
    /// </summary>
    [DataContract]
    public class InventoryOperationSetting
    {
        /// <summary>
        /// 安全在庫数
        /// </summary>
        [DataMember(Name = "safetyStock")]
        public int? SafetyStock { get; set; }

        /// <summary>
        /// 発注点
        /// </summary>
        [DataMember(Name = "reorderPoint")]
        public int? ReorderPoint { get; set; }

        /// <summary>
        /// 最大在庫数
        /// </summary>
        [DataMember(Name = "maximumLevel")]
        public int? MaximumLevel { get; set; }

        public InventoryOperationSetting()
        {
            SafetyStock = null;
            ReorderPoint = null;
            MaximumLevel = null;
        }

        public InventoryOperationSetting(int? safetyStock, int? reorderPoint, int? maximumLevel)
        {
            SafetyStock = safetyStock;
            ReorderPoint = reorderPoint;
            MaximumLevel = maximumLevel;
        }
    }

    /// <summary>
    /// 在庫操作レコード
    /// </summary>
    [DataContract]
    public class InventoryOperationRecord
    {
        /// <summary>
        /// 在庫操作日
        /// </summary>
        /// <remarks>
        /// UTCで記録（例：2024-11-08T18:13:37.0000000+00:00）
        /// </remarks>
        [DataMember(Name = "dateTime")]
        public string DateTime { get; set; }

        /// <summary>
        /// 在庫操作の種類
        /// </summary>
        [DataMember(Name = "operationType")]
        public InventoryOperationType OperationType { get; set; }

        /// <summary>
        /// 現在の在庫数
        /// </summary>
        [DataMember(Name = "quantity")]
        public int Quantity { get; set; }

        /// <summary>
        /// 在庫操作のコメント
        /// </summary>
        [DataMember(Name = "note")]
        public string Note { get; set; }

        public InventoryOperationRecord()
        {
            DateTime = string.Empty;
            OperationType = InventoryOperationType.Stocktaking;
            Quantity = 0;
            Note = string.Empty;
        }

        public InventoryOperationRecord(string dateTime, InventoryOperationType operationType, int quantity, string note)
        {
            DateTime = dateTime ?? string.Empty;
            OperationType = operationType;
            Quantity = quantity;
            Note = note ?? string.Empty;
        }
    }

    /// <summary>
    /// 在庫管理データ全体 (JSON用)
    /// </summary>
    [DataContract]
    public class InventoryData
    {
        /// <summary>
        /// 在庫メタデータ
        /// </summary>
        [DataMember(Name = "metaData")]
        public InventoryMetaData MetaData { get; set; }

        /// <summary>
        /// 在庫管理設定
        /// </summary>
        [DataMember(Name = "setting")]
        public InventoryOperationSetting Setting { get; set; }

        /// <summary>
        /// 在庫操作レコード
        /// </summary>
        [DataMember(Name = "operations")]
        public List<InventoryOperationRecord> Operations { get; set; }

        public InventoryData()
        {
            MetaData = new InventoryMetaData();
            Setting = new InventoryOperationSetting();
            Operations = new List<InventoryOperationRecord>();
        }

        /// <summary>
        /// 現在の在庫数を計算
        /// </summary>
        public int? CalculateCurrentInventory()
        {
            int? inventory = 0;
            if (Operations != null)
            {
                foreach (var op in Operations)
                {
                    inventory += op.Quantity;
                }
            }
            else
            {
                inventory = null;
            }
            return inventory;
        }

        /// <summary>
        /// 在庫状況を取得
        /// </summary>
        public InventoryStatus GetInventoryStatus(int? count)
        {
            if (!Setting.SafetyStock.HasValue && !Setting.ReorderPoint.HasValue && !Setting.MaximumLevel.HasValue)
            {
                return InventoryStatus.NotSet;
            }

            if (count == null)
            {
                return InventoryStatus.NotSet;
            }
            else if (count <= 0)
            {
                return InventoryStatus.StockOut;
            }
            else if (Setting.SafetyStock.HasValue && count < Setting.SafetyStock.Value)
            {
                return InventoryStatus.UnderStocked;
            }
            else if (Setting.ReorderPoint.HasValue && count >= (Setting.SafetyStock ?? 0) && count < Setting.ReorderPoint.Value)
            {
                return InventoryStatus.AppropriateNeedReorder;
            }
            else if (Setting.MaximumLevel.HasValue && count > Setting.MaximumLevel.Value)
            {
                return InventoryStatus.OverStocked;
            }
            else
            {
                return InventoryStatus.Appropriate;
            }
        }
    }

    /// <summary>
    /// コレクションデータクラス
    /// </summary>
    [DataContract]
    public class CollectionData
    {
        /// <summary>
        /// コレクションフォルダパス
        /// </summary>
        [DataMember(Name = "collectionFolderPath")]
        public string CollectionFolderPath { get; set; } = string.Empty;

        /// <summary>
        /// コレクション名
        /// </summary>
        [DataMember(Name = "collectionName")]
        public string CollectionName { get; set; } = string.Empty;

        /// <summary>
        /// コレクションID
        /// </summary>
        [DataMember(Name = "collectionID")]
        public string CollectionID { get; set; } = string.Empty;

        /// <summary>
        /// 管理コード
        /// </summary>
        [DataMember(Name = "collectionMC")]
        public string CollectionMC { get; set; } = string.Empty;

        /// <summary>
        /// 登録日
        /// </summary>
        [DataMember(Name = "collectionRegistrationDate")]
        public string CollectionRegistrationDate { get; set; } = string.Empty;

        /// <summary>
        /// カテゴリ
        /// </summary>
        [DataMember(Name = "collectionCategory")]
        public string CollectionCategory { get; set; } = string.Empty;

        /// <summary>
        /// タグ1
        /// </summary>
        [DataMember(Name = "collectionTag1")]
        public string CollectionTag1 { get; set; } = string.Empty;

        /// <summary>
        /// タグ2
        /// </summary>
        [DataMember(Name = "collectionTag2")]
        public string CollectionTag2 { get; set; } = string.Empty;

        /// <summary>
        /// タグ3
        /// </summary>
        [DataMember(Name = "collectionTag3")]
        public string CollectionTag3 { get; set; } = string.Empty;

        /// <summary>
        /// 場所(Real)
        /// </summary>
        [DataMember(Name = "collectionRealLocation")]
        public string CollectionRealLocation { get; set; } = string.Empty;

        /// <summary>
        /// コレクションの在庫管理データ
        /// </summary>
        [DataMember(Name = "inventoryData")]
        public InventoryData InventoryData { get; set; } = new InventoryData();

        /// <summary>
        /// 現在の在庫数
        /// </summary>
        public int? CollectionCurrentInventory { get; set; } = null;

        /// <summary>
        /// 在庫状況
        /// </summary>
        public InventoryStatus CollectionInventoryStatus { get; set; } = InventoryStatus.NotSet;

        /// <summary>
        /// サムネイル画像パス（相対パス）
        /// </summary>
        public string? ThumbnailPath { get; set; }

        /// <summary>
        /// 画像ファイルリスト
        /// </summary>
        public List<string> ImageFiles { get; set; } = new List<string>();

        /// <summary>
        /// その他ファイルリスト
        /// </summary>
        public List<string> OtherFiles { get; set; } = new List<string>();
    }

    /// <summary>
    /// 検索フィールドの種類
    /// </summary>
    public enum SearchField
    {
        All,            // 全項目
        ID,             // UUID/ID
        Name,           // 名称
        ManagementCode, // 管理コード
        Category,       // カテゴリ
        Tag,            // タグ (全て)
        Tag1,           // タグ1
        Tag2,           // タグ2
        Tag3,           // タグ3
        Location,       // 場所
    }

    /// <summary>
    /// 検索方式の種類
    /// </summary>
    public enum SearchMethod
    {
        Partial,        // 部分一致
        Prefix,         // 前方一致
        Suffix,         // 後方一致
        Exact           // 完全一致
    }

    /// <summary>
    /// 検索条件クラス
    /// </summary>
    public class SearchCriteria
    {
        public string? SearchText { get; set; }
        public SearchField SearchField { get; set; } = SearchField.All;
        public SearchMethod SearchMethod { get; set; } = SearchMethod.Partial;
        public InventoryStatus? InventoryStatus { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    /// <summary>
    /// 検索結果クラス
    /// </summary>
    public class SearchResult
    {
        public List<CollectionData> Collections { get; set; } = new List<CollectionData>();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    }
}