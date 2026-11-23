using BaihuaSdk.Services;
using BaihuaSdk.Signing;
using BaihuaSdk.Storage;
using BaihuaSdk.Transport;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using MobileContract.VaultSync;

namespace BaihuaSdk.Tests.Services;

public class SyncServiceTests
{
    // ---- 静态方法测试 ----

    [Theory]
    [InlineData("file.md", true)]
    [InlineData("path/to/readme.json", true)]
    [InlineData("image.png", false)]
    [InlineData("photo.jpg", false)]
    [InlineData("notes.MD", true)] // case insensitive
    public void IsTextFile(string path, bool expected)
    {
        Assert.Equal(expected, SyncServiceImpl.IsTextFile(path));
    }

    [Theory]
    [InlineData("notes/readme.md")]
    [InlineData("simple-file.txt")]
    [InlineData("a/b/c/d/file.json")]
    public void AssertValidRelPath_Valid_ReturnsNormalized(string path)
    {
        var result = SyncServiceImpl.AssertValidRelPath(path);
        Assert.Equal(path, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute/path")]
    [InlineData("../escape")]
    [InlineData("file:name")]
    [InlineData("contains../inside")]
    public void AssertValidRelPath_Invalid_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => SyncServiceImpl.AssertValidRelPath(path));
    }

    [Fact]
    public void AssertValidRelPath_ConvertsBackslash()
    {
        var result = SyncServiceImpl.AssertValidRelPath(@"dir\file.md");
        Assert.Equal("dir/file.md", result);
    }

    // ---- Mock HttpClient 测试 ----

    private static (HttpClient client, MockHttpMessageHandler handler) CreateMockClient()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        return (client, handler);
    }

    [Fact]
    public async Task FetchManifestAsync_Success_ReturnsManifest()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string> { ["X-Mobile-Signature"] = "test" });

        var manifest = new VaultManifestResponse(
            VaultId: "vault-1",
            VaultName: "Test Vault",
            Cursor: 0,
            Files: new List<ManifestFile>
            {
                new(Op: "upsert", RelPath: "test.md", Mtime: 1000, Size: null, Sha256: null)
            });
        handler.SetupResponse("/mg/manifest", HttpStatusCode.OK, JsonSerializer.Serialize(manifest));

        // Ensure cache is clear for test isolation
        SyncServiceImpl.ClearVaultListCache("http://localhost");

        var service = new SyncServiceImpl(client, signerMock.Object);

        var result = await service.FetchManifestAsync("http://localhost", "vault-1", "device-1");

        Assert.NotNull(result);
        Assert.Single(result.Files!);
        Assert.Equal("test.md", result.Files![0].RelPath);
    }

    [Fact]
    public async Task FetchManifestAsync_ServerError_Throws()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/manifest", HttpStatusCode.InternalServerError, "Server error");

        // Ensure cache is clear for test isolation
        SyncServiceImpl.ClearVaultListCache("http://localhost");

        var service = new SyncServiceImpl(client, signerMock.Object);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.FetchManifestAsync("http://localhost", "vault-1", "device-1"));
    }

    [Fact]
    public async Task FetchManifestAsync_NotFound_Throws()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/manifest", HttpStatusCode.NotFound, "Not found");

        var service = new SyncServiceImpl(client, signerMock.Object);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.FetchManifestAsync("http://localhost", "vault-1", "device-1"));
    }

    [Fact]
    public async Task DownloadTextFileAsync_Success_ReturnsContent()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/file", HttpStatusCode.OK, "# Test Content");

        var service = new SyncServiceImpl(client, signerMock.Object);

        var result = await service.DownloadTextFileAsync("http://localhost", "vault-1", "test.md");

        Assert.Equal("# Test Content", result);
    }

    [Fact]
    public async Task DownloadBinaryFileAsync_Success_ReturnsBytes()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        handler.SetupResponse("/mg/file", HttpStatusCode.OK, bytes);

        var service = new SyncServiceImpl(client, signerMock.Object);

        var result = await service.DownloadBinaryFileAsync("http://localhost", "vault-1", "image.png");

        Assert.Equal(bytes, result);
    }

    [Fact]
    public void AssertValidRelPath_EscapePath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SyncServiceImpl.AssertValidRelPath("../escape.md"));
    }

    [Fact]
    public async Task FetchVaultListAsync_Success_ReturnsList()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        var vaults = new[]
        {
            new VaultInfo(Id: "v1", Name: "Vault 1", Industry: "notes", Source: "server"),
            new VaultInfo(Id: "v2", Name: "Vault 2", Industry: "dev", Source: "server")
        };
        handler.SetupResponse("/mg/vaults", HttpStatusCode.OK, JsonSerializer.Serialize(vaults));

        var service = new SyncServiceImpl(client, signerMock.Object);

        var result = await service.FetchVaultListAsync("http://localhost");

        Assert.Equal(2, result.Count);
        Assert.Equal("v1", result[0].Id);
    }

    [Fact]
    public async Task FetchVaultListAsync_CachesResult()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        var vaults = new[] { new VaultInfo(Id: "v1", Name: "Vault 1", Industry: "notes", Source: "server") };
        handler.SetupResponse("/mg/vaults", HttpStatusCode.OK, JsonSerializer.Serialize(vaults));

        var service = new SyncServiceImpl(client, signerMock.Object);

        // First call
        var result1 = await service.FetchVaultListAsync("http://localhost");
        // Second call (should use cache)
        var result2 = await service.FetchVaultListAsync("http://localhost");

        Assert.Equal(result1, result2);
        Assert.Single(handler.RequestLog); // Only one HTTP request made
    }

    [Fact]
    public void ClearVaultListCache_RemovesCache()
    {
        SyncServiceImpl.ClearVaultListCache("http://localhost");
        // No exception = success
    }

    // ---- SyncVaultAsync 全量同步测试 ----

    private static FileSystemVaultStorage CreateTempStorage(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), $"sync_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new FileSystemVaultStorage(dir);
    }

    /// <summary>服务端 mtime 为 unix 秒，本地存储按毫秒读写</summary>
    private const long ServerMtimeSec = 1710000000; // 2024-03-09 左右
    private const long ServerMtimeMs = ServerMtimeSec * 1000L;

    [Fact]
    public async Task SyncVaultAsync_AlwaysDownloads_EvenIfMtimeMatches()
    {
        // 全量同步语义：本地已存在且 mtime 一致的文件也会重新下载（不依赖客户端增量比对）
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        var storage = CreateTempStorage(out var dir);
        try
        {
            await storage.WriteTextFileAsync("notes/a.md", "旧内容", ServerMtimeMs);

            var manifest = new VaultManifestResponse(
                VaultId: "vault-1", VaultName: "Test", Cursor: 42,
                Files: new List<ManifestFile>
                {
                    new(Op: "upsert", RelPath: "notes/a.md", Mtime: ServerMtimeSec, Size: null, Sha256: null),
                    new(Op: "upsert", RelPath: "notes/b.md", Mtime: ServerMtimeSec, Size: null, Sha256: null)
                });
            handler.SetupResponse("/mg/manifest", HttpStatusCode.OK, JsonSerializer.Serialize(manifest));
            handler.SetupResponse("/mg/file", HttpStatusCode.OK, "# B 内容");

            var service = new SyncServiceImpl(client, signerMock.Object);
            var result = await service.SyncVaultAsync("http://localhost", "vault-1", "device-1", storage);

            Assert.Equal(2, result.Downloaded);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(0, result.Failed);
            Assert.Equal(42, result.Cursor);
            // 两个文件都发起下载（a.md 不因 mtime 一致而跳过）
            Assert.Equal(2, handler.RequestLog.Count(p => p == "/mg/file"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVaultAsync_DownloadsAndWritesContent()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        var storage = CreateTempStorage(out var dir);
        try
        {
            var manifest = new VaultManifestResponse(
                VaultId: "vault-1", VaultName: "Test", Cursor: 0,
                Files: new List<ManifestFile>
                {
                    new(Op: "upsert", RelPath: "notes/a.md", Mtime: ServerMtimeSec, Size: null, Sha256: null)
                });
            handler.SetupResponse("/mg/manifest", HttpStatusCode.OK, JsonSerializer.Serialize(manifest));
            handler.SetupResponse("/mg/file", HttpStatusCode.OK, "# 新内容");

            var service = new SyncServiceImpl(client, signerMock.Object);
            var result = await service.SyncVaultAsync("http://localhost", "vault-1", "device-1", storage);

            Assert.Equal(1, result.Downloaded);
            Assert.Equal(0, result.Skipped);
            Assert.Equal("# 新内容", await File.ReadAllTextAsync(Path.Combine(dir, "notes", "a.md")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVaultAsync_FileNotFound_SkipsNotFails()
    {
        var (client, handler) = CreateMockClient();
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new Dictionary<string, string>());

        var storage = CreateTempStorage(out var dir);
        try
        {
            var manifest = new VaultManifestResponse(
                VaultId: "vault-1", VaultName: "Test", Cursor: 0,
                Files: new List<ManifestFile>
                {
                    new(Op: "upsert", RelPath: "notes/gone.md", Mtime: ServerMtimeSec, Size: null, Sha256: null)
                });
            handler.SetupResponse("/mg/manifest", HttpStatusCode.OK, JsonSerializer.Serialize(manifest));
            // manifest 快照里存在、但文件已被服务端删除 → 下载返回 404，应静默跳过而非计入失败
            handler.SetupResponse("/mg/file", HttpStatusCode.NotFound, "Not found");

            var service = new SyncServiceImpl(client, signerMock.Object);
            var result = await service.SyncVaultAsync("http://localhost", "vault-1", "device-1", storage);

            Assert.Equal(0, result.Downloaded);
            Assert.Equal(0, result.Failed);
            Assert.Null(result.Errors);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
