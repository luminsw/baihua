using Baihua.Contracts.Learn;

namespace Baihua.Modules.Family.Services;

/// <summary>生成进度（每批完成时上报）</summary>
public sealed record QuizGenerateProgress(int CompletedBatches, int TotalBatches, string Message);

/// <summary>
/// 题库生成器抽象：后台任务通过它执行生成，便于单元测试注入 fake。
/// </summary>
public interface IQuizBankGenerator
{
    Task<QuizGeneratePreviewDto> PreviewAsync(
        QuizGenerateRequestDto request,
        IProgress<QuizGenerateProgress>? progress,
        CancellationToken ct = default);
}