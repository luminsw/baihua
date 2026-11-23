namespace MobileContract.VaultSync;

/// <summary>
/// 一次同步操作的汇总结果。
/// </summary>
public record SyncResult(
    int Cursor = 0,
    int TotalFiles = 0,
    int Downloaded = 0,
    int Skipped = 0,
    int Deleted = 0,
    int Failed = 0,
    IReadOnlyList<string>? Errors = null);
