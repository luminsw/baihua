using System.Text.Json;

namespace Baihua.Web.Services;

public interface ISearchHistoryService
{
    Task AddSearchQuery(string query);
    Task<List<string>> GetRecentSearches();
    Task ClearSearchHistory();
    Task RemoveSearchQuery(string query);
}

public class SearchHistoryService : ISearchHistoryService
{
    private const int MaxItems = 10;
    private const string StorageKey = "search_history";
    private readonly string _storagePath;
    private List<string> _queries = new();
    private readonly ILogger<SearchHistoryService> _logger;

    public SearchHistoryService(ILogger<SearchHistoryService> logger)
    {
        _logger = logger;
        var appDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        Directory.CreateDirectory(appDataPath);
        _storagePath = Path.Combine(appDataPath, $"{StorageKey}.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _queries = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载搜索历史失败");
            _queries = new List<string>();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_queries, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存搜索历史失败");
        }
    }

    public Task AddSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.CompletedTask;

        query = query.Trim();

        // 去重
        if (_queries.Contains(query))
        {
            _queries.Remove(query);
        }

        // 添加到开头
        _queries.Insert(0, query);

        // 限制数量
        if (_queries.Count > MaxItems)
        {
            _queries = _queries.Take(MaxItems).ToList();
        }

        Save();
        return Task.CompletedTask;
    }

    public Task<List<string>> GetRecentSearches()
    {
        return Task.FromResult(_queries.Take(MaxItems).ToList());
    }

    public Task ClearSearchHistory()
    {
        _queries.Clear();
        Save();
        return Task.CompletedTask;
    }

    public Task RemoveSearchQuery(string query)
    {
        _queries.Remove(query);
        Save();
        return Task.CompletedTask;
    }
}
