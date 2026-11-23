namespace Baihua.Web.Services;

/// <summary>
/// 搜索状态（用于页面导航后恢复搜索结果）
/// </summary>
public class SearchState
{
    public string Query { get; set; } = "";
    public List<SearchResult> Results { get; set; } = new();
    public HashSet<string> ExpandedGroups { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

/// <summary>
/// 搜索状态服务（临时保存搜索状态，支持返回时恢复）
/// </summary>
public class SearchStateService
{
    private SearchState? _state;
    private readonly TimeSpan _expiry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 保存搜索状态
    /// </summary>
    public void Save(string query, List<SearchResult> results, HashSet<string> expandedGroups)
    {
        _state = new SearchState
        {
            Query = query,
            Results = results,
            ExpandedGroups = new HashSet<string>(expandedGroups),
            SavedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 获取搜索状态（不清除，支持多次返回时恢复）
    /// </summary>
    public SearchState? Retrieve()
    {
        if (_state == null) return null;
        
        // 检查是否过期
        if (DateTime.UtcNow - _state.SavedAt > _expiry)
        {
            _state = null;
            return null;
        }
        
        return _state;
    }

    /// <summary>
    /// 清除搜索状态
    /// </summary>
    public void Clear()
    {
        _state = null;
    }

    /// <summary>
    /// 检查是否有保存的状态
    /// </summary>
    public bool HasState => _state != null && DateTime.UtcNow - _state.SavedAt <= _expiry;
}
