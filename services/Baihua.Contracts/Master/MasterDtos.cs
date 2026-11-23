using Baihua.Contracts.Ai;

namespace Baihua.Contracts.Master;

public class CreateMasterRequest
{
    public string Goal { get; set; } = string.Empty;
    public string Industry { get; set; } = string.Empty;
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
    public string MasterId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Stage { get; set; } = "";
    public List<ChatHistoryItem>? History { get; set; }
}

public class StageCompleteRequest
{
    public string StageName { get; set; } = string.Empty;
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
    public string? Foundation { get; set; }
    public string? LearningStyle { get; set; }
    public string? Strengths { get; set; }
    public string? Weaknesses { get; set; }
    public List<string> GraduatedStages { get; set; } = new();
    public string CurrentStage { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public class AssessRequest
{
    public string Type { get; set; } = "capability";
}

public class AssessResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Report { get; set; } = "";
    public double PassProbability { get; set; }
    public List<string> WeakPoints { get; set; } = new();
    public string Advice { get; set; } = "";
}

public class MasterListItem
{
    public string MasterId { get; set; } = "";
    public string MasterName { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Industry { get; set; } = "";
    public string CurrentStage { get; set; } = "";
    public int CurrentStageOrder { get; set; }
    public List<string> GraduatedStages { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class UpdateProfileRequest
{
    public string? Foundation { get; set; }
    public string? LearningStyle { get; set; }
    public string? Strengths { get; set; }
    public string? Weaknesses { get; set; }
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

public class MasterEvictResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int CompressedStages { get; set; }
    public int EvictedStages { get; set; }
}

public class ConversationHistoryItem
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
    public List<ConversationHistoryItem> Items { get; set; } = new();
}

public class ConversationSyncRequest
{
    public List<ConversationHistoryItem> Items { get; set; } = new();
}

public class ConversationSyncResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int SyncedCount { get; set; }
}
