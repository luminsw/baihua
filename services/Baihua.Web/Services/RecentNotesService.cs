using System.Text.Json;

namespace Baihua.Web.Services;

public interface IRecentNotesService
{
    Task AddRecentNote(string path, string title);
    Task<List<RecentNoteItem>> GetRecentNotes();
    Task ClearRecentNotes();
}

public class RecentNoteItem
{
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
}

public class RecentNotesService : IRecentNotesService
{
    private const int MaxItems = 10;
    private readonly string _storagePath;
    private List<RecentNoteItem> _items = new();
    private readonly ILogger<RecentNotesService> _logger;

    public RecentNotesService(ILogger<RecentNotesService> logger)
    {
        _logger = logger;
        // 使用应用数据目录存储
        var appDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        Directory.CreateDirectory(appDataPath);
        _storagePath = Path.Combine(appDataPath, "recent_notes.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _items = JsonSerializer.Deserialize<List<RecentNoteItem>>(json) ?? new List<RecentNoteItem>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载最近笔记失败");
            _items = new List<RecentNoteItem>();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存最近笔记失败");
        }
    }

    public Task AddRecentNote(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title))
            return Task.CompletedTask;

        // 去重：移除已存在的相同路径
        var existing = _items.FirstOrDefault(i => i.Path == path);
        if (existing != null)
        {
            _items.Remove(existing);
        }

        // 提取分类
        var category = ExtractCategory(path);

        // 添加到开头
        _items.Insert(0, new RecentNoteItem
        {
            Path = path,
            Title = title,
            Category = category,
            OpenedAt = DateTime.Now
        });

        // 限制数量
        if (_items.Count > MaxItems)
        {
            _items = _items.Take(MaxItems).ToList();
        }

        Save();
        return Task.CompletedTask;
    }

    public Task<List<RecentNoteItem>> GetRecentNotes()
    {
        return Task.FromResult(_items.OrderByDescending(i => i.OpenedAt).Take(MaxItems).ToList());
    }

    public Task ClearRecentNotes()
    {
        _items.Clear();
        Save();
        return Task.CompletedTask;
    }

    private static string ExtractCategory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "未分类";

        // 路径格式：分类/名称 或 一级/二级/名称
        var parts = path.Split('/');
        if (parts.Length > 1)
        {
            return parts[0];
        }

        return "未分类";
    }
}
