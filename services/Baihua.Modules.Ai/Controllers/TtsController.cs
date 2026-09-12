using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baihua.Contracts.Ai;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Baihua.Modules.Ai.Controllers;

[ApiController]
[Route("api/ai/tts")]
public class TtsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TtsController> _logger;
    private readonly IMemoryCache _cache;
    private readonly string _ttsBaseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Dictionary<string, (string Name, string Lang, string Gender)> VoiceInfo = new()
    {
        ["zf_xiaobei"] = ("小贝", "zh", "female"),
        ["zf_xiaoni"] = ("小妮", "zh", "female"),
        ["zf_xiaoxiao"] = ("小晓", "zh", "female"),
        ["zf_xiaoyi"] = ("小艺", "zh", "female"),
        ["zm_yunjian"] = ("云健", "zh", "male"),
        ["zm_yunxi"] = ("云希", "zh", "male"),
        ["zm_yunxia"] = ("夏然", "zh", "male"),
        ["zm_yunyang"] = ("云扬", "zh", "male"),
        ["af_heart"] = ("Heart", "en", "female"),
        ["af_alloy"] = ("Alloy", "en", "female"),
        ["af_bella"] = ("Bella", "en", "female"),
        ["af_aoede"] = ("Aoede", "en", "female"),
        ["am_michael"] = ("Michael", "en", "male"),
        ["am_adam"] = ("Adam", "en", "male"),
    };

    public TtsController(
        IHttpClientFactory httpClientFactory,
        ILogger<TtsController> logger,
        IMemoryCache cache,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cache = cache;
        _ttsBaseUrl = configuration["KokoroTts:BaseUrl"] ?? "http://localhost:8001";
    }

    [HttpPost("speech")]
    public async Task<IActionResult> Speech([FromBody] TtsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return Ok(new { error = "Text is required" });
        if (string.IsNullOrWhiteSpace(request.Voice))
            return Ok(new { error = "Voice is required" });

        var speed = request.Speed ?? 1.0f;
        var cacheKey = $"tts:{request.Voice}:{speed:F1}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Text)))}";
        if (_cache.TryGetValue<byte[]>(cacheKey, out var cached) && cached != null)
        {
            _logger.LogDebug("TTS cache hit: {Key}", cacheKey);
            return File(cached, "audio/wav");
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);

        var payload = new
        {
            model = "kokoro",
            input = request.Text,
            voice = request.Voice,
            speed = speed,
        };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var content = new StringContent(json, new System.Text.UTF8Encoding(false), "application/json");

        try
        {
            var resp = await client.PostAsync($"{_ttsBaseUrl}/v1/audio/speech", content);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                _logger.LogWarning("TTS server returned {Status}: {Error}", resp.StatusCode, err);
                return StatusCode((int)resp.StatusCode, new { error = err });
            }

            var audioBytes = await resp.Content.ReadAsByteArrayAsync();
            var opts = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(30),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
                Size = audioBytes.Length,
            };
            _cache.Set(cacheKey, audioBytes, opts);
            return File(audioBytes, "audio/wav");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "TTS server unreachable at {Url}", _ttsBaseUrl);
            return StatusCode(503, new { error = "TTS service unavailable" });
        }
    }

    [HttpGet("voices")]
    public IActionResult Voices()
    {
        var voices = VoiceInfo.Select(kv => new TtsVoice(
            kv.Key,
            kv.Value.Name,
            kv.Value.Lang,
            kv.Value.Gender
        )).ToList();
        return Ok(new TtsVoiceList(voices));
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var resp = await client.GetAsync($"{_ttsBaseUrl}/health");
            return Ok(new { status = resp.IsSuccessStatusCode ? "ok" : "degraded" });
        }
        catch
        {
            return Ok(new { status = "unavailable" });
        }
    }
}