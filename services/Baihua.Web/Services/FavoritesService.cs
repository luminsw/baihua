using System.Text.Json;

namespace Baihua.Web.Services;

public interface IFavoritesService
{
    Task AddFavorite(string path, string title);
    Task<List<FavoriteItem>> GetFavorites();
    Task RemoveFavorite(string path);
    Task ClearFavorites();
    Task<bool> IsFavorite(string path);
}

public class FavoriteItem
{
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
}

public class FavoritesService : IFavoritesService
{
    private const string StorageKey = "favorites";
    private readonly string _storagePath;
    private List<FavoriteItem> _items = new();
    private readonly ILogger<FavoritesService> _logger;

    public FavoritesService(ILogger<FavoritesService> logger)
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
                _items = JsonSerializer.Deserialize<List<FavoriteItem>>(json) ?? new List<FavoriteItem>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载收藏失败");
            _items = new List<FavoriteItem>();
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
            _logger.LogWarning(ex, "保存收藏失败");
        }
    }

    public Task AddFavorite(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title))
            return Task.CompletedTask;

        // 如果已存在，不重复添加
        if (_items.Any(i => i.Path == path))
            return Task.CompletedTask;

        var category = ExtractCategory(path);

        _items.Add(new FavoriteItem
        {
            Path = path,
            Title = title,
            Category = category,
            AddedAt = DateTime.Now
        });

        Save();
        return Task.CompletedTask;
    }

    public Task<List<FavoriteItem>> GetFavorites()
    {
        return Task.FromResult(_items.OrderByDescending(i => i.AddedAt).ToList());
    }

    public Task RemoveFavorite(string path)
    {
        _items.RemoveAll(i => i.Path == path);
        Save();
        return Task.CompletedTask;
    }

    public Task ClearFavorites()
    {
        _items.Clear();
        Save();
        return Task.CompletedTask;
    }

    public Task<bool> IsFavorite(string path)
    {
        return Task.FromResult(_items.Any(i => i.Path == path));
    }

    private static string ExtractCategory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "未分类";

        var parts = path.Split('/');
        if (parts.Length > 1)
        {
            return parts[0];
        }

        return "未分类";
    }
}
