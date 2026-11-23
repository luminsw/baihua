using System.Text.Json;

namespace Baihua.Web.Services;

public interface IUserPreferencesService
{
    Task<UserPreferences> GetPreferences();
    Task SavePreferences(UserPreferences preferences);
    Task<double> GetFontSize();
    Task SaveFontSize(double size);
}

public class UserPreferences
{
    public double FontSize { get; set; } = 16.0; // 默认字体大小（px）
    public string Theme { get; set; } = "light"; // light/dark
    public bool AutoSave { get; set; } = true;
}

public class UserPreferencesService : IUserPreferencesService
{
    private const string StorageKey = "user_preferences";
    private readonly string _storagePath;
    private UserPreferences _preferences = new();
    private readonly ILogger<UserPreferencesService> _logger;

    public UserPreferencesService(ILogger<UserPreferencesService> logger)
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
                _preferences = JsonSerializer.Deserialize<UserPreferences>(json) ?? new UserPreferences();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载用户偏好失败");
            _preferences = new UserPreferences();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_preferences, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存用户偏好失败");
        }
    }

    public Task<UserPreferences> GetPreferences()
    {
        return Task.FromResult(_preferences);
    }

    public Task SavePreferences(UserPreferences preferences)
    {
        _preferences = preferences;
        Save();
        return Task.CompletedTask;
    }

    public Task<double> GetFontSize()
    {
        return Task.FromResult(_preferences.FontSize);
    }

    public Task SaveFontSize(double size)
    {
        _preferences.FontSize = size;
        Save();
        return Task.CompletedTask;
    }
}
