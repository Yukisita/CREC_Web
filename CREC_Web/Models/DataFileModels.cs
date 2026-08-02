/*
CREC Web - Data File Manager Models
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Models
{
    public sealed class DataDirectoryListing
    {
        public string CurrentPath { get; set; } = string.Empty;
        public List<DataFileEntry> Entries { get; set; } = new();
    }

    public sealed class DataFileEntry
    {
        public string Name { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string EntryType { get; set; } = string.Empty;
        public long? Size { get; set; }
        public DateTime LastModifiedUtc { get; set; }
    }

    public sealed class CreateDataDirectoryRequest
    {
        public string ParentPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public sealed class RenameDataEntryRequest
    {
        public string Path { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
    }
}
