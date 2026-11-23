using System.Text.Json;
using Baihua.Contracts.ComputePool;

namespace Baihua.Web.Services;

public partial class ApiService
{
    /// <summary>算力池总览（本机 + 局域网对端节点与模型/TPS）。</summary>
    public async Task<ComputePoolViewDto?> GetComputePoolAsync()
    {
        try
        {
            using var quick = new CancellationTokenSource(QuickCallTimeout);
            var response = await _httpClient.GetAsync("/api/compute-pool", quick.Token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ComputePoolViewDto>(quick.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取算力池失败");
            return null;
        }
    }

    /// <summary>立即刷新对端能力。</summary>
    public async Task<bool> RefreshComputePoolAsync()
    {
        try
        {
            using var quick = new CancellationTokenSource(QuickCallTimeout);
            var response = await _httpClient.PostAsync("/api/compute-pool/refresh", null, quick.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新算力池失败");
            return false;
        }
    }

    /// <summary>一键把本机 OVMS 登记为本地算力提供方（探测 OpenVinoOms__BaseUrl 的 /v1/models）。</summary>
    public async Task<(bool ok, string message)> RegisterLocalOvmsAsync()
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await _httpClient.PostAsync("/api/compute-pool/register-local-ovms", null, quick.Token);
            var body = await response.Content.ReadAsStringAsync(quick.Token);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (response.IsSuccessStatusCode)
                    return (true, doc.RootElement.TryGetProperty("message", out var m) ? (m.GetString() ?? "已登记") : "已登记");
                return (false, doc.RootElement.TryGetProperty("error", out var e) ? (e.GetString() ?? body) : body);
            }
            catch
            {
                return (response.IsSuccessStatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "登记本地 OVMS 失败");
            return (false, ex.Message);
        }
    }

    /// <summary>选用某个节点+模型为本机主 AI 提供方。</summary>
    public async Task<(bool ok, string? error)> SelectComputeModelAsync(string serverId, string modelName)
    {
        try
        {
            using var quick = new CancellationTokenSource(QuickCallTimeout);
            var response = await _httpClient.PostAsync("/api/compute-pool/select",
                JsonContent.Create(new SelectComputeModelRequest { ServerId = serverId, ModelName = modelName }),
                quick.Token);
            if (response.IsSuccessStatusCode)
                return (true, null);
            var body = await response.Content.ReadAsStringAsync(quick.Token);
            try
            {
                using var doc = JsonDocument.Parse(body);
                return (false, doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : body);
            }
            catch { return (false, body); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选用模型失败");
            return (false, ex.Message);
        }
    }

    /// <summary>跨机测速：在指定节点运行该模型的快速 benchmark（耗时数秒~数十秒）。</summary>
    public async Task<BenchmarkRunResultDto?> RunComputeBenchmarkAsync(string serverId, string modelName)
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var response = await _httpClient.PostAsync("/api/compute-pool/benchmark",
                JsonContent.Create(new SelectComputeModelRequest { ServerId = serverId, ModelName = modelName }),
                quick.Token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<BenchmarkRunResultDto>(quick.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "跨机测速失败");
            return null;
        }
    }

    /// <summary>算力池深度任务：指定模型+提示词，经统一网关执行。</summary>
    public async Task<(bool ok, string? text, string? error)> RunPoolChatAsync(string modelName, string prompt)
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var response = await _httpClient.PostAsync("/api/compute-pool/chat",
                JsonContent.Create(new PoolChatRequest { ModelName = modelName, Prompt = prompt }),
                quick.Token);
            var body = await response.Content.ReadAsStringAsync(quick.Token);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean())
            {
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : "";
                return (true, text, null);
            }
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : body;
            return (false, null, err);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "算力池任务失败");
            return (false, null, ex.Message);
        }
    }

    /// <summary>从对端拉取模型（模型商店）。</summary>
    public async Task<(bool ok, string? error)> PullComputeModelAsync(string serverId, string modelName)
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(60));
            var response = await _httpClient.PostAsync("/api/compute-pool/pull-model",
                JsonContent.Create(new PullModelRequest { ServerId = serverId, ModelName = modelName }),
                quick.Token);
            if (response.IsSuccessStatusCode)
                return (true, null);
            var body = await response.Content.ReadAsStringAsync(quick.Token);
            try
            {
                using var doc = JsonDocument.Parse(body);
                return (false, doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : body);
            }
            catch { return (false, body); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "拉取模型失败");
            return (false, ex.Message);
        }
    }

    /// <summary>跨机布署：把本机模型布署到对端跑（对端拉取 + 启动运行时，大模型需数分钟）。</summary>
    public async Task<(bool ok, string? message, string? error)> DeployComputeModelAsync(string serverId, string modelName)
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(60));
            var response = await _httpClient.PostAsync("/api/compute-pool/deploy",
                JsonContent.Create(new DeployModelRequest { ServerId = serverId, ModelName = modelName }),
                quick.Token);
            var body = await response.Content.ReadAsStringAsync(quick.Token);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (response.IsSuccessStatusCode)
                {
                    var port = doc.RootElement.TryGetProperty("port", out var p) ? p.GetInt32() : 0;
                    var device = doc.RootElement.TryGetProperty("device", out var d) ? d.GetString() : "";
                    return (true, $"已布署 {modelName} 到对端（{device} · :{port}）", null);
                }
                var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : body;
                return (false, null, err);
            }
            catch { return (false, null, body); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "跨机布署失败");
            return (false, null, ex.Message);
        }
    }

    /// <summary>删除对端登记（同时清理该对端自动登记的对端提供方，用于清理算力池里的过期节点）。</summary>
    public async Task<(bool ok, string? error)> DeleteComputePeerAsync(Guid peerId)
    {
        try
        {
            using var quick = new CancellationTokenSource(QuickCallTimeout);
            var response = await _httpClient.DeleteAsync($"/api/compute-pool/peers/{peerId}", quick.Token);
            if (response.IsSuccessStatusCode)
                return (true, null);
            var body = await response.Content.ReadAsStringAsync(quick.Token);
            try
            {
                using var doc = JsonDocument.Parse(body);
                return (false, doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : body);
            }
            catch { return (false, body); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除算力池对端失败");
            return (false, ex.Message);
        }
    }
}
