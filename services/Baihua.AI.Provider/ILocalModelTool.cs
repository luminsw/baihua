using Baihua.Contracts.LocalModels;

namespace Baihua.AI.Provider;

/// <summary>
/// 本地模型工具提供方接口（厂商中立的"最大公约数"）。
///
/// 每个本地推理工具（Ollama / llama.cpp / LM Studio / OpenVINO / 未来 NVIDIA CUDA / AMD ROCm）
/// 实现本接口，供 Family 的本地模型部署页与下载/运行编排统一调用。
/// 厂商专有能力不进本接口——由各 provider 自行扩展（见 ai.provider.openvino 的
/// ILocalVisionInference / ILocalRuntimeManager）。
/// </summary>
public interface ILocalModelTool
{
    /// <summary>工具标识（ollama / llamacpp / lmstudio / openvino / cuda / rocm）</summary>
    string Id { get; }

    /// <summary>工具显示名</summary>
    string Name { get; }

    /// <summary>安装/版本/运行/模型根 状态</summary>
    Task<(bool Installed, string? Version, bool Running, string ModelPath)> GetToolInfoAsync(CancellationToken ct = default);

    /// <summary>当前运行中的模型</summary>
    Task<List<RunningModelDto>> GetRunningModelsAsync(CancellationToken ct = default);

    /// <summary>可部署/可下载的模型清单（厂商目录或本地扫描）</summary>
    Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default);

    /// <summary>本机已下载的模型</summary>
    Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(CancellationToken ct = default);

    /// <summary>确保运行时就绪（启动/拉起工具进程或后端服务）</summary>
    Task EnsureServerRunningAsync(CancellationToken ct = default);

    /// <summary>加载（运行）指定模型</summary>
    Task<bool> LoadModelAsync(string modelName, CancellationToken ct = default);

    /// <summary>卸载（停止）指定模型</summary>
    Task<bool> UnloadModelAsync(string modelName, CancellationToken ct = default);

    /// <summary>
    /// 查询模型详情（路径 / 参数等）。默认实现不支持详情，各 Provider 可覆盖。
    /// </summary>
    Task<ModelDetailsDto?> GetModelDetailsAsync(string modelName, CancellationToken ct = default)
        => Task.FromResult<ModelDetailsDto?>(null);

    /// <summary>
    /// 删除本地模型。默认实现不支持删除，各 Provider 可覆盖。
    /// </summary>
    Task<bool> DeleteModelAsync(string modelName, CancellationToken ct = default)
        => Task.FromResult(false);
}
