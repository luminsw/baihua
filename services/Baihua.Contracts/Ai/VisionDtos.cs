namespace Baihua.Contracts.Ai;

/// <summary>
/// 本地视觉识别请求（Qwen2.5-VL，OpenVINO）
/// </summary>
public class VisionRequestDto
{
    /// <summary>图片 Base64（原始字节，常见格式 png/jpg）</summary>
    public string ImageBase64 { get; set; } = "";

    /// <summary>提问内容，默认"请详细描述这张图片的内容"</summary>
    public string Prompt { get; set; } = "请详细描述这张图片的内容。";

    /// <summary>模型标识：3b / 7b</summary>
    public string Model { get; set; } = "3b";
}

/// <summary>
/// 本地视觉识别结果
/// </summary>
public class VisionResultDto
{
    /// <summary>模型输出的描述文本</summary>
    public string Text { get; set; } = "";

    /// <summary>实际使用的模型标识</summary>
    public string Model { get; set; } = "";

    /// <summary>耗时（毫秒，含模型加载）</summary>
    public long ElapsedMs { get; set; }

    /// <summary>视觉服务是否在运行</summary>
    public bool ServerRunning { get; set; }
}

/// <summary>
/// 本地视觉服务状态
/// </summary>
public class VisionStatusDto
{
    /// <summary>功能是否启用（配置开关）</summary>
    public bool Enabled { get; set; }

    /// <summary>Python 视觉服务进程是否在运行</summary>
    public bool ServerRunning { get; set; }

    /// <summary>视觉服务端口（默认 8801）</summary>
    public int Port { get; set; } = 8801;

    /// <summary>错误信息（如 Python 未安装、服务启动失败）</summary>
    public string? Message { get; set; }

    /// <summary>可用模型列表</summary>
    public List<VisionModelInfo> Models { get; set; } = new();
}

/// <summary>
/// 本地视觉模型信息
/// </summary>
public class VisionModelInfo
{
    /// <summary>模型标识：3b / 7b</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名称</summary>
    public string Name { get; set; } = "";

    /// <summary>OpenVINO 模型目录路径</summary>
    public string Path { get; set; } = "";

    /// <summary>模型目录是否存在</summary>
    public bool Exists { get; set; }

    /// <summary>模型目录大小（字节）</summary>
    public long SizeBytes { get; set; }
}
