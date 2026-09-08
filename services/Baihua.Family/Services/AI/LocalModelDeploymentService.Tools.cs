using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Baihua.Contracts.LocalModels;
using Baihua.Contracts.OpenClaw;
using Baihua.Data.Entities;
using Baihua.AI.Provider;
using Baihua.Family.Models;

namespace Baihua.Family.Services;

public partial class LocalModelDeploymentService
{
        #region Tool Detection

        public async Task<List<LocalToolInfoDto>> GetLocalToolsAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh && _cache.TryGetValue(ToolsCacheKey, out List<LocalToolInfoDto>? cached) && cached != null)
            {
                _logger.LogDebug("本地工具状态命中缓存");
                return cached;
            }

            var tools = new List<LocalToolInfoDto>();

            // OpenVINO GenAI（本地视觉模型，对接 vision_server.py）
            var (ovInstalled, ovVersion, ovRunning, ovModelPath) = await _openVino.GetToolInfoAsync(ct);
            tools.Add(new LocalToolInfoDto
            {
                Id = "openvino",
                Name = "OpenVINO GenAI",
                IsInstalled = ovInstalled,
                Version = ovVersion,
                IsRunning = ovRunning,
                DefaultModelPath = ovModelPath,
                InstallGuideUrl = "https://docs.openvino.ai"
            });

            _cache.Set(ToolsCacheKey, tools, TimeSpan.FromSeconds(300));
            return tools;
        }

        #endregion

        #region Running Model Management

        public async Task<List<RunningModelDto>> GetRunningModelsAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh && _cache.TryGetValue(RunningModelsCacheKey, out List<RunningModelDto>? cached) && cached != null)
            {
                _logger.LogDebug("运行中模型命中缓存");
                return cached;
            }

            var results = new List<RunningModelDto>();

            try
            {
                results.AddRange(await _openVino.GetRunningModelsAsync(ct));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 OpenVINO 运行中模型失败");
            }

            _cache.Set(RunningModelsCacheKey, results);
            return results;
        }

        #endregion

        #region Available Models

        public async Task<List<string>> GetAvailableModelsAsync(string toolId, CancellationToken ct = default)
        {
            var cacheKey = "available_" + toolId.ToLowerInvariant();
            if (_cache.TryGetValue(cacheKey, out List<string>? cached) && cached != null)
            {
                _logger.LogDebug("可用模型列表命中缓存: {Key}", cacheKey);
                return cached;
            }

            List<string> result;
            if (toolId.Equals("openvino", StringComparison.OrdinalIgnoreCase))
                result = await _openVino.GetAvailableModelsAsync(ct);
            else
                result = new List<string>();

            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(300));
            return result;
        }

        #endregion

        #region Downloaded Model Management

        public async Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(CancellationToken ct = default)
        {
            if (_cache.TryGetValue(DownloadedModelsCacheKey, out List<DownloadedModelDto>? cached) && cached != null)
            {
                _logger.LogDebug("已下载模型命中缓存");
                return cached;
            }

            var results = new List<DownloadedModelDto>();

            try
            {
                results.AddRange(await _openVino.GetDownloadedModelsAsync(ct));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 OpenVINO 已下载模型失败");
            }

            _cache.Set(DownloadedModelsCacheKey, results, TimeSpan.FromSeconds(300));
            return results;
        }

        public async Task<bool> DeleteModelAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            if (toolId.Equals("openvino", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("OpenVINO 模型删除暂不支持，请手动删除模型目录");
                return false;
            }

            return false;
        }

        public async Task<ModelDetailsDto?> GetModelDetailsAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            if (toolId.Equals("openvino", StringComparison.OrdinalIgnoreCase))
                return await _openVino.GetModelDetailsAsync(modelName, ct);

            return null;
        }

        #endregion

}
