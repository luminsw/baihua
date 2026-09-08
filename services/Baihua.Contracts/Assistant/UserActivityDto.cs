namespace Baihua.Contracts.Assistant;

public class UserActivityDto
{
    public DateTime Time { get; set; }
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public int Length { get; set; }
}