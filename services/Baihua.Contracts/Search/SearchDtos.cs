namespace Baihua.Contracts.Search;

public class SearchResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    public string Preview { get; set; } = "";
    public int Score { get; set; }
}

public class SearchStatusInfo
{
    public bool VaultConfigured { get; set; }
    public bool VaultExists { get; set; }
    public string SearchMethod { get; set; } = "unknown";
    public string? ErrorMessage { get; set; }
}

public class SearchResponse
{
    public List<SearchResult> Results { get; set; } = new();
    public SearchStatusInfo Status { get; set; } = new();
}

public class IndexStatusDto
{
    public string VaultId { get; set; } = "";
    public int IndexedCount { get; set; }
    public bool HasIndex { get; set; }
}
