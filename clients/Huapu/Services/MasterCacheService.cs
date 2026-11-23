using System.Text.Json;
using BaihuaSdk.Storage;

namespace MobileApp.Maui.Services;

public interface IMasterCacheService
{
    Task<List<ChatMessage>> GetConversationAsync(string masterId);
    Task SaveConversationAsync(string masterId, List<ChatMessage> messages);
    Task AppendMessageAsync(string masterId, ChatMessage message);
    Task ClearConversationAsync(string masterId);
    Task<ApprenticeProfileResponse?> GetCachedProfileAsync(string masterId);
    Task CacheProfileAsync(string masterId, ApprenticeProfileResponse profile);
    Task<List<MasterListItem>> GetCachedMastersAsync();
    Task CacheMastersAsync(List<MasterListItem> masters);
    Task ClearAllCacheAsync();
    Task<bool> GetDisclaimerAcceptedAsync(string masterId);
    Task SetDisclaimerAcceptedAsync(string masterId, bool accepted);
}

public class MasterCacheService : IMasterCacheService
{
    private readonly ISecureStore _secureStore;
    private const string ConversationPrefix = "master_conv_";
    private const string ProfilePrefix = "master_profile_";
    private const string MastersKey = "master_list";
    private const string DisclaimerPrefix = "master_disclaimer_";
    private const int MaxMessagesPerConversation = 200;

    public MasterCacheService(ISecureStore secureStore)
    {
        _secureStore = secureStore;
    }

    public async Task<List<ChatMessage>> GetConversationAsync(string masterId)
    {
        var key = ConversationPrefix + masterId;
        var json = await _secureStore.GetAsync(key);
        if (string.IsNullOrEmpty(json))
            return new();

        try
        {
            var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json);
            return messages ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task SaveConversationAsync(string masterId, List<ChatMessage> messages)
    {
        var trimmed = messages.Count > MaxMessagesPerConversation
            ? messages.Skip(messages.Count - MaxMessagesPerConversation).ToList()
            : messages;

        var json = JsonSerializer.Serialize(trimmed);
        var key = ConversationPrefix + masterId;
        await _secureStore.SetAsync(key, json);
    }

    public async Task AppendMessageAsync(string masterId, ChatMessage message)
    {
        var messages = await GetConversationAsync(masterId);
        messages.Add(message);

        if (messages.Count > MaxMessagesPerConversation)
            messages = messages.Skip(messages.Count - MaxMessagesPerConversation).ToList();

        var json = JsonSerializer.Serialize(messages);
        var key = ConversationPrefix + masterId;
        await _secureStore.SetAsync(key, json);
    }

    public async Task ClearConversationAsync(string masterId)
    {
        var key = ConversationPrefix + masterId;
        await _secureStore.RemoveAsync(key);
    }

    public async Task<ApprenticeProfileResponse?> GetCachedProfileAsync(string masterId)
    {
        var key = ProfilePrefix + masterId;
        var json = await _secureStore.GetAsync(key);
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ApprenticeProfileResponse>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task CacheProfileAsync(string masterId, ApprenticeProfileResponse profile)
    {
        var key = ProfilePrefix + masterId;
        var json = JsonSerializer.Serialize(profile);
        await _secureStore.SetAsync(key, json);
    }

    public async Task<List<MasterListItem>> GetCachedMastersAsync()
    {
        var json = await _secureStore.GetAsync(MastersKey);
        if (string.IsNullOrEmpty(json))
            return new();

        try
        {
            var masters = JsonSerializer.Deserialize<List<MasterListItem>>(json);
            return masters ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task CacheMastersAsync(List<MasterListItem> masters)
    {
        var json = JsonSerializer.Serialize(masters);
        await _secureStore.SetAsync(MastersKey, json);
    }

    public async Task ClearAllCacheAsync()
    {
        await _secureStore.RemoveAsync(MastersKey);
    }

    public async Task<bool> GetDisclaimerAcceptedAsync(string masterId)
    {
        var key = DisclaimerPrefix + masterId;
        var val = await _secureStore.GetAsync(key);
        return val == "1";
    }

    public async Task SetDisclaimerAcceptedAsync(string masterId, bool accepted)
    {
        var key = DisclaimerPrefix + masterId;
        await _secureStore.SetAsync(key, accepted ? "1" : "0");
    }
}
