using System.Text.Json;
using BaihuaSdk.Storage;
using BaihuaSdk.Signing;
using BaihuaSdk.Transport;

namespace MobileApp.Maui.Services;

public interface IMasterService
{
    Task<List<MasterListItem>> GetMastersAsync(bool useCache = true);
    Task<CreateMasterResponse> CreateMasterAsync(string goal, string industry);
    Task<bool> DeleteMasterAsync(string masterId);
    Task<ApprenticeProfileResponse> GetProfileAsync(string masterId, bool useCache = true);
    Task<StageCompleteResponse> CompleteStageAsync(string masterId, string stageName);
    Task<ApprenticeProfileResponse> UpdateProfileAsync(string masterId, UpdateProfileRequest request);
    IAsyncEnumerable<string> StreamChatAsync(string masterId, string message, string stage, List<ChatHistoryItem>? history = null, CancellationToken ct = default);
    Task<List<ChatHistoryItem>> GetConversationHistoryAsync(string masterId, int limit = 20);
    Task<List<ChatHistoryItem>> GetConversationsFromServerAsync(string masterId, int limit = 100);
    Task<bool> SyncConversationsToServerAsync(string masterId, List<ChatHistoryItem> conversations);
    Task<bool> CheckAiConfiguredAsync();
    Task<List<VaultFocusItem>> GetVaultFocusAsync(string masterId);
    Task<List<VaultListItem>> GetAvailableVaultsAsync();
    Task<bool> FocusVaultAsync(string masterId, string vaultId, string? stageName = null);
    Task<bool> RemoveVaultFocusAsync(string masterId, string vaultId);
    Task<bool> GetDisclaimerAcceptedAsync(string masterId);
    Task SetDisclaimerAcceptedAsync(string masterId);
    List<string> GetIndustries();
    string ResolveMasterName(string industry);
    bool IsMedicalOrLegalIndustry(string industry);
    event EventHandler? OnProfileUpdated;
    event EventHandler? OnMastersUpdated;
}

public class MasterService : IMasterService
{
    private readonly IServerConfigStore _serverStore;
    private readonly IRequestSigner _signer;
    private readonly HttpClient _httpClient;
    private readonly IMasterCacheService _cacheService;

    private static readonly Dictionary<string, string> IndustryMasterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["中医"] = "岐伯", ["医学"] = "岐伯",
        ["计算机"] = "图灵", ["IT"] = "图灵", ["编程"] = "图灵",
        ["会计"] = "算圣", ["财务"] = "算圣",
        ["教资"] = "夫子", ["教育"] = "夫子",
        ["法律"] = "廷尉",
        ["建筑"] = "鲁班",
    };

    private static readonly string[] Industries = ["中医", "医学", "计算机", "IT", "编程", "会计", "财务", "教资", "教育", "法律", "建筑", "通用"];

    public event EventHandler? OnProfileUpdated;
    public event EventHandler? OnMastersUpdated;

    public MasterService(IServerConfigStore serverStore, IRequestSigner signer, HttpClient httpClient, IMasterCacheService cacheService)
    {
        _serverStore = serverStore;
        _signer = signer;
        _httpClient = httpClient;
        _cacheService = cacheService;
    }

    public List<string> GetIndustries() => Industries.ToList();

    public string ResolveMasterName(string industry)
    {
        foreach (var (key, name) in IndustryMasterNames)
        {
            if (industry.Contains(key, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return "先生";
    }

    public bool IsMedicalOrLegalIndustry(string industry)
    {
        if (string.IsNullOrEmpty(industry)) return false;
        var lower = industry.ToLowerInvariant();
        return lower.Contains("医") || lower.Contains("药") || lower.Contains("法") || lower.Contains("律");
    }

    public async Task<bool> GetDisclaimerAcceptedAsync(string masterId)
    {
        return await _cacheService.GetDisclaimerAcceptedAsync(masterId);
    }

    public async Task SetDisclaimerAcceptedAsync(string masterId)
    {
        await _cacheService.SetDisclaimerAcceptedAsync(masterId, true);
    }

    private async Task<HttpTransport> CreateTransportAsync()
    {
        var server = await _serverStore.GetCurrentServerAsync();
        if (server == null)
            throw new InvalidOperationException("未选择服务器，请先配对");

        return new HttpTransport(_httpClient, _signer, server.HttpUrl);
    }

    public async Task<List<MasterListItem>> GetMastersAsync(bool useCache = true)
    {
        if (useCache)
        {
            var cached = await _cacheService.GetCachedMastersAsync();
            if (cached.Count > 0)
                return cached;
        }

        var transport = await CreateTransportAsync();
        var response = await transport.GetJsonAsync<List<MasterListItem>>("/api/master");
        if (!response.IsSuccess)
            throw new InvalidOperationException(response.ErrorMessage ?? "获取师父列表失败");

        var masters = response.Data ?? new();
        await _cacheService.CacheMastersAsync(masters);
        OnMastersUpdated?.Invoke(this, EventArgs.Empty);
        return masters;
    }

    public async Task<CreateMasterResponse> CreateMasterAsync(string goal, string industry)
    {
        var transport = await CreateTransportAsync();
        var request = new CreateMasterRequest { Goal = goal, Industry = industry };
        var response = await transport.PostJsonAsync<CreateMasterResponse>("/api/master/create", request);
        if (!response.IsSuccess)
            throw new InvalidOperationException(response.ErrorMessage ?? "创建师父失败");

        OnMastersUpdated?.Invoke(this, EventArgs.Empty);
        return response.Data ?? new();
    }

    public async Task<bool> DeleteMasterAsync(string masterId)
    {
        var transport = await CreateTransportAsync();
        var response = await transport.DeleteJsonAsync<object>($"/api/master/{masterId}");
        if (response.IsSuccess)
        {
            await _cacheService.ClearConversationAsync(masterId);
            OnMastersUpdated?.Invoke(this, EventArgs.Empty);
        }
        return response.IsSuccess;
    }

    public async Task<ApprenticeProfileResponse> GetProfileAsync(string masterId, bool useCache = true)
    {
        if (useCache)
        {
            var cached = await _cacheService.GetCachedProfileAsync(masterId);
            if (cached != null && cached.Success)
                return cached;
        }

        var transport = await CreateTransportAsync();
        var response = await transport.GetJsonAsync<ApprenticeProfileResponse>($"/api/master/{masterId}/profile");
        if (!response.IsSuccess)
            throw new InvalidOperationException(response.ErrorMessage ?? "获取画像失败");

        var profile = response.Data ?? new();
        if (profile.Success)
            await _cacheService.CacheProfileAsync(masterId, profile);
        return profile;
    }

    public async Task<StageCompleteResponse> CompleteStageAsync(string masterId, string stageName)
    {
        var transport = await CreateTransportAsync();
        var request = new StageCompleteRequest { StageName = stageName };
        var response = await transport.PostJsonAsync<StageCompleteResponse>($"/api/master/{masterId}/stage-complete", request);
        if (!response.IsSuccess)
            throw new InvalidOperationException(response.ErrorMessage ?? "阶段完成处理失败");

        OnProfileUpdated?.Invoke(this, EventArgs.Empty);
        OnMastersUpdated?.Invoke(this, EventArgs.Empty);
        return response.Data ?? new();
    }

    public async Task<ApprenticeProfileResponse> UpdateProfileAsync(string masterId, UpdateProfileRequest request)
    {
        var transport = await CreateTransportAsync();
        var response = await transport.PutJsonAsync<ApprenticeProfileResponse>($"/api/master/{masterId}/profile", request);
        if (!response.IsSuccess)
            throw new InvalidOperationException(response.ErrorMessage ?? "更新画像失败");

        var profile = response.Data ?? new();
        if (profile.Success)
        {
            await _cacheService.CacheProfileAsync(masterId, profile);
            OnProfileUpdated?.Invoke(this, EventArgs.Empty);
        }
        return profile;
    }

    public async Task<List<ChatHistoryItem>> GetConversationHistoryAsync(string masterId, int limit = 20)
    {
        var cached = await _cacheService.GetConversationAsync(masterId);
        if (cached.Count == 0)
            return new();

        return cached
            .OrderByDescending(m => m.Time)
            .Take(limit)
            .Select(m => new ChatHistoryItem
            {
                Role = m.IsUser ? "user" : "assistant",
                Content = m.Content
            })
            .ToList();
    }

    public async Task<List<ChatHistoryItem>> GetConversationsFromServerAsync(string masterId, int limit = 100)
    {
        var transport = await CreateTransportAsync();
        var response = await transport.GetJsonAsync<ConversationHistoryResponse>($"/api/master/{masterId}/conversations?limit={limit}");
        if (!response.IsSuccess)
            throw new InvalidOperationException(response.ErrorMessage ?? "获取对话历史失败");

        var items = response.Data?.Items ?? new();
        return items.Select(i => new ChatHistoryItem
        {
            Role = i.Role,
            Content = i.Content
        }).ToList();
    }

    public async Task<bool> SyncConversationsToServerAsync(string masterId, List<ChatHistoryItem> conversations)
    {
        if (conversations.Count == 0)
            return true;

        var transport = await CreateTransportAsync();
        var request = new ConversationSyncRequest
        {
            Items = conversations.Select(c => new ChatHistoryItem
            {
                Role = c.Role,
                Content = c.Content,
                Stage = "",
                CreatedAt = DateTime.Now
            }).ToList()
        };
        var response = await transport.PostJsonAsync<ConversationSyncResponse>($"/api/master/{masterId}/conversations/sync", request);
        return response.IsSuccess && response.Data?.Success == true;
    }

    public async Task<bool> CheckAiConfiguredAsync()
    {
        try
        {
            var transport = await CreateTransportAsync();
            var response = await transport.GetJsonAsync<List<object>>("/api/ai/providers");
            if (!response.IsSuccess)
                return false;

            var providers = response.Data as List<object>;
            return providers != null && providers.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<VaultFocusItem>> GetVaultFocusAsync(string masterId)
    {
        var transport = await CreateTransportAsync();
        var response = await transport.GetJsonAsync<VaultFocusListResponse>($"/api/master/{masterId}/vault-focus");
        if (!response.IsSuccess)
            throw new InvalidOperationException(response.ErrorMessage ?? "获取知识库关联失败");

        return response.Data?.Items ?? new();
    }

    public async Task<List<VaultListItem>> GetAvailableVaultsAsync()
    {
        var transport = await CreateTransportAsync();
        var response = await transport.GetJsonAsync<VaultListResponse>("/api/vault-settings/vaults");
        if (!response.IsSuccess)
            throw new InvalidOperationException(response.ErrorMessage ?? "获取知识库列表失败");

        return response.Data?.Vaults ?? new();
    }

    public async Task<bool> FocusVaultAsync(string masterId, string vaultId, string? stageName = null)
    {
        var transport = await CreateTransportAsync();
        var request = new VaultFocusUpdateRequest
        {
            VaultId = vaultId,
            State = "focused",
            StageName = stageName
        };
        var response = await transport.PostJsonAsync<VaultFocusUpdateResponse>($"/api/master/{masterId}/vault-focus", request);
        return response.IsSuccess && response.Data?.Success == true;
    }

    public async Task<bool> RemoveVaultFocusAsync(string masterId, string vaultId)
    {
        var transport = await CreateTransportAsync();
        var response = await transport.DeleteJsonAsync<VaultFocusUpdateResponse>($"/api/master/{masterId}/vault-focus/{vaultId}");
        return response.IsSuccess && response.Data?.Success == true;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string masterId, string message, string stage,
        List<ChatHistoryItem>? history = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var server = await _serverStore.GetCurrentServerAsync();
        if (server == null)
            throw new InvalidOperationException("未选择服务器");

        var baseUrl = HttpTransport.NormalizeBaseUrl(server.HttpUrl);
        var url = $"{baseUrl}/api/master/chat/stream";

        var historyForRequest = history ?? await GetConversationHistoryAsync(masterId);

        var requestBody = new MasterChatRequest
        {
            MasterId = masterId,
            Message = message,
            Stage = stage,
            History = historyForRequest
        };

        var jsonBody = JsonSerializer.Serialize(requestBody);

        var userMessage = new ChatMessage
        {
            Id = $"user_{DateTime.Now:HH:mm:ss}",
            IsUser = true,
            Content = message,
            Time = DateTime.Now
        };
        await _cacheService.AppendMessageAsync(masterId, userMessage);

        var result = await TryCollectStreamAsync(url, jsonBody, baseUrl, ct);

        if (!result.Success)
        {
            result = await TryCollectFallbackAsync(url, jsonBody, baseUrl, ct);
        }

        if (!result.Success && result.Error != null)
        {
            throw result.Error;
        }

        foreach (var chunk in result.Chunks)
        {
            yield return chunk;
        }
    }

    private async Task<(bool Success, List<string> Chunks, Exception? Error)> TryCollectStreamAsync(
        string url, string jsonBody, string baseUrl, CancellationToken ct)
    {
        var chunks = new List<string>();
        try
        {
            await foreach (var chunk in TryStreamChatInternalAsync(url, jsonBody, baseUrl, ct))
            {
                chunks.Add(chunk);
            }
            return (true, chunks, null);
        }
        catch (Exception ex)
        {
            return (false, chunks, ex);
        }
    }

    private async Task<(bool Success, List<string> Chunks, Exception? Error)> TryCollectFallbackAsync(
        string url, string jsonBody, string baseUrl, CancellationToken ct)
    {
        var chunks = new List<string>();
        try
        {
            await foreach (var chunk in FallbackNonStreamChatAsync(url, jsonBody, baseUrl, ct))
            {
                chunks.Add(chunk);
            }
            return (true, chunks, null);
        }
        catch (Exception ex)
        {
            return (false, chunks, ex);
        }
    }

    private async IAsyncEnumerable<string> TryStreamChatInternalAsync(
        string url, string jsonBody, string baseUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 通过 SDK HttpTransport.PostStreamAsync 发起，而非直接操作 HttpClient
        var transport = new HttpTransport(_httpClient, _signer, baseUrl);
        using var response = await transport.PostStreamAsync("/api/master/chat/stream",
            JsonSerializer.Deserialize<MasterChatRequest>(jsonBody), ct: ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var fullResponse = "";
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var data = line.Substring(6).Trim();
            if (data == "")
                continue;

            var parsed = TryParseContent(data);
            if (parsed != null)
            {
                fullResponse += parsed;
                yield return parsed;
            }
        }

        if (!string.IsNullOrEmpty(fullResponse))
        {
            var masterId = ExtractMasterIdFromJson(jsonBody);
            var assistantMessage = new ChatMessage
            {
                Id = $"master_{DateTime.Now:HH:mm:ss}",
                IsUser = false,
                Content = fullResponse,
                Time = DateTime.Now
            };
            await _cacheService.AppendMessageAsync(masterId, assistantMessage);
        }
    }

    private async IAsyncEnumerable<string> FallbackNonStreamChatAsync(
        string url, string jsonBody, string baseUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 通过 SDK HttpTransport.PostStreamAsync 发起非流式后备请求
        var transport = new HttpTransport(_httpClient, _signer, baseUrl);
        using var response = await transport.PostStreamAsync("/api/master/chat/stream",
            JsonSerializer.Deserialize<MasterChatRequest>(jsonBody), ct: ct);

        var fullContent = await response.Content.ReadAsStringAsync(ct);

        var fullResponse = "";
        using var reader = new StringReader(fullContent);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var data = line.Substring(6).Trim();
            if (data == "")
                continue;

            var parsed = TryParseContent(data);
            if (parsed != null)
                fullResponse += parsed;
        }

        if (string.IsNullOrEmpty(fullResponse))
        {
            var directParsed = TryParseContent(fullContent.Trim());
            if (directParsed != null)
                fullResponse = directParsed;
        }

        if (!string.IsNullOrEmpty(fullResponse))
        {
            var masterId = ExtractMasterIdFromJson(jsonBody);
            var assistantMessage = new ChatMessage
            {
                Id = $"master_{DateTime.Now:HH:mm:ss}",
                IsUser = false,
                Content = fullResponse,
                Time = DateTime.Now
            };
            await _cacheService.AppendMessageAsync(masterId, assistantMessage);
            yield return fullResponse;
        }
    }

    private static string ExtractMasterIdFromJson(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (doc.RootElement.TryGetProperty("MasterId", out var id))
                return id.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private static string? TryParseContent(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("content", out var content))
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            return null;
        }
        catch
        {
            return data;
        }
    }
}

public class UpdateProfileRequest
{
    public string? Foundation { get; set; }
    public string? LearningStyle { get; set; }
    public string? Strengths { get; set; }
    public string? Weaknesses { get; set; }
}
