namespace Baihua.Contracts.LocalModels;

/// <summary>
/// OpenVINO 可下载模型目录（精选：按场景去同质，只保留最合适的模型）。
/// 下载源：HuggingFace 镜像（hf-mirror.com，国内直连快）/ ModelScope，
/// OpenVINO 官方组织仓库（INT4 量化，适配 Intel Arc GPU）。
/// MinVramGiB：所需显存（权重 + KV cache + 推理开销的估算值），目录页按当前显卡过滤。
/// </summary>
public static class OpenVinoCatalog
{
    public static IReadOnlyList<OpenVinoCatalogEntry> All => _entries;

    private static readonly List<OpenVinoCatalogEntry> _entries =
    [
        new()
        {
            Id = "qwen3.5-9b",
            Name = "Qwen 3.5 9B（多模态）",
            ParameterSize = "9B",
            SizeGiB = 8.8,
            MinVramGiB = 10,
            Description = "旗舰：对话 + 视觉 + 思维链（int8），质量全面优于 Qwen2.5-7B",
            IsVision = true,
            Category = "对话",
            ModelScopeRepo = "OpenVINO/Qwen3.5-9B-int8-ov",
            HuggingFaceRepo = "openvino/Qwen3.5-9B-int8-ov",
            DirectoryName = "Qwen3.5-9B-int8-ov",
        },
        new()
        {
            Id = "qwen2.5-7b",
            Name = "Qwen 2.5 7B Instruct",
            ParameterSize = "7B",
            SizeGiB = 4.7,
            MinVramGiB = 6,
            Description = "轻量对话：速度快（~13 tok/s），显存占用小",
            Category = "对话",
            ModelScopeRepo = "OpenVINO/Qwen2.5-7B-Instruct-INT4-OV",
            HuggingFaceRepo = "openvino/Qwen2.5-7B-Instruct-INT4-OV",
            DirectoryName = "Qwen2.5-7B-Instruct-int4-ov",
        },
        new()
        {
            Id = "qwen2.5-coder-7b",
            Name = "Qwen 2.5 Coder 7B",
            ParameterSize = "7B",
            SizeGiB = 4.7,
            MinVramGiB = 6,
            Description = "代码生成/补全/解释，编程 Agent 可用",
            Category = "代码",
            ModelScopeRepo = "OpenVINO/Qwen2.5-Coder-7B-Instruct-INT4-OV",
            HuggingFaceRepo = "openvino/Qwen2.5-Coder-7B-Instruct-INT4-OV",
            DirectoryName = "Qwen2.5-Coder-7B-Instruct-int4-ov",
        },
        new()
        {
            Id = "biancang-instruct",
            Name = "扁仓 BianCang Instruct（医疗）",
            ParameterSize = "7B",
            SizeGiB = 14.2,
            MinVramGiB = 8,
            Description = "中文医疗领域指令模型。下载 safetensors 源（~14GB）本地转 OpenVINO IR（~7GB）",
            IsMedical = true,
            Category = "医疗",
            ModelScopeRepo = "QLU-NLP/BianCang-Qwen2.5-7B-Instruct",
            HuggingFaceRepo = "QLU-NLP/BianCang-Qwen2.5-7B-Instruct",
            Format = "safetensors",
        },
        new()
        {
            Id = "kokoro-82m",
            Name = "Kokoro 82M（TTS 语音合成）",
            ParameterSize = "82M",
            SizeGiB = 0.17,
            MinVramGiB = null,
            Description = "中英文语音合成，独立 Python TTS 服务推理（非 OVMS，不占 GPU 显存）",
            IsTts = true,
            Category = "TTS",
            ModelScopeRepo = "OpenVINO/Kokoro-82M-int8-ov",
            HuggingFaceRepo = "openvino/Kokoro-82M-int8-ov",
        },
    ];

    public static OpenVinoCatalogEntry? GetById(string id) =>
        _entries.FirstOrDefault(e => e.Id == id);
}
