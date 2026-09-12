using Baihua.Core.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Health;
using Baihua.Modules.Family.Services;
using Baihua.AI.Provider;

namespace Baihua.Modules.Family.Controllers;

public partial class HealthController
{
        [HttpPost("fix")]
        public async Task<ActionResult<HealthFixResultDto>> FixIssues(CancellationToken cancellationToken)
        {
            var result = new HealthFixResultDto();
            var fixes = new List<HealthFixItemDto>();

            try
            {
                // 先获取当前健康报告
                var report = await _healthService.GetHealthReportAsync(cancellationToken);

                foreach (var component in report.Components)
                {
                    if (component.Status == "healthy")
                    {
                        fixes.Add(new HealthFixItemDto
                        {
                            Component = component.Name,
                            Status = "skipped",
                            Message = _loc["Health_Fix_StatusNormal"]
                        });
                        continue;
                    }

                    switch (component.Name)
                    {
                        case "Ollama":
                            // 尝试启动 Ollama
                            try
                            {
                                var config = await _localAiConfig.GetLocalAiConfigAsync();
                                var ollamaUrl = config.Ollama?.BaseUrl ?? "http://localhost:11434";
                                var started = await _localAiAutoStarter.TryEnsureRunningAsync("ollama", ollamaUrl);
                                fixes.Add(new HealthFixItemDto
                                {
                                    Component = component.Name,
                                    Status = started ? "fixed" : "failed",
                                    Message = started ? _loc["Health_Fix_OllamaStarted"] : _loc["Health_Fix_OllamaFailed"]
                                });
                            }
                            catch (Exception ex)
                            {
                                fixes.Add(new HealthFixItemDto
                                {
                                    Component = component.Name,
                                    Status = "failed",
                                    Message = _loc["Health_Fix_StartFailed", ex.Message]
                                });
                            }
                            break;

                        case "Git":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = _loc["Health_Fix_Git"]
                            });
                            break;

                        case "Python":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = _loc["Health_Fix_Python"]
                            });
                            break;

                        case "Node.js":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = _loc["Health_Fix_NodeJs"]
                            });
                            break;

                        case "PIP":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = _loc["Health_Fix_Pip"]
                            });
                            break;

                        case "API Key":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = _loc["Health_Fix_ApiKey"]
                            });
                            break;

                        case "知识库":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = _loc["Health_Fix_Vault"]
                            });
                            break;

                        case "Obsidian":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = _loc["Health_Fix_Obsidian"]
                            });
                            break;

                        default:
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "skipped",
                                Message = _loc["Health_Fix_NotSupported"]
                            });
                            break;
                    }
                }

                // 重新检测
                var newReport = await _healthService.GetHealthReportAsync(cancellationToken);
                result.Success = newReport.Status != "critical";
                result.Message = _loc["Health_Fix_Completed", newReport.HealthScore, fixes.Count(f => f.Status == "manual_required")];
                result.Fixes = fixes;
                result.NewReport = newReport;

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "一键修复失败");
                return StatusCode(500, new HealthFixResultDto
                {
                    Success = false,
                    Message = _loc["Health_Fix_Failed", ex.Message],
                    Fixes = fixes
                });
            }
        }

}
