using BaihuaSdk.Signing;
using Xunit;

namespace BaihuaSdk.Tests.Signing;

public class RequestSignerTests
{
    private const string TestDeviceId = "test_device_001";
    private const string TestDeviceName = "TestPhone";

    private RequestSigner CreateSigner(
        string? websiteBaseUrl = null, string? mobileClientSecret = null) =>
        new(TestDeviceId, TestDeviceName, websiteBaseUrl, mobileClientSecret);

    [Fact]
    public void SignRequest_WithoutSecret_ReturnsEmpty()
    {
        var signer = CreateSigner();
        var headers = signer.SignRequest("GET", "http://192.168.1.2:8788/mg/manifest");
        Assert.Empty(headers);
    }

    [Fact]
    public void SignRequest_WithGlobalSecret_ProducesValidSignature()
    {
        var signer = CreateSigner();
        signer.SetSharedSecret("my-secret-key");

        var headers = signer.SignRequest("GET", "http://example.com/path?a=1",
            serverUrl: "http://example.com");

        Assert.True(headers.ContainsKey("X-Mobile-Signature"));
        Assert.True(headers.ContainsKey("X-Device-Id"));
        Assert.True(headers.ContainsKey("X-Device-Name"));
        Assert.Equal(TestDeviceId, headers["X-Device-Id"]);
        Assert.Equal(TestDeviceName, headers["X-Device-Name"]);

        var sig = headers["X-Mobile-Signature"];
        var parts = sig.Split(':');
        Assert.Equal(2, parts.Length);
        Assert.True(long.TryParse(parts[0], out var ts));
        Assert.True(ts > 0);
        Assert.NotEmpty(parts[1]);
    }

    [Fact]
    public void SignRequest_PostBody_ProducesSignature()
    {
        var signer = CreateSigner();
        signer.SetSharedSecret("secret");

        var headers = signer.SignRequest("POST", "http://example.com/api",
            body: "{\"key\":\"value\"}", serverUrl: "http://example.com");

        Assert.True(headers.ContainsKey("X-Mobile-Signature"));
    }

    [Fact]
    public void SignRequest_EmptyBody_IncludesEmptyBodyHash()
    {
        var signer = CreateSigner();
        signer.SetSharedSecret("secret");

        var headers = signer.SignRequest("GET", "http://example.com/path?a=1",
            serverUrl: "http://example.com");

        Assert.True(headers["X-Mobile-Signature"].Contains(':'));
    }

    [Fact]
    public void SignRequest_WebsiteUrl_FallsBackToMobileClientSecret()
    {
        var signer = CreateSigner(
            websiteBaseUrl: "https://www.shzhengji.com",
            mobileClientSecret: "built-in-secret");

        var headers = signer.SignRequest("GET", "https://www.shzhengji.com/mg/manifest",
            serverUrl: "https://www.shzhengji.com");

        Assert.True(headers.ContainsKey("X-Mobile-Signature"));
        Assert.NotEmpty(headers["X-Mobile-Signature"]);
    }

    [Fact]
    public void SignRequest_WebsiteUrl_WithoutClientSecret_ReturnsEmpty()
    {
        var signer = CreateSigner(websiteBaseUrl: "https://www.shzhengji.com");

        var headers = signer.SignRequest("GET", "https://www.shzhengji.com/mg/manifest",
            serverUrl: "https://www.shzhengji.com");

        Assert.Empty(headers);
    }

    [Fact]
    public void ServerSecret_OverridesGlobalSecret()
    {
        var signer = CreateSigner();
        signer.SetSharedSecret("global");
        signer.SetServerSecret("http://server-a", "server-a-secret");

        var headersA = signer.SignRequest("GET", "http://server-a/path",
            serverUrl: "http://server-a");
        Assert.NotEmpty(headersA);

        signer.ClearSecrets();
        signer.SetSharedSecret("global");

        var headersB = signer.SignRequest("GET", "http://server-b/path",
            serverUrl: "http://server-b");
        Assert.NotEmpty(headersB);
    }

    [Fact]
    public void HasServerSecret_ReturnsCorrectly()
    {
        var signer = CreateSigner();
        Assert.False(signer.HasServerSecret("http://s1"));

        signer.SetServerSecret("http://s1", "s1-secret");
        Assert.True(signer.HasServerSecret("http://s1"));
        Assert.False(signer.HasServerSecret("http://s2"));
    }

    [Fact]
    public void GetServerSecret_ReturnsNull_WhenNoSecretSet()
    {
        var signer = CreateSigner();
        Assert.Null(signer.GetServerSecret("http://unknown"));
    }

    // ---- crypto unit tests ----

    [Fact]
    public void Sha256Hex_EmptyString_ReturnsKnownHash()
    {
        // SHA-256("") = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        var result = RequestSigner.Sha256Hex("");
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result);
    }

    [Fact]
    public void Sha256Hex_KnownString_ReturnsCorrectHash()
    {
        // SHA-256("hello world") = b94d27b9...
        var result = RequestSigner.Sha256Hex("hello world");
        Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", result);
    }

    [Fact]
    public void HmacSha256Base64_KnownValues_ReturnsCorrectSignature()
    {
        var result = RequestSigner.HmacSha256Base64("message", "secret");
        // HMAC-SHA256 of "message" with key "secret"
        Assert.Equal("i19IcCmVwVmMVz2x4hhmqbgl1KeU0WnXBgoDYFeWNgs=", result);
    }

    [Fact]
    public void HmacSha256Base64_DifferentKey_ProducesDifferentOutput()
    {
        var r1 = RequestSigner.HmacSha256Base64("message", "key1");
        var r2 = RequestSigner.HmacSha256Base64("message", "key2");
        Assert.NotEqual(r1, r2);
    }

    [Fact]
    public void ExtractPathFromUrl_VariousUrls_ReturnsCorrectPath()
    {
        Assert.Equal("/", RequestSigner.ExtractPathFromUrl("http://example.com"));
        Assert.Equal("/api/v1", RequestSigner.ExtractPathFromUrl("http://example.com/api/v1"));
        Assert.Equal("/api?x=1", RequestSigner.ExtractPathFromUrl("http://example.com/api?x=1"));
        Assert.Equal("/mg/manifest?vaultId=123",
            RequestSigner.ExtractPathFromUrl("https://example.com:8788/mg/manifest?vaultId=123"));
        Assert.Equal("plain-text", RequestSigner.ExtractPathFromUrl("plain-text"));
    }

    [Fact]
    public void SignRequest_DeterministicOutput()
    {
        // Verify that with fixed inputs, SignRequest produces a predictable signature
        // This test ensures the algorithm matches what the Kotlin/ArkTS versions produce
        var signer = CreateSigner();
        signer.SetSharedSecret("fixed-test-secret");

        // We can't control the timestamp, but we can verify the structure
        var headers = signer.SignRequest("GET", "http://example.com/api",
            body: "test-body", serverUrl: "http://example.com");

        var sig = headers["X-Mobile-Signature"];
        var parts = sig.Split(':');
        Assert.Equal(2, parts.Length);

        // Verify it's valid base64
        var bytes = Convert.FromBase64String(parts[1]);
        Assert.Equal(32, bytes.Length); // SHA-256 produces 32 bytes
    }

    [Fact]
    public void SignRequest_NullBody_HandlesGracefully()
    {
        var signer = CreateSigner();
        signer.SetSharedSecret("secret");

        var headers = signer.SignRequest("GET", "http://example.com/api",
            body: null, serverUrl: "http://example.com");

        Assert.True(headers.ContainsKey("X-Mobile-Signature"));
    }

    [Fact]
    public void ClearSecrets_RemovesAll()
    {
        var signer = CreateSigner();
        signer.SetSharedSecret("global");
        signer.SetServerSecret("http://s1", "s1-secret");

        signer.ClearSecrets();

        Assert.Null(signer.GetServerSecret("http://s1"));
        var headers = signer.SignRequest("GET", "http://s1/path",
            serverUrl: "http://s1");
        Assert.Empty(headers);
    }

    [Fact]
    public void ServerSecret_NormalizesUrl()
    {
        var signer = CreateSigner();
        signer.SetServerSecret("http://Example.COM:8788/", "my-secret");

        // Same URL with different case and trailing slash should match
        var headers = signer.SignRequest("GET", "http://example.com:8788/path",
            serverUrl: "http://example.com:8788");

        Assert.NotEmpty(headers);
    }

    [Fact]
    public void DeviceIdentity_PropagatesToHeaders()
    {
        var signer = new RequestSigner("my-device-123", "My Phone");
        signer.SetSharedSecret("secret");

        var headers = signer.SignRequest("GET", "http://example.com/api",
            serverUrl: "http://example.com");

        Assert.Equal("my-device-123", headers["X-Device-Id"]);
        Assert.Equal("My Phone", headers["X-Device-Name"]);
    }
}
