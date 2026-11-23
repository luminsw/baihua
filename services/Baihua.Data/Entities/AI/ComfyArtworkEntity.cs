using System.ComponentModel.DataAnnotations;

namespace Baihua.Data.Entities;

/// <summary>
/// AI 绘图（ComfyUI）生成记录
/// </summary>
public class ComfyArtworkEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// 生成时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 生成类型：image / video
    /// </summary>
    public string Kind { get; set; } = "image";

    /// <summary>
    /// 用户输入的提示词
    /// </summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// 使用的模型文件
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// 参数 JSON（width/height/steps/seed 等）
    /// </summary>
    public string ParamsJson { get; set; } = "{}";

    /// <summary>
    /// 生成的文件名（ComfyUI output 里的）
    /// </summary>
    public string FileName { get; set; } = "";

    /// <summary>
    /// 文件子目录（通常为空）
    /// </summary>
    public string Subfolder { get; set; } = "";

    /// <summary>
    /// 文件类型：output / temp
    /// </summary>
    public string FileType { get; set; } = "output";

    /// <summary>
    /// ComfyUI prompt_id
    /// </summary>
    public string PromptId { get; set; } = "";

    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess { get; set; } = true;

    /// <summary>
    /// 失败信息（成功时为空）
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 耗时（秒）
    /// </summary>
    public double DurationSeconds { get; set; }
}
