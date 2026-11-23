using Baihua.Contracts.LocalModels;

namespace Baihua.AI.Provider;

/// <summary>
/// 本地模型运行时管理接口（厂商中立）。
///
/// 抽象"把已下载的模型目录启动为常驻推理进程/服务"的启停与状态管理：
/// - OpenVINO：openvino_llm_server.py（当前实现，ai.provider.openvino）
/// - 未来 NVIDIA：TensorRT-LLM / vLLM 进程
/// - 未来 AMD：vLLM-ROCm / llama.cpp ROCm
/// Family 的部署页/网关只依赖本接口。
/// </summary>
public interface ILocalRuntimeManager
{
    /// <summary>模型根目录（已下载模型存放处）</summary>
    string ModelRoot { get; }

    /// <summary>扫描已下载模型（目录存在即算已下载）</summary>
    List<OpenVinoInstalledModelDto> GetInstalledModels();

    /// <summary>当前运行中的实例</summary>
    List<OpenVinoInstalledModelDto> GetRunning();

    /// <summary>启动模型（指定设备），等待就绪后返回端口</summary>
    Task<OpenVinoRunResult> StartAsync(string modelPath, string device, CancellationToken ct = default);

    /// <summary>停止指定端口的实例</summary>
    Task<bool> StopAsync(int port, CancellationToken ct = default);
}
