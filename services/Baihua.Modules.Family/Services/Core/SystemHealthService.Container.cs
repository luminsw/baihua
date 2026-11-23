using ComponentStatus = Baihua.Contracts.Health.ComponentStatusDto;

namespace Baihua.Modules.Family.Services
{
    public partial class SystemHealthService
    {
        /// <summary>
        /// 最终用户部署（wslc / k8s 容器）不检测宿主开发工具：容器内没有 Git/Python/Node/PIP/Ollama，
        /// 缺失不代表系统不可用，不能把它们算成 critical/warning 把整体状态拉红。
        /// </summary>
        private static readonly bool IsContainerDeployment =
            string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase)
            || File.Exists("/.dockerenv");

        private ComponentStatus SkippedInContainer(string name) => new()
        {
            Name = name,
            Status = "skipped",
            Message = _loc["Health_SkippedInContainer"]
        };
    }
}