using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Baihua.Contracts.LocalModels;
using Baihua.Contracts.OpenClaw;
using Baihua.Data.Entities;
using Baihua.Family.Models;

namespace Baihua.Family.Services;

public partial class LocalModelDeploymentService
{
        #region Model Lifecycle

        public async Task<bool> LoadModelAsync(string toolId, string modelName, int keepAliveMinutes, CancellationToken ct = default)
        {
            if (toolId.Equals("openvino", StringComparison.OrdinalIgnoreCase))
            {
                await _openVino.EnsureServerRunningAsync(ct);
                var result = await _openVino.LoadModelAsync(modelName, ct);
                if (result) InvalidateCaches();
                return result;
            }

            return false;
        }

        public async Task<bool> UnloadModelAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            if (toolId.Equals("openvino", StringComparison.OrdinalIgnoreCase))
            {
                var result = await _openVino.UnloadModelAsync(modelName, ct);
                if (result) InvalidateCaches();
                return result;
            }

            return false;
        }

        #endregion

        #region Download Sources

        public List<DownloadSourceDto> GetDownloadSources()
        {
            return new List<DownloadSourceDto>
            {
                new()
                {
                    Id = "huggingface",
                    Name = "Hugging Face",
                    BaseUrl = "https://huggingface.co",
                    IsChinaMirror = false,
                    IsAvailable = true
                },
                new()
                {
                    Id = "hf-mirror",
                    Name = "Hugging Face Mirror (hf-mirror.com)",
                    BaseUrl = "https://hf-mirror.com",
                    IsChinaMirror = true,
                    IsAvailable = true
                },
                new()
                {
                    Id = "modelscope",
                    Name = "ModelScope Community",
                    BaseUrl = "https://modelscope.cn",
                    IsChinaMirror = true,
                    IsAvailable = true
                }
            };
        }

        #endregion

        #region Cache Management

        private void InvalidateCaches()
        {
            _cache.Remove(RunningModelsCacheKey);
            _cache.Remove(ToolsCacheKey);
            _cache.Remove(DownloadedModelsCacheKey);
            _cache.Remove("available_openvino");
            _logger.LogDebug("本地模型缓存已清除");
        }

        #endregion
}
