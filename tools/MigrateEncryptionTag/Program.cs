// 一次性迁移工具：把加密标签从 TaskRunner.ApiKey.Encryption.v1 迁移到 Baihua.ApiKey.Encryption.v1
// 用法: dotnet run --project tools/MigrateEncryptionTag
// 逻辑: 读 ai.db 所有 EncryptedApiKey，用旧标签派生密钥解密，再用新标签重新加密写回。
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

const string OldTag = "TaskRunner.ApiKey.Encryption.v1";
const string NewTag = "Baihua.ApiKey.Encryption.v1";
const int NonceSize = 12;
const int TagSize = 16;
const byte Version = 1;

var dbPath = args.Length > 0 ? args[0] : @"C:\Users\lumin\.baihua\db\ai.db";
var keyFile = Path.Combine(Path.GetDirectoryName(dbPath)!, ".baihua-key");

if (!File.Exists(keyFile))
{
    Console.WriteLine("ERROR: .baihua-key not found at " + keyFile);
    return 1;
}

var keyFileBytes = SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(keyFile).Trim()));

byte[] DeriveKey(byte[] fingerprint, string tag)
{
    using var hmac = new HMACSHA256(fingerprint);
    return hmac.ComputeHash(Encoding.UTF8.GetBytes(tag));
}

string? DecryptWithTag(string cipherText, string tag)
{
    try
    {
        if (!cipherText.StartsWith("A:")) return null;
        var data = Convert.FromBase64String(cipherText[2..]);
        if (data.Length < 1 + NonceSize + TagSize || data[0] != Version) return null;
        var nonce = new byte[NonceSize];
        var tagBytes = new byte[TagSize];
        var cipherBytes = new byte[data.Length - 1 - NonceSize - TagSize];
        Buffer.BlockCopy(data, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(data, 1 + NonceSize, cipherBytes, 0, cipherBytes.Length);
        Buffer.BlockCopy(data, 1 + NonceSize + cipherBytes.Length, tagBytes, 0, TagSize);
        var key = DeriveKey(keyFileBytes, tag);
        var plain = new byte[cipherBytes.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tagBytes, plain);
        return Encoding.UTF8.GetString(plain);
    }
    catch { return null; }
}

string EncryptWithTag(string plainText, string tag)
{
    var key = DeriveKey(keyFileBytes, tag);
    var nonce = new byte[NonceSize];
    RandomNumberGenerator.Fill(nonce);
    var plainBytes = Encoding.UTF8.GetBytes(plainText);
    var cipherBytes = new byte[plainBytes.Length];
    var tagBytes = new byte[TagSize];
    using (var aes = new AesGcm(key, TagSize))
    {
        aes.Encrypt(nonce, plainBytes, cipherBytes, tagBytes);
    }
    var result = new byte[1 + NonceSize + cipherBytes.Length + TagSize];
    result[0] = Version;
    Buffer.BlockCopy(nonce, 0, result, 1, NonceSize);
    Buffer.BlockCopy(cipherBytes, 0, result, 1 + NonceSize, cipherBytes.Length);
    Buffer.BlockCopy(tagBytes, 0, result, 1 + NonceSize + cipherBytes.Length, TagSize);
    return "A:" + Convert.ToBase64String(result);
}

Console.WriteLine($"DB: {dbPath}");
Console.WriteLine($"KeyFile: {keyFile} (exists: {File.Exists(keyFile)})");

using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

var providers = new List<(long Id, string? Encrypted)>();
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT Id, EncryptedApiKey FROM AiProviderSettings WHERE EncryptedApiKey IS NOT NULL AND EncryptedApiKey != ''";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        providers.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
    }
}

Console.WriteLine($"Found {providers.Count} providers with encrypted keys.");
int migrated = 0, failed = 0, skipped = 0;

foreach (var (id, encrypted) in providers)
{
    if (string.IsNullOrEmpty(encrypted)) { skipped++; continue; }

    // 先用新标签解（可能已经是新标签加密的）
    var newDecrypted = DecryptWithTag(encrypted, NewTag);
    if (newDecrypted != null)
    {
        Console.WriteLine($"  [{id}] already decryptable with new tag, skip.");
        skipped++;
        continue;
    }

    // 用旧标签解
    var oldDecrypted = DecryptWithTag(encrypted, OldTag);
    if (oldDecrypted == null)
    {
        Console.WriteLine($"  [{id}] FAILED to decrypt with both tags!");
        failed++;
        continue;
    }

    // 用新标签重新加密写回
    var reEncrypted = EncryptWithTag(oldDecrypted, NewTag);
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "UPDATE AiProviderSettings SET EncryptedApiKey = @v, UpdatedAt = datetime('now') WHERE Id = @id";
        cmd.Parameters.AddWithValue("@v", reEncrypted);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }
    Console.WriteLine($"  [{id}] MIGRATED (old tag -> new tag).");
    migrated++;
}

Console.WriteLine($"\nDone. migrated={migrated}, failed={failed}, skipped={skipped}");
return failed > 0 ? 2 : 0;
