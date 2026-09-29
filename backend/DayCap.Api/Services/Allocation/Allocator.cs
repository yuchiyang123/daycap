namespace DayCap.Api.Services.Allocation;

/// <summary>
/// 分配器的一格（見 docs/REQUIREMENTS.md §8.1）。
/// </summary>
/// <param name="Key">鍵值，例如 "weekday-lunch"。</param>
/// <param name="Days">天數；0 的格子會被排除。</param>
/// <param name="Weight">權重。</param>
/// <param name="LockedUnitAmount">鎖定的單價（每餐／每次）；null = 不鎖定。</param>
/// <param name="Floor">底線（單價下限）；null = 沒有底線。</param>
/// <param name="Tier">層級，數字越小越晚被擠壓；單層場景全部填同值。</param>
public record AllocationCell(
    string Key,
    int Days,
    decimal Weight,
    decimal? LockedUnitAmount,
    decimal? Floor,
    int Tier);

/// <param name="UnitAmount">每格單價（每餐／每次，不是每日）。Errors 非空時為空字典。</param>
/// <param name="Leftover">零頭，進待分配池。</param>
public record AllocationResult(
    IReadOnlyDictionary<string, decimal> UnitAmount,
    decimal Leftover,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 分層比例分配器。用在：餐費分早午晚、範本套用分大區塊、微調重算。
///
/// 依 REQUIREMENTS.md §0.1，這個方法由使用者親手實作；AI 不撰寫、不補完實作。
/// 演算法、不變量、開發步驟見 §8.1。實作完成後由使用者補上演算法說明。
/// </summary>
public static class Allocator
{
    public static AllocationResult Allocate(
        decimal budget,
        IReadOnlyList<AllocationCell> cells,
        decimal roundingUnit,
        string? releasedShareReceiverKey = null)
        => throw new NotImplementedException();
}
