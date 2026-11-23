namespace Baihua.Data.Entities;

/// <summary>
/// Onboarding 完成状态
/// </summary>
public class OnboardingState
{
    public int Id { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
