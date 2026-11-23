using Baihua.Contracts;

namespace Baihua.Core;

/// <summary>
/// 本地 AI 模型存储配置（绑定配置节 LocalAI）
/// </summary>
public class LocalAiOptions
{
    /// <summary>模型下载/存储目录，为空则使用默认路径（百花数据目录下 models/，跟随 BAIHUA_HOME）</summary>
    public string DownloadDirectory { get; set; } = "";

    /// <summary>
    /// OpenVINO LLM / 视觉服务使用的 Python 可执行文件（绝对路径或命令名）。
    /// 留空时自动探测 PATH 中的 python/py/python3 及常见 Python 安装目录
    /// （能 import openvino_genai 的解释器）。对齐 LocalVision:PythonExe 的语义。
    /// </summary>
    public string? PythonExe { get; set; }

    /// <summary>
    /// 获取模型根目录：
    /// 1. 优先使用 DownloadDirectory 配置值
    /// 2. 未配置时回退到 $BAIHUA_HOME/models（与百花统一数据根目录一致）
    /// </summary>
    public string GetModelRoot()
    {
        if (!string.IsNullOrWhiteSpace(DownloadDirectory))
            return DownloadDirectory;
        return Path.Combine(BaihuaPaths.Home, "models");
    }
}
