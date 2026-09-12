using Baihua.Core;

namespace Baihua.Modules.Vault.Controllers
{
    public class NoteMetadata
    {
        public string Path { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime Modified { get; set; }
        public long Size { get; set; }
        public string? Hash { get; set; }
    }

    public class NoteUpdateRequest
    {
        public string Path { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Modified { get; set; }
        public string? Hash { get; set; }
    }

    public class ConflictResolution
    {
        public string Path { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Message { get; set; }
        public DateTime? ServerModified { get; set; }
        public DateTime? ClientModified { get; set; }
    }

    public class SyncResponse
    {
        public long Timestamp { get; set; }
        public List<NoteMetadata> Notes { get; set; } = new List<NoteMetadata>();
        public List<TaskInfo> Tasks { get; set; } = new List<TaskInfo>();
        public int TotalNotes { get; set; }
        public int TotalTasks { get; set; }
    }

    public class SystemInfo
    {
        public string ServerVersion { get; set; } = string.Empty;
        public string? VaultPath { get; set; }
        public bool VaultExists { get; set; }
        public int VaultFileCount { get; set; }
        public TimeSpan ServiceUptime { get; set; }
        public string ApiBaseUrl { get; set; } = string.Empty;
        public List<string> SupportedFeatures { get; set; } = new List<string>();
    }
}
