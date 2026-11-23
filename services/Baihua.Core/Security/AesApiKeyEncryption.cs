using System.Security.Cryptography;
using System.Text;

namespace Baihua.Core.Security;

/// <summary>
/// AES-256-GCM 加密方案 - 用于 API Key 的加密
///
/// 密钥管理策略：
/// 1. 首次启动时自动生成 256-bit 随机密钥，持久化到 db/.baihua-key
/// 2. 容器重建后从文件读取固定密钥，避免机器指纹变化导致无法解密
/// 3. 高级用户可通过 BAIHUA_ENCRYPTION_KEY 环境变量覆盖
/// 4. 支持自动迁移：检测到旧密钥（机器指纹）加密的 API Key 时，
///    用旧密钥解密后用新密钥重新加密
///
/// 安全特性：
/// - 使用 AES-256-GCM 提供认证加密（防篡改）
/// - 每次加密使用随机 IV，确保相同明文产生不同密文
/// - 密钥文件权限限制为 600（仅所有者可读写）
/// </summary>
public static class AesApiKeyEncryption
{
    private const byte Version = 0x01;
    private const int KeySize = 32;   // 256 bits
    private const int NonceSize = 12; // 96 bits for GCM
    private const int TagSize = 16;   // 128 bits for GCM

    /// <summary>
    /// 密钥文件路径（委托 BaihuaPaths.KeyFile）
    /// </summary>
    public static string KeyFilePath => Baihua.Contracts.BaihuaPaths.KeyFile;

    /// <summary>
    /// 机器指纹（用于密钥文件丢失时的兜底）
    ///
    /// 优先读取 OS 级机器 ID（重装系统前永不变化）：
    ///   Windows: HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid
    ///   Linux:   /etc/machine-id
    ///   macOS:   ioreg IOPlatformUUID
    ///
    /// 读不到时回退到 MachineName + ProcessorCount，保证至少同机同指纹。
    /// </summary>
    public static byte[] GetMachineFingerprint()
    {
        var stableId = ReadOsMachineId();
        if (!string.IsNullOrEmpty(stableId))
            return SHA256.HashData(Encoding.UTF8.GetBytes(stableId));

        var fallback = $"{Environment.MachineName}|{Environment.ProcessorCount}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(fallback));
    }

    /// <summary>
    /// 旧版机器指纹（仅用于迁移，保持与历史数据兼容）
    /// </summary>
    public static byte[] GetLegacyMachineFingerprint()
    {
        var components = new[]
        {
            Environment.UserName,
            AppDomain.CurrentDomain.BaseDirectory,
            Environment.OSVersion.ToString(),
            Environment.ProcessorCount.ToString()
        };
        var fingerprint = string.Join("|", components);
        return SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
    }

    private static string? ReadOsMachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid")?.ToString();
            }
            if (OperatingSystem.IsLinux())
            {
                return File.Exists("/etc/machine-id")
                    ? File.ReadAllText("/etc/machine-id").Trim()
                    : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                var result = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ioreg",
                    Arguments = "-rd1 -c IOPlatformExpertDevice",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                });
                var output = result?.StandardOutput.ReadToEnd() ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(output, "\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"");
                return match.Success ? match.Groups[1].Value : null;
            }
        }
        catch { /* 权限不足等，回退到备用方案 */ }
        return null;
    }

    private static byte[] DeriveKey(byte[] fingerprint)
    {
        using var hmac = new HMACSHA256(fingerprint);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes("Baihua.ApiKey.Encryption.v1"));
    }

    /// <summary>
    /// 解析加密密钥
    /// 优先级：持久化密钥文件 &gt; BAIHUA_ENCRYPTION_KEY 环境变量 &gt; 机器指纹
    /// </summary>
    public static byte[] ResolveFingerprint()
    {
        // 1. 优先读取持久化密钥文件（最稳定，容器重建后仍然存在）
        var keyFile = KeyFilePath;
        if (File.Exists(keyFile))
        {
            var keyFromFile = File.ReadAllText(keyFile).Trim();
            if (!string.IsNullOrWhiteSpace(keyFromFile))
            {
                return SHA256.HashData(Encoding.UTF8.GetBytes(keyFromFile));
            }
        }

        // 2. BAIHUA_ENCRYPTION_KEY 环境变量（高级用户手动配置）
        var envKey = Environment.GetEnvironmentVariable("BAIHUA_ENCRYPTION_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(envKey.Trim()));
        }

        // 3. 回退：机器指纹（密钥文件丢失时的兜底）
        return GetMachineFingerprint();
    }

    /// <summary>
    /// 生成新的随机密钥文件。返回生成的密钥内容。
    /// </summary>
    public static string GenerateKeyFile()
    {
        var keyFile = KeyFilePath;

        // 已存在则复用（Family/AI 双进程可能并发启动各自生成不同密钥，先到先得，后到者读取同一文件，
        // 保证共享卷上两进程密钥一致；也防止已有固定密钥时被意外覆盖）
        if (File.Exists(keyFile))
        {
            var existing = File.ReadAllText(keyFile).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;
        }

        var randomKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(KeySize));

        EnsureKeyFileDirectory();
        File.WriteAllText(keyFile, randomKey);
        RestrictFilePermissions(keyFile);

        return randomKey;
    }

    private static void EnsureKeyFileDirectory()
    {
        var dir = Path.GetDirectoryName(KeyFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else
            {
                var fi = new FileInfo(path);
                fi.Attributes |= FileAttributes.Hidden;
            }
        }
        catch { /* 权限设置失败不影响功能 */ }
    }

    /// <summary>
    /// 用指定指纹解密（用于迁移时尝试旧密钥）
    /// </summary>
    public static string DecryptWithFingerprint(string cipherText, byte[] fingerprint)
    {
        if (string.IsNullOrEmpty(cipherText) || !cipherText.StartsWith("A:"))
            return "";

        try
        {
            var data = Convert.FromBase64String(cipherText[2..]);
            if (data.Length < 1 + NonceSize + TagSize)
                return "";

            var version = data[0];
            if (version != Version)
                return "";

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipherBytes = new byte[data.Length - 1 - NonceSize - TagSize];

            Buffer.BlockCopy(data, 1, nonce, 0, NonceSize);
            Buffer.BlockCopy(data, 1 + NonceSize, cipherBytes, 0, cipherBytes.Length);
            Buffer.BlockCopy(data, 1 + NonceSize + cipherBytes.Length, tag, 0, TagSize);

            var key = DeriveKey(fingerprint);
            var plainBytes = new byte[cipherBytes.Length];

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 加密 API Key
    /// 格式: [1 byte version][12 bytes nonce][n bytes ciphertext][16 bytes tag]
    /// </summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return "";

        try
        {
            var fingerprint = ResolveFingerprint();
            var key = DeriveKey(fingerprint);
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
            }

            var result = new byte[1 + NonceSize + cipherBytes.Length + TagSize];
            result[0] = Version;
            Buffer.BlockCopy(nonce, 0, result, 1, NonceSize);
            Buffer.BlockCopy(cipherBytes, 0, result, 1 + NonceSize, cipherBytes.Length);
            Buffer.BlockCopy(tag, 0, result, 1 + NonceSize + cipherBytes.Length, TagSize);

            return "A:" + Convert.ToBase64String(result);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("API Key AES encryption failed", ex);
        }
    }

    /// <summary>
    /// 解密 API Key（使用当前解析的密钥）
    /// </summary>
    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return "";

        if (!cipherText.StartsWith("A:"))
            return "";

        try
        {
            var data = Convert.FromBase64String(cipherText[2..]);

            if (data.Length < 1 + NonceSize + TagSize)
                throw new FormatException("Invalid ciphertext format: insufficient length");

            var version = data[0];
            if (version != Version)
                throw new NotSupportedException($"Unsupported encryption version: {version}");

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipherBytes = new byte[data.Length - 1 - NonceSize - TagSize];

            Buffer.BlockCopy(data, 1, nonce, 0, NonceSize);
            Buffer.BlockCopy(data, 1 + NonceSize, cipherBytes, 0, cipherBytes.Length);
            Buffer.BlockCopy(data, 1 + NonceSize + cipherBytes.Length, tag, 0, TagSize);

            var fingerprint = ResolveFingerprint();
            var key = DeriveKey(fingerprint);
            var plainBytes = new byte[cipherBytes.Length];

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            return "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}

/// <summary>
/// 加密方案类型
/// </summary>
public enum EncryptionScheme
{
    /// <summary>AES-256-GCM（机器指纹派生密钥）</summary>
    AesGcm
}
