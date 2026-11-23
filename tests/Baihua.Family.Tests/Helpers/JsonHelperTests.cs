using System.Text.Json;
using Baihua.AI.Provider;
using Xunit;

namespace Baihua.Family.Tests.Helpers;

public class JsonHelperTests
{
    #region GetString

    [Fact]
    public void GetString_PropertyExists_ReturnsValue()
    {
        var json = """{"name":"test","value":"123"}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetString(element, "name");

        Assert.Equal("test", result);
    }

    [Fact]
    public void GetString_PropertyNotExists_ReturnsEmpty()
    {
        var json = """{"name":"test"}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetString(element, "missing");

        Assert.Equal("", result);
    }

    [Fact]
    public void GetString_PropertyNotString_ReturnsEmpty()
    {
        var json = """{"count":42}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetString(element, "count");

        Assert.Equal("", result);
    }

    [Fact]
    public void GetString_NullValue_ReturnsEmpty()
    {
        var json = """{"name":null}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetString(element, "name");

        Assert.Equal("", result);
    }

    [Fact]
    public void GetString_ChineseCharacters_ReturnsCorrectly()
    {
        var json = """{"title":"中文标题"}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetString(element, "title");

        Assert.Equal("中文标题", result);
    }

    #endregion

    #region GetLong

    [Fact]
    public void GetLong_PropertyExists_ReturnsValue()
    {
        var json = """{"count":42}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetLong(element, "count");

        Assert.Equal(42, result);
    }

    [Fact]
    public void GetLong_PropertyNotExists_ReturnsZero()
    {
        var json = """{"name":"test"}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetLong(element, "missing");

        Assert.Equal(0, result);
    }

    [Fact]
    public void GetLong_PropertyNotNumber_ReturnsZero()
    {
        var json = """{"name":"test"}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetLong(element, "name");

        Assert.Equal(0, result);
    }

    [Fact]
    public void GetLong_Int32Value_ReturnsValue()
    {
        var json = """{"small":100}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetLong(element, "small");

        Assert.Equal(100, result);
    }

    [Fact]
    public void GetLong_LargeValue_ReturnsValue()
    {
        var json = """{"large":9223372036854775807}""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = JsonHelper.GetLong(element, "large");

        Assert.Equal(long.MaxValue, result);
    }

    #endregion

    #region JsonSerializerOptions

    [Fact]
    public void Indented_HasWriteIndented()
    {
        Assert.True(JsonHelper.Indented.WriteIndented);
    }

    [Fact]
    public void Compact_HasNoWriteIndented()
    {
        Assert.False(JsonHelper.Compact.WriteIndented);
    }

    [Fact]
    public void CaseInsensitive_IsCaseInsensitive()
    {
        Assert.True(JsonHelper.CaseInsensitive.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void IndentedUnicode_HasUnicodeEncoder()
    {
        Assert.True(JsonHelper.IndentedUnicode.WriteIndented);
        Assert.NotNull(JsonHelper.IndentedUnicode.Encoder);
    }

    #endregion
}