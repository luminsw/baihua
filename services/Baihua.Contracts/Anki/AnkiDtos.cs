namespace Baihua.Contracts.Anki;

public class CardItemDto
{
    public string Id { get; set; } = "";
    public string Deck { get; set; } = "";
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string Source { get; set; } = "";
}

public class DailyCardResultDto
{
    public bool HasCard { get; set; }
    public string Message { get; set; } = "";
    public CardItemDto? Card { get; set; }
    public DailyProgressDto? TodayProgress { get; set; }
    public int Remaining { get; set; }
}

public class DailyProgressDto
{
    public int Completed { get; set; }
    public int Target { get; set; } = 5;
    public int TotalCards { get; set; }
    public int Streak { get; set; }
}

public class CustomCardRequestDto
{
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public string? Deck { get; set; }
    public List<string>? Tags { get; set; }
}

public class DailyAnswerRequestDto
{
    public string CardId { get; set; } = "";
    public string Result { get; set; } = "";
}

public class StudiedCardDto
{
    public CardItemDto Card { get; set; } = new();
    public string Result { get; set; } = "";
    public string Date { get; set; } = "";
}

public class BatchGenerateRequestDto
{
    public string Directory { get; set; } = "";
    public bool Recursive { get; set; } = true;
}

public class BatchGenerateResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int TotalCards { get; set; }
}

public class AnkiSearchResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<CardItemDto> Cards { get; set; } = new();
    public int Total { get; set; }
    public string Query { get; set; } = "";
}

public class DeckListResult
{
    public List<DeckInfo> Decks { get; set; } = new();
}

public class DeckInfo
{
    public string Name { get; set; } = "";
    public int CardCount { get; set; }
    public List<string> Sources { get; set; } = new();
}

public class JsonDeckData
{
    public string? Name { get; set; }
    public List<JsonCard>? Cards { get; set; }
}

public class JsonCard
{
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}

public class GenerateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int CardCount { get; set; }
}

public class BatchGenerateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int TotalCards { get; set; }
    public List<GenerateResult>? Results { get; set; }
}
