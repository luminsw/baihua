using System.Diagnostics;
using System.Runtime.InteropServices;
using Baihua.Core.Security;
using ComponentStatus = Baihua.Contracts.Health.ComponentStatusDto;

namespace Baihua.Modules.Family.Services
{
    public partial class SystemHealthService
    {
        private async Task<ComponentStatus> CheckPythonAsync(CancellationToken cancellationToken)
        {
            // Windows 上优先尝试 py 启动器，然后是 python/python3
            var pythonCmds = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "py", "python", "python3" }
                : new[] { "python3", "python" };

            foreach (var cmd in pythonCmds)
            {
                Process? process = null;
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process is null) continue;

                    var (ok, exitCode, output) = await WaitForProcessAsync(process, 4000, cancellationToken);
                    if (ok && exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        return new ComponentStatus
                        {
                            Name = "Python",
                            Status = "healthy",
                            Version = HealthCheckHelper.ExtractVersion(output),
                            Message = string.Format(_loc["Health_PythonInstalled"], cmd)
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 尝试下一个命令
                    continue;
                }
            }

            return new ComponentStatus
            {
                Name = "Python",
                Status = "warning",
                Message = _loc["Health_PythonNotInstalled"]
            };
        }

        private async Task<ComponentStatus> CheckNodeAsync(CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process is null)
                {
                    return new ComponentStatus
                    {
                        Name = "Node.js",
                        Status = "warning",
                        Message = _loc["Health_NodeNotInstalled"]
                    };
                }

                var (ok, exitCode, output) = await WaitForProcessAsync(process, 4000, cancellationToken);
                if (!ok)
                    return new ComponentStatus { Name = "Node.js", Status = "critical", Message = _loc["Health_NodeTimeout"] };
                if (exitCode != 0)
                    return new ComponentStatus { Name = "Node.js", Status = "critical", Message = _loc["Health_NodeCheckFailed"] };

                return new ComponentStatus
                {
                    Name = "Node.js",
                    Status = "healthy",
                    Version = HealthCheckHelper.ExtractVersion(output),
                    Message = _loc["Health_NodeInstalled"]
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Node.js 检测失败");
                return new ComponentStatus
                {
                    Name = "Node.js",
                    Status = "warning",
                    Message = _loc["Health_NodeCheckError"]
                };
            }
        }

        private async Task<ComponentStatus> CheckPipAsync(CancellationToken cancellationToken)
        {
            // Windows 上优先尝试 py 启动器，然后是 python/python3
            var pythonCmds = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "py", "python", "python3" }
                : new[] { "python3", "python" };

            foreach (var cmd in pythonCmds)
            {
                Process? process = null;
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "-m pip --version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process is null) continue;

                    var (ok, exitCode, output) = await WaitForProcessAsync(process, 4000, cancellationToken);
                    if (ok && exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        return new ComponentStatus
                        {
                            Name = "PIP",
                            Status = "healthy",
                            Version = HealthCheckHelper.ExtractVersion(output),
                            Message = string.Format(_loc["Health_PipInstalled"], cmd)
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 尝试下一个命令
                    continue;
                }
            }

            return new ComponentStatus
            {
                Name = "PIP",
                Status = "warning",
                Message = _loc["Health_PipNotInstalled"]
            };
        }

        /// <summary>
        /// 检查 AI API Key 配置状态（一服务一数据库：Family 不持有明文 key，
        /// 仅依据 AI 服务提供的摘要（HasApiKey/KeyMask）判断）。
        /// </summary>
        private Task<ComponentStatus> CheckApiKeyAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var providers = _aiConfigService.GetProviders();
                if (providers.Count == 0)
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "API Key",
                        Status = "warning",
                        Message = _loc["Health_NoAiProvider"]
                    });
                }

                var mainProvider = providers.FirstOrDefault(p => p.IsMain) ?? providers.First();
                var isLocalProvider = HealthCheckHelper.IsLocalAiProvider(mainProvider);

                var summaries = _aiConfigService.GetApiKeySummaries();
                var summary = summaries.FirstOrDefault(s => 
                    s.ProviderId.Equals(mainProvider.Id, StringComparison.OrdinalIgnoreCase));
                var hasApiKey = summary?.HasApiKey == true;

                if (!hasApiKey)
                {
                    // 本地 AI 服务不需要 API Key
                    if (isLocalProvider)
                    {
                        return Task.FromResult(new ComponentStatus
                        {
                            Name = "API Key",
                            Status = "healthy",
                            Message = string.Format(_loc["Health_LocalProviderNoKey"], mainProvider.Name)
                        });
                    }

                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "API Key",
                        Status = "critical",
                        Message = string.Format(_loc["Health_MainProviderNoKey"], mainProvider.Name)
                    });
                }

                var scheme = summary!.Scheme switch
                {
                    EncryptionScheme.AesGcm => _loc["Health_AesEncrypted"],
                    _ => _loc["Health_Encrypted"]
                };
                return Task.FromResult(new ComponentStatus
                {
                    Name = "API Key",
                    Status = "healthy",
                    Message = string.Format(_loc["Health_ApiKeyConfigured"], mainProvider.Name, scheme)
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "API Key 检测失败");
                return Task.FromResult(new ComponentStatus
                {
                    Name = "API Key",
                    Status = "warning",
                    Message = _loc["Health_ApiKeyCheckError"]
                });
            }
        }

        /// <summary>
        /// 检查知识库路径配置状态
        /// </summary>
        private Task<ComponentStatus> CheckVaultPathAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var vaultPath = _vaultSettings.VaultPath;
                
                if (string.IsNullOrWhiteSpace(vaultPath))
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "Vault",
                        Status = "critical",
                        Message = _loc["Health_VaultPathNotConfigured"]
                    });
                }

                if (!Directory.Exists(vaultPath))
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "Vault",
                        Status = "critical",
                        Message = string.Format(_loc["Health_VaultPathNotExists"], vaultPath)
                    });
                }

                // 只要路径存在即视为有效，不再强制要求 .obsidian 目录或 .md 文件
                // 用户可以通过 WebUI 的"在 Obsidian 中打开"按钮来初始化该目录
                var hasObsidianDir = Directory.Exists(Path.Combine(vaultPath, ".obsidian"));
                var mdFiles = Directory.GetFiles(vaultPath, "*.md", SearchOption.TopDirectoryOnly);
                
                return Task.FromResult(new ComponentStatus
                {
                    Name = "Vault",
                    Status = "healthy",
                    Message = hasObsidianDir 
                        ? string.Format(_loc["Health_VaultConfigured"], Path.GetFileName(vaultPath), mdFiles.Length)
                        : string.Format(_loc["Health_VaultPathConfigured"], Path.GetFileName(vaultPath))
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "知识库路径检测失败");
                return Task.FromResult(new ComponentStatus
                {
                    Name = "Vault",
                    Status = "warning",
                    Message = _loc["Health_VaultCheckError"]
                });
            }
        }
    }
}

