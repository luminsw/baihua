using System.Text.Json.Serialization;

namespace Baihua.Contracts.Vaults;

public class VaultConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 知识库标签
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 所属行业（如：笔记、开发等）
    /// </summary>
    public string Industry { get; set; } = string.Empty;

    /// <summary>
    /// 知识库来源：local=本地创建, mobile=移动端推送, cloud=官网同步
    /// </summary>
    public string Source { get; set; } = "local";

    public string PushedByDeviceId { get; set; } = "";
    public string PushedByDeviceName { get; set; } = "";
    public DateTime? PushedAt { get; set; }
}

public class VaultsResponse
{
    [JsonPropertyName("vaults")]
    public List<VaultConfig> Vaults { get; set; } = new();
}

public class VaultNoteResponse
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Modified { get; set; }
    public List<string>? Tags { get; set; }
    public bool AiGenerated { get; set; }
    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public DateTime? GeneratedAt { get; set; }
}

public class VaultRootResponse
{
    public string VaultPath { get; set; } = "";
}

public class VaultRootRequest
{
    public string VaultPath { get; set; } = "";
}

public class VaultRootPathPreferenceResponse
{
    public string VaultRootPath { get; set; } = "";
}

public class AddVaultRequest
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Industry { get; set; }
}

public class UpdateVaultRequest
{
    public string? Name { get; set; }
    public string? Tags { get; set; }
    public string? Industry { get; set; }
}

/// <summary>
/// 移动端推送 AI 生成知识库的请求
/// </summary>
public class MobileVaultPushRequest
{
    public string VaultName { get; set; } = "";
    public string Industry { get; set; } = "";
    public List<MobileVaultNoteDto> Notes { get; set; } = new();
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

public class MobileVaultNoteDto
{
    public string RelPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";
}

/// <summary>
/// 知识库浏览项（目录或笔记）
/// </summary>
public class VaultBrowseItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long? Size { get; set; }
    public DateTime? Modified { get; set; }
}

/// <summary>
/// 知识库浏览响应
/// </summary>
public class VaultBrowseResponse
{
    public string VaultId { get; set; } = "";
    public string VaultName { get; set; } = "";
    public string CurrentPath { get; set; } = "";
    public List<VaultBrowseItem> Items { get; set; } = new();
}

/// <summary>
/// 批量获取笔记响应（听知识库用）
/// </summary>
public class VaultNotesBatchResponse
{
    public List<VaultNoteResponse> Notes { get; set; } = new();
}
