using Baihua.Contracts.OpenClaw;

namespace Baihua.AI.Provider;

/// <summary>
/// 本地 AI 运行时配置/扫描/启停（Ollama / LM Studio / llama.cpp / OpenVINO）。
/// 接口定义在 Baihua.AI.Provider，实现由宿主应用（Baihua.Server）提供。
/// </summary>
public interface ILocalAiConfigService
{
    Task<OpenClawLocalAiConfigDto> GetLocalAiConfigAsync();
    Task<bool> SaveLocalAiConfigAsync(SaveOpenClawLocalAiConfigRequest request);
    Task<List<OpenClawLocalModelDto>> ScanLocalModelsAsync(string provider);
    Task<LocalAiServiceStatusDto> DetectAndStartLocalAiAsync(string provider);
    Task<bool> SyncLocalModelsToOpenClawAsync(string provider);
}
