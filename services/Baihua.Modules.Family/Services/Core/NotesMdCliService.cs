using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using System.Diagnostics;
using System.Text.Json;

namespace Baihua.Modules.Family.Services
{
    /// <summary>
    /// notesmd-cli 集成服务，用于检测和管理 Obsidian vault 注册。
    /// </summary>
    public class NotesMdCliService
    {
        private readonly ILogger<NotesMdCliService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

        public NotesMdCliService(IStringLocalizer<SharedResources> loc,
        ILogger<NotesMdCliService> logger)
        {
            _loc = loc;
        _logger = logger;
        }

        /// <summary>
        /// 检测 notesmd-cli 是否可用。
        /// </summary>
        public bool IsAvailable()
        {
            try
            {
                var result = RunCli("--version");
                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "notesmd-cli 不可用");
                return false;
            }
        }

        /// <summary>
        /// 获取已注册的 vault 路径列表（从 obsidian.json 解析，不依赖 CLI 的 JSON 输出格式）。
        /// </summary>
        public List<string> GetRegisteredVaultPaths()
        {
            var paths = new List<string>();
            try
            {
                var configPath = GetObsidianConfigPath();
                if (!File.Exists(configPath))
                    return paths;

                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("vaults", out var vaults))
                {
                    foreach (var property in vaults.EnumerateObject())
                    {
                        if (property.Value.TryGetProperty("path", out var pathElement))
                        {
                            var path = pathElement.GetString();
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                paths.Add(path);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 Obsidian vault 配置失败");
            }
            return paths;
        }

        /// <summary>
        /// 添加单个 vault 到 notesmd-cli。
        /// </summary>
        public bool AddVault(string path)
        {
            try
            {
                var result = RunCli("add-vault", path);
                if (result.ExitCode != 0)
                {
                    _logger.LogWarning("notesmd-cli add-vault 失败: {Stderr}", result.Stderr);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调用 notesmd-cli add-vault 失败");
                return false;
            }
        }

        /// <summary>
        /// 批量添加 vaults，返回成功和失败的列表。
        /// </summary>
        public (List<string> succeeded, List<string> failed) BatchAddVaults(IEnumerable<string> paths)
        {
            var succeeded = new List<string>();
            var failed = new List<string>();
            foreach (var path in paths)
            {
                if (AddVault(path))
                    succeeded.Add(path);
                else
                    failed.Add(path);
            }
            return (succeeded, failed);
        }

        private static string GetObsidianConfigPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(home, "AppData", "Roaming", "obsidian", "obsidian.json");
            }
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(home, "Library", "Application Support", "obsidian", "obsidian.json");
            }
            // Linux
            return Path.Combine(home, ".config", "obsidian", "obsidian.json");
        }

        private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "notesmd-cli",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 notesmd-cli 进程");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode, stdout, stderr);
        }
    }
}
