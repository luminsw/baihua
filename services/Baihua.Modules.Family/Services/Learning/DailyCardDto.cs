namespace Baihua.Modules.Family.Services;

public class DailyCardResult
{
    public bool HasCard { get; set; }
    public string Message { get; set; } = "";
    public CardItem? Card { get; set; }
    public DailyProgress? TodayProgress { get; set; }
    public int Remaining { get; set; }
    /// <summary>是否为复习卡片（已到期的旧卡片）</summary>
    public bool IsReview { get; set; }
}

public class CardItem
{
    public string Id { get; set; } = "";
    public string Deck { get; set; } = "";
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string Source { get; set; } = "";
}

public class DailyProgress
{
    public int Completed { get; set; }
    public int Target { get; set; } = 5;
    public int TotalCards { get; set; }
    public int Streak { get; set; }
}

public class CustomCardRequest
{
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public string? Deck { get; set; }
    public List<string>? Tags { get; set; }
}

public class StudiedCard
{
    public CardItem Card { get; set; } = new();
    public string Result { get; set; } = "";
    public string Date { get; set; } = "";
}

public class DailyRecord
{
    public int Completed { get; set; }
    public Dictionary<string, string> Answers { get; set; } = new();
}
