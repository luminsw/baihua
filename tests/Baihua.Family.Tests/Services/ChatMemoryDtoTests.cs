using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class ChatMemoryDtoTests
{
    [Fact]
    public void MemoryContext_Defaults_NullSummaryEmptyHistory()
    {
        var ctx = new MemoryContext();

        Assert.Null(ctx.Summary);
        Assert.NotNull(ctx.RecentHistory);
        Assert.Empty(ctx.RecentHistory);
    }

    [Fact]
    public void MemoryContext_SetProperties_StoresValues()
    {
        var ctx = new MemoryContext
        {
            Summary = "用户讨论了中医方剂",
            RecentHistory = new List<Baihua.Contracts.Ai.ChatHistoryItem>
            {
                new() { Role = "user", Content = "你好" },
                new() { Role = "assistant", Content = "你好！" }
            }
        };

        Assert.Equal("用户讨论了中医方剂", ctx.Summary);
        Assert.Equal(2, ctx.RecentHistory.Count);
        Assert.Equal("user", ctx.RecentHistory[0].Role);
    }

    [Fact]
    public void RetrievedMemory_Defaults_EmptyContentZeroSimilarity()
    {
        var mem = new RetrievedMemory();

        Assert.Equal("", mem.UserContent);
        Assert.Equal("", mem.AssistantContent);
        Assert.Equal(0.0, mem.Similarity);
        Assert.Equal(0, mem.Round);
    }

    [Fact]
    public void RetrievedMemory_SetProperties_StoresValues()
    {
        var mem = new RetrievedMemory
        {
            UserContent = "什么是桂枝汤？",
            AssistantContent = "桂枝汤是一种解表剂...",
            Similarity = 0.85,
            Round = 3
        };

        Assert.Equal("什么是桂枝汤？", mem.UserContent);
        Assert.Equal("桂枝汤是一种解表剂...", mem.AssistantContent);
        Assert.Equal(0.85, mem.Similarity);
        Assert.Equal(3, mem.Round);
    }
}
