namespace Baihua.Contracts.Anki;

public class GenerateRequest
{
    public string NotePath { get; set; } = "";
}

public class BatchGenerateRequest
{
    public string Directory { get; set; } = "";
    public bool Recursive { get; set; } = true;
}

public class AiGenerateRequest
{
    public string NotePath { get; set; } = "";
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
}

public class StudyRequest
{
    public string? CardFront { get; set; }
    public string? CardBack { get; set; }
    public string? Deck { get; set; }
    public string? Result { get; set; }
}

public class StudyStats
{
    public int Total { get; set; }
    public int Remembered { get; set; }
    public int Normal { get; set; }
    public int Forgot { get; set; }
    public List<StudyRecord>? History { get; set; }
}

public class StudyRecord
{
    public string CardFront { get; set; } = "";
    public string CardBack { get; set; } = "";
    public string Deck { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTime Time { get; set; }
}

public class StudyStatsResponse
{
    public StudyStats? Today { get; set; }
    public StudySummary? Summary { get; set; }
    public List<DailyStats> DailyStats { get; set; } = new();
}

public class StudySummary
{
    public int TotalCards { get; set; }
    public int Remembered { get; set; }
    public int Forgot { get; set; }
    public double Accuracy { get; set; }
    public int Streak { get; set; }
}

public class DailyStats
{
    public string Date { get; set; } = "";
    public int Total { get; set; }
    public int Remembered { get; set; }
    public int Forgot { get; set; }
    public int Normal { get; set; }
}