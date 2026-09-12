using System.Diagnostics;

namespace Baihua.Modules.Family.Services
{
    /// <summary>
    /// 服务启动监控：记录启动时间和重启次数
    /// </summary>
    public class StartupMonitor
    {
        private static StartupMonitor? _instance;
        private static readonly object _lock = new();
        
        public DateTime StartTime { get; private set; }
        public int RestartCount { get; private set; }
        public List<DateTime> RestartHistory { get; private set; }
        public string LogFilePath { get; private set; }
        
        private StartupMonitor()
        {
            StartTime = DateTime.UtcNow;
            RestartCount = 0;
            RestartHistory = new List<DateTime>();
            // 使用 BaihuaPaths.Logs 作为日志目录
            var logDir = Baihua.Contracts.BaihuaPaths.Logs;
            Directory.CreateDirectory(logDir);
            LogFilePath = Path.Combine(logDir, "startup.log");
        }
        
        public static StartupMonitor Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new StartupMonitor();
                    }
                }
                return _instance;
            }
        }
        
        public void RecordStartup()
        {
            var now = DateTime.Now;
            
            // 读取之前的启动记录
            LoadHistory();

            // Ensure log directory exists
            try
            {
                var dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
            
            // 如果距离上次启动不到 1 分钟，认为是重启
            if (RestartHistory.Count > 0)
            {
                var lastStart = RestartHistory.Last();
                var timeSinceLast = now - lastStart;
                if (timeSinceLast.TotalMinutes < 1)
                {
                    RestartCount++;
                    File.AppendAllText(LogFilePath, 
                        $"[{now:yyyy-MM-dd HH:mm:ss}] ⚠️ 检测到快速重启！距离上次启动: {timeSinceLast.TotalSeconds:F1} 秒，重启次数: {RestartCount}\n");
                }
            }
            
            RestartHistory.Add(now);
            StartTime = now;
            
            // 只保留最近 100 次启动记录
            if (RestartHistory.Count > 100)
            {
                RestartHistory = RestartHistory.TakeLast(100).ToList();
            }
            
            // 保存到文件
            SaveHistory();

            // 记录启动日志 (确保目录已创建)
            try
            {
                File.AppendAllText(LogFilePath, $"[{now:yyyy-MM-dd HH:mm:ss}] Service started, PID: {Environment.ProcessId}\n");
            }
            catch { }
        }
        
        private void LoadHistory()
        {
            try
            {
                if (File.Exists(LogFilePath))
                {
                    var lines = File.ReadAllLines(LogFilePath);
                    foreach (var line in lines)
                    {
                        if (line.Contains("服务启动"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]");
                            if (match.Success && DateTime.TryParse(match.Groups[1].Value, out var dt))
                            {
                                if (!RestartHistory.Contains(dt))
                                {
                                    RestartHistory.Add(dt);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // 忽略读取错误
            }
        }
        
        private void SaveHistory()
        {
            try
            {
                var historyFile = Path.Combine(Path.GetDirectoryName(LogFilePath) ?? AppDomain.CurrentDomain.BaseDirectory, "startup_history.json");
                var historyDir = Path.GetDirectoryName(historyFile);
                if (!string.IsNullOrEmpty(historyDir)) Directory.CreateDirectory(historyDir);
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    StartTime,
                    RestartCount,
                    RestartHistory,
                    LastUpdate = DateTime.UtcNow
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(historyFile, json);
            }
            catch
            {
                // 忽略保存错误
            }
        }
        
        public string GetStatusReport()
        {
            var uptime = DateTime.Now - StartTime;
            var recentRestarts = RestartHistory
                .Where(t => (DateTime.Now - t).TotalMinutes < 10)
                .Count();

            return string.Format("Service uptime: {0}", uptime.ToString(@"hh\:mm\:ss")) + "\n" +
                   string.Format("Start time: {0}", StartTime.ToString("yyyy-MM-dd HH:mm:ss")) + "\n" +
                   string.Format("Restart count: {0}", RestartCount) + "\n" +
                   string.Format("Recent restarts (10 min): {0}", recentRestarts) + "\n" +
                   string.Format("PID: {0}", Environment.ProcessId);
        }
    }
}