namespace BaihuaSdk.Transport;

/// <summary>
/// 通用 API 响应封装。
/// </summary>
public class ApiResponse<T>
{
    public bool IsSuccess { get; init; }
    public int StatusCode { get; init; }
    public T? Data { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawBody { get; init; }

    public static ApiResponse<T> Ok(T data, int statusCode = 200) => new()
    {
        IsSuccess = true, StatusCode = statusCode, Data = data
    };

    public static ApiResponse<T> Fail(int statusCode, string error, string? rawBody = null) => new()
    {
        IsSuccess = false, StatusCode = statusCode, ErrorMessage = error, RawBody = rawBody
    };
}

/// <summary>
/// 无数据体的 API 响应。
/// </summary>
public class ApiResponse
{
    public bool IsSuccess { get; init; }
    public int StatusCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static ApiResponse Ok(int statusCode = 200) => new()
    {
        IsSuccess = true, StatusCode = statusCode
    };

    public static ApiResponse Fail(int statusCode, string error) => new()
    {
        IsSuccess = false, StatusCode = statusCode, ErrorMessage = error
    };
}
