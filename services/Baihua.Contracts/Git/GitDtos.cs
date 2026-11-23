namespace Baihua.Contracts.Git;

public class GitStatusResponse
{
    public bool IsGitRepo { get; set; }
    public string Branch { get; set; } = "";
    public bool HasRemote { get; set; }
    public List<ChangeResponse> Changes { get; set; } = new();
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public string Message { get; set; } = "";
}

public class ChangeResponse
{
    public string Status { get; set; } = "";
    public string File { get; set; } = "";
}

public class GitResultResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? Output { get; set; }
}

public class CommitResponse
{
    public string Hash { get; set; } = "";
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
}

public class CommitRequest
{
    public string Message { get; set; } = "";
}
