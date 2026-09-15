using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Core.Services
{
    /// <summary>
    /// 服务器地址配置服务
    /// </summary>
    public class ServerAddressService
    {
        private readonly IDbContextFactory<FamilyDbContext> _dbContextFactory;
        private readonly ILogger<ServerAddressService> _logger;
        private readonly IConfiguration _configuration;

        /// <summary>
        /// 首次初始化（生成服务器身份 + 共享密钥）必须串行。
        /// 启动期并发调用时，无锁的 check-then-insert 会插入两条身份记录、
        /// 生成两个不同的共享密钥 —— 移动端按其中一个配对、服务端按另一个验签，
        /// 表现为配对成功但所有带签名请求全部 401（花记看不到知识库列表）。
        /// </summary>
        private static readonly object _initLock = new();

        public ServerAddressService(
            IDbContextFactory<FamilyDbContext> dbContextFactory,
            ILogger<ServerAddressService> logger,
            IConfiguration configuration)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>
        /// 获取服务器地址配置
        /// </summary>
        public ServerAddressSetting GetSettings()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            try
            {
                lock (_initLock)
                {
                    return GetOrCreateSettings(dbContext);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取服务器地址配置失败，返回默认配置");
                return new ServerAddressSetting { Domain = "", Url = "" };
            }
        }

        private ServerAddressSetting GetOrCreateSettings(FamilyDbContext dbContext)
        {
            var setting = dbContext.ServerAddressSettings.OrderBy(s => s.Id).FirstOrDefault();
            if (setting == null)
            {
                setting = new ServerAddressSetting
                {
                    Domain = "",
                    Url = "",
                    ServerInstanceId = GenerateServerInstanceId(),
                    DisplayName = GenerateCulturalDisplayName()
                };
                dbContext.ServerAddressSettings.Add(setting);
                dbContext.SaveChanges();
            }
            else if (string.IsNullOrWhiteSpace(setting.ServerInstanceId))
            {
                setting.ServerInstanceId = GenerateServerInstanceId();
                dbContext.SaveChanges();
            }
            else if (string.IsNullOrWhiteSpace(setting.DisplayName))
            {
                setting.DisplayName = GenerateCulturalDisplayName();
                dbContext.SaveChanges();
            }

            if (string.IsNullOrWhiteSpace(setting.SharedSecret))
            {
                setting.SharedSecret = GenerateSharedSecret();
                dbContext.SaveChanges();
            }
            return setting;
        }

        /// <summary>
        /// 更新服务器地址配置（使用域名）
        /// </summary>
        public async Task<ServerAddressSetting> UpdateSettings(string? domain, string? displayName = null)
        {
            var normalizedDomain = NormalizeDomain(domain ?? "");

            using var dbContext = _dbContextFactory.CreateDbContext();
            try
            {
                var setting = dbContext.ServerAddressSettings.OrderBy(s => s.Id).FirstOrDefault();
                if (setting == null)
                {
                    setting = new ServerAddressSetting
                    {
                        Domain = normalizedDomain,
                        DisplayName = displayName ?? GenerateCulturalDisplayName(),
                        Url = "",
                        ServerInstanceId = GenerateServerInstanceId(),
                        SharedSecret = GenerateSharedSecret()
                    };
                    dbContext.ServerAddressSettings.Add(setting);
                }
                else
                {
                    setting.Domain = normalizedDomain;
                    if (displayName != null)
                    {
                        setting.DisplayName = displayName;
                    }
                }

                await dbContext.SaveChangesAsync();
                _logger.LogInformation("服务器地址配置已更新: Domain={Domain}", setting.Domain);

                return setting;
            }
            catch (Exception ex)
            {
                // PostgreSQL：schema 由 EF EnsureCreated 统一创建，不再用裸 SQL 兜底
                _logger.LogError(ex, "更新服务器地址配置失败");
                throw;
            }
        }

        /// <summary>
        /// 获取服务器实例唯一标识
        /// </summary>
        public string GetServerInstanceId()
        {
            return GetSettings().ServerInstanceId;
        }

        public string GetSharedSecret()
        {
            return GetSettings().SharedSecret;
        }

        /// <summary>
        /// 生成服务器实例唯一标识
        /// </summary>
        private static string GenerateServerInstanceId()
        {
            return $"srv-{Guid.NewGuid():N}";
        }

        private static string GenerateSharedSecret()
        {
            return $"sec-{Guid.NewGuid():N}";
        }

        private static readonly string[] CulturalNamePrefixes =
        [
            "听风", "望月", "拾光", "寻芳", "踏雪", "观云", "沐雨", "临水",
            "知秋", "迎春", "听雨", "看花", "折柳", "采菊", "抚琴", "煮茶",
            "清心", "静思", "悠然", "闲云", "素心", "雅韵", "墨香", "竹影",
            "松风", "梅骨", "兰心", "菊韵", "荷香", "桃夭", "杏雨", "梨云"
        ];

        private static readonly string[] CulturalNameSuffixes =
        [
            "阁", "轩", "斋", "居", "堂", "舍", "庐", "苑",
            "院", "楼", "亭", "台", "馆", "室", "房", "庄"
        ];

        private static string GenerateCulturalDisplayName()
        {
            var prefix = CulturalNamePrefixes[Random.Shared.Next(CulturalNamePrefixes.Length)];
            var suffix = CulturalNameSuffixes[Random.Shared.Next(CulturalNameSuffixes.Length)];
            return $"{prefix}{suffix}";
        }

        /// <summary>
        /// 获取用于二维码的服务器地址
        /// 如果配置了域名则使用 https://域名（广域网）
        /// 否则自动生成局域网地址（http://IP:端口）
        /// </summary>
        public (string url, string hostName) GetQrCodeAddresses()
        {
            try
            {
                var settings = GetSettings();
                // 优先使用配置的 DisplayName，否则使用系统 hostname
                var hostName = !string.IsNullOrWhiteSpace(settings.DisplayName)
                    ? settings.DisplayName
                    : System.Net.Dns.GetHostName();

                // 优先使用配置的 PublicBaseUrl（Nginx 统一入口地址，如 http://192.168.1.5 或 https://mydomain.com）
                var publicBase = _configuration["Baihua:PublicBaseUrl"];
                if (!string.IsNullOrWhiteSpace(publicBase))
                    return (publicBase.TrimEnd('/'), hostName);

                // 优先使用 Domain（广域网 HTTPS）
                if (!string.IsNullOrWhiteSpace(settings.Domain))
                {
                    var domain = NormalizeDomain(settings.Domain);
                    return ($"https://{domain}", hostName);
                }

                // 局域网：自动生成 HTTP 地址
                var localIp = GetLocalIpAddress();
                int httpPort = GetHttpPort();

                return ($"http://{localIp}:{httpPort}", hostName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取二维码地址失败，使用默认地址");
                var localIp = GetLocalIpAddress();
                var displayName = "";
                try { displayName = GetSettings().DisplayName ?? ""; } catch { }
                var hostName = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : System.Net.Dns.GetHostName();
                return ($"http://{localIp}:8788", hostName);
            }
        }

        /// <summary>
        /// 获取配置的 HTTP 端口
        /// </summary>
        private int GetHttpPort()
        {
            var configuredHttpUrl = _configuration["Kestrel:Endpoints:Http:Url"];
            if (!string.IsNullOrWhiteSpace(configuredHttpUrl) &&
                Uri.TryCreate(configuredHttpUrl, UriKind.Absolute, out var uri))
            {
                _logger.LogDebug("从配置中获取HTTP端口: {Port}", uri.Port);
                return uri.Port;
            }
            _logger.LogDebug("使用默认HTTP端口: 8788");
            return 8788;
        }

        /// <summary>
        /// 规范化域名：去掉协议前缀、路径、端口
        /// </summary>
        private static string NormalizeDomain(string domain)
        {
            domain = domain.Trim();
            if (string.IsNullOrEmpty(domain))
                return "";

            // 去掉协议前缀
            if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                domain = domain.Substring(7);
            else if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                domain = domain.Substring(8);

            // 去掉尾部斜杠和路径
            var slashIndex = domain.IndexOf('/');
            if (slashIndex >= 0)
                domain = domain.Substring(0, slashIndex);

            // 去掉端口（简单处理，IPv6 暂不支持）
            var colonIndex = domain.LastIndexOf(':');
            if (colonIndex > 0)
                domain = domain.Substring(0, colonIndex);

            return domain.Trim();
        }

        /// <summary>
        /// 获取本机局域网IP地址
        /// </summary>
        private string GetLocalIpAddress()
        {
            try
            {
                var hostName = System.Net.Dns.GetHostName();
                var addresses = System.Net.Dns.GetHostAddresses(hostName);

                // 优先选择非回环的IPv4地址
                foreach (var address in addresses)
                {
                    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(address))
                    {
                        return address.ToString();
                    }
                }

                return "127.0.0.1";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取本机IP失败");
                return "127.0.0.1";
            }
        }

        /// <summary>
        /// 自动探测本机局域网 HTTP 入口（native 部署用）：本机 IP + Kestrel 监听端口。
        /// 供服务器互联广播/回发使用，避免手动配置自己的 IP。
        /// </summary>
        public string GetLocalPublicBaseUrl()
        {
            var ip = GetLocalIpAddress();
            var port = GetHttpPort();
            return $"http://{ip}:{port}";
        }
    }
}
