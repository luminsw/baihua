namespace MobileApp.Maui.Services;

public class MasterListItem
{
    public string MasterId { get; set; } = "";
    public string MasterName { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Industry { get; set; } = "";
    public string CurrentStage { get; set; } = "";
    public int CurrentStageOrder { get; set; }
    public string Status { get; set; } = "";
    public List<string> GraduatedStages { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class CreateMasterRequest
{
    public string Goal { get; set; } = "";
    public string Industry { get; set; } = "";
}

public class CreateMasterResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string MasterId { get; set; } = "";
    public string MasterName { get; set; } = "";
    public List<StageInfo> Stages { get; set; } = new();
}

public class StageInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public int Order { get; set; }
}

public class MasterChatRequest
{
    public string MasterId { get; set; } = "";
    public string Message { get; set; } = "";
    public string Stage { get; set; } = "";
    public List<ChatHistoryItem>? History { get; set; }
}

public class ChatHistoryItem
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string Stage { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class ConversationHistoryResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<ChatHistoryItem> Items { get; set; } = new();
}

public class ConversationSyncRequest
{
    public List<ChatHistoryItem> Items { get; set; } = new();
}

public class ConversationSyncResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int SyncedCount { get; set; }
}

public class StageCompleteRequest
{
    public string StageName { get; set; } = "";
}

public class StageCompleteResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string NextStage { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Blessing { get; set; } = "";
    public string KeyCorrections { get; set; } = "";
}

public class ApprenticeProfileResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string MasterId { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Industry { get; set; } = "";
    public string? Foundation { get; set; }
    public string? LearningStyle { get; set; }
    public string? Strengths { get; set; }
    public string? Weaknesses { get; set; }
    public List<string> GraduatedStages { get; set; } = new();
    public string CurrentStage { get; set; } = "";
    public string EstimatedTime { get; set; } = "";
    public List<StageItem> StageHistory { get; set; } = new();
    public string UpdatedAt { get; set; } = "";
}

public class ChatMessage
{
    public string Id { get; set; } = "";
    public bool IsUser { get; set; }
    public string Content { get; set; } = "";
    public DateTime Time { get; set; }
}

public class StageItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string RoleName { get; set; } = "";
    public bool IsCurrent { get; set; }
    public bool IsGraduated { get; set; }
    public int ProgressPct { get; set; }
    public string Summary { get; set; } = "";
}

public class VaultFocusItem
{
    public string VaultId { get; set; } = "";
    public string VaultName { get; set; } = "";
    public string State { get; set; } = "";
    public string? StageName { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class VaultFocusListResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<VaultFocusItem> Items { get; set; } = new();
}

public class VaultFocusUpdateRequest
{
    public string VaultId { get; set; } = "";
    public string State { get; set; } = "focused";
    public string? StageName { get; set; }
}

public class VaultFocusUpdateResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class VaultListItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class VaultListResponse
{
    public List<VaultListItem> Vaults { get; set; } = new();
}
