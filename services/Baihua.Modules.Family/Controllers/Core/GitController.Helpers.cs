using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Git;

namespace Baihua.Modules.Family.Controllers;

public partial class GitController
{
        private async Task<string> RunGitCommand(string vaultPath, string args, int timeoutMs = 10000)
        {
            if (string.IsNullOrEmpty(vaultPath))
            {
                throw new InvalidOperationException(_loc["Git_VaultPathNotConfigured"]);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = vaultPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (s, e) => 
            { 
                try { if (e.Data != null) output.AppendLine(e.Data); } catch { } 
            };
            process.ErrorDataReceived += (s, e) => 
            { 
                try { if (e.Data != null) error.AppendLine(e.Data); } catch { } 
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill();
                throw new TimeoutException(_loc["Git_CommandTimeout"]);
            }

            // 等待异步读取完成
            await Task.Delay(100);

            if (process.ExitCode != 0 && error.Length > 0)
            {
                throw new Exception(error.ToString().Trim());
            }

            return output.ToString().Trim();
        }

        /// <summary>
        /// 解码 Git 返回的八进制编码路径（如 \347\224\237 -> 生）
        /// Git 使用 UTF-8 字节的八进制表示
        /// </summary>
        private string DecodeGitOctalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            
            // Git 使用八进制编码 UTF-8 字节，格式如 \347\224\237
            // 需要将连续的 \xxx 转换为 UTF-8 字节，然后解码
            try
            {
                var bytes = new List<byte>();
                var i = 0;
                
                while (i < path.Length)
                {
                    if (path[i] == '\\' && i + 3 < path.Length)
                    {
                        // 收集连续的八进制编码
                        var byteList = new List<byte>();
                        while (i < path.Length && path[i] == '\\' && i + 3 < path.Length)
                        {
                            var octal = path.Substring(i + 1, 3);
                            try
                            {
                                var byteValue = Convert.ToByte(octal, 8);
                                byteList.Add(byteValue);
                                i += 4;
                            }
                            catch
                            {
                                break;
                            }
                        }
                        
                        // 将收集的 UTF-8 字节解码为字符串
                        if (byteList.Count > 0)
                        {
                            var decoded = Encoding.UTF8.GetString(byteList.ToArray());
                            bytes.AddRange(Encoding.UTF8.GetBytes(decoded));
                        }
                    }
                    else
                    {
                        // 普通 ASCII 字符
                        bytes.Add((byte)path[i]);
                        i++;
                    }
                }
                
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            catch
            {
                return path;
            }
        }

        private string MapGitStatus(string status)
        {
            return status switch
            {
                "M" or "MM" or " M" => "Modified",
                "A" or "AM" => "Added",
                "D" or " D" => "Deleted",
                "R" => "Renamed",
                "C" => "Copied",
                "??" => "Untracked",
                _ => "Unknown"
            };
        }
}
