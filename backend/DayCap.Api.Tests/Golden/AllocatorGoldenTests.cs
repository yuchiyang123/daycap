using DayCap.Api.Services.Allocation;

namespace DayCap.Api.Tests.Golden;

/// <summary>
/// REQUIREMENTS.md §23.1 分配器黃金情境。
///
/// 規則（§0.1、§0.2）：
/// - Allocate 由使用者實作。
/// - 預期值欄（Expected*）由使用者親手計算後填入，AI 不填、不改。
/// - 不變量（§8.1 的 6 條）每個情境都斷言，不依賴預期值。
///
/// 目前 Allocate 尚未實作，整組先 Skip。實作完成後拿掉 Skip，先讓不變量全過，再逐列填預期值。
/// </summary>
public class AllocatorGoldenTests
{
    private const string SkipReason = "Allocate 尚未實作；實作後拿掉 Skip，並親手填入預期值（§0.2）";

    /// <param name="Id">情境編號（§23.1）。</param>
    /// <param name="Description">說明。</param>
    /// <param name="ExpectedUnitAmount">預期每格單價。留空 = 還沒填。</param>
    /// <param name="ExpectedLeftover">預期零頭。留空 = 還沒填。</param>
    /// <param name="ExpectErrors">預期會不會有 Errors。留空 = 還沒填。</param>
    public record Case(
        string Id,
        string Description,
        decimal Budget,
        AllocationCell[] Cells,
        decimal RoundingUnit,
        string? ReleasedShareReceiverKey,
        IReadOnlyDictionary<string, decimal>? ExpectedUnitAmount,
        decimal? ExpectedLeftover,
        bool? ExpectErrors)
    {
        public override string ToString() => $"{Id} {Description}";
    }

    // ---- 輸入組件（只是把 §23.1 的文字翻成格子，沒有任何計算結果） ----

    /// <summary>A1 的格子：平日 22 天、假日 8 天；早:午:晚 = 1:2:2，假日權重 ×1.2；平日午餐鎖定 150。</summary>
    private static AllocationCell[] A1Cells(int weekdayDays = 22, int holidayDays = 8) =>
    [
        new("weekday-breakfast", weekdayDays, 1m, null, null, 0),
        new("weekday-lunch", weekdayDays, 2m, 150m, null, 0),
        new("weekday-dinner", weekdayDays, 2m, null, null, 0),
        new("holiday-breakfast", holidayDays, 1m * 1.2m, null, null, 0),
        new("holiday-lunch", holidayDays, 2m * 1.2m, null, null, 0),
        new("holiday-dinner", holidayDays, 2m * 1.2m, null, null, 0),
    ];

    private static AllocationCell[] With(AllocationCell[] cells, string key, Func<AllocationCell, AllocationCell> change) =>
        cells.Select(c => c.Key == key ? change(c) : c).ToArray();

    /// <summary>
    /// A8/A9 的大區塊：天數 1、取整 1。儲蓄是受保護層（層級數字越小越晚被擠壓 → 0），其餘一般層 1。
    /// 房租的權重 25 取自 §20.5 範本的房租 %，只在「鎖定值和範本份額的差」要用到；鎖定時單價以鎖定值為準。
    /// </summary>
    private static AllocationCell[] A8Cells(decimal rentLocked) =>
    [
        new("rent", 1, 25m, rentLocked, null, 1),
        new("savings", 1, 32m, null, null, 0),
        new("food", 1, 25m, null, null, 1),
        new("transport", 1, 8m, null, null, 1),
        new("leisure", 1, 10m, null, null, 1),
    ];

    public static TheoryData<Case> Cases() => new()
    {
        new Case("A1", "12,000；平日 22、假日 8；權重 1/2/2、假日 ×1.2；平午鎖定 150；取整 5（文件已附驗算值，請親手確認後填入）",
            12000m, A1Cells(), 5m, null,
            ExpectedUnitAmount: null, ExpectedLeftover: null, ExpectErrors: null),

        new Case("A2", "同 A1，假日 0 天：天數 0 排除",
            12000m, A1Cells(holidayDays: 0), 5m, null,
            null, null, null),

        new Case("A3", "同 A1，平早底線 90：底線夾住重算",
            12000m, With(A1Cells(), "weekday-breakfast", c => c with { Floor = 90m }), 5m, null,
            null, null, null),

        new Case("A4", "同 A1，平午鎖定 600：鎖定超過預算 → 錯誤",
            12000m, With(A1Cells(), "weekday-lunch", c => c with { LockedUnitAmount = 600m }), 5m, null,
            null, null, null),

        // A5「全部鎖定」：文件沒有寫每格鎖在多少。請決定鎖定值後把 LockedUnitAmount 填進去。
        new Case("A5", "同 A1，全部鎖定：無可分格，只顯示差額（輸入的鎖定值待你決定）",
            12000m, A1Cells(), 5m, null,
            null, null, null),

        new Case("A6", "權重全為 0；預算為 0：錯誤，不得除以零",
            0m, A1Cells().Select(c => c with { Weight = 0m, LockedUnitAmount = null }).ToArray(), 5m, null,
            null, null, null),

        // A7「權重同分」：這裡假設為六格權重都相同、沒有鎖定；若你指的是別的組合請改輸入。
        new Case("A7", "同 A1，權重同分：依固定次序決勝",
            12000m, A1Cells().Select(c => c with { Weight = 1m, LockedUnitAmount = null }).ToArray(), 5m, null,
            null, null, null),

        new Case("A8", "收入 40,000；房租鎖定 15,000；儲蓄受保護層權重 32；餐費 25、交通 8、娛樂 10：分層吸收",
            40000m, A8Cells(15000m), 1m, "savings",
            null, null, null),

        new Case("A9", "同 A8，房租鎖定 8,000：釋出份額進儲蓄",
            40000m, A8Cells(8000m), 1m, "savings",
            null, null, null),

        // A10～A12 依賴範本套用、微調、模式切換的畫面邏輯（§8.2、§20.5），輸入待那些功能定案後補。
    };

    [Theory(Skip = SkipReason)]
    [MemberData(nameof(Cases))]
    public void Invariants_hold(Case c)
    {
        var result = Allocator.Allocate(c.Budget, c.Cells, c.RoundingUnit, c.ReleasedShareReceiverKey);
        if (result.Errors.Count > 0) return; // 有錯誤時不回傳分配結果，不檢查不變量

        var active = c.Cells.Where(x => x.Days > 0).ToList();

        // 1. Σ(單價 × 天數) ＋ 零頭 ＝ 總額
        Assert.Equal(c.Budget, active.Sum(x => result.UnitAmount[x.Key] * x.Days) + result.Leftover);

        // 2. 鎖定格輸出 ＝ 鎖定值
        foreach (var x in active.Where(x => x.LockedUnitAmount is not null))
            Assert.Equal(x.LockedUnitAmount!.Value, result.UnitAmount[x.Key]);

        // 3. 每格單價 ≥ 底線（沒有錯誤時）
        foreach (var x in active.Where(x => x.Floor is not null))
            Assert.True(result.UnitAmount[x.Key] >= x.Floor!.Value, $"{x.Key} 低於底線");

        // 4. 每格單價為取整單位的整數倍
        foreach (var x in active)
            Assert.Equal(0m, result.UnitAmount[x.Key] % c.RoundingUnit);

        // 5. 相同輸入跑兩次結果完全相同
        var again = Allocator.Allocate(c.Budget, c.Cells, c.RoundingUnit, c.ReleasedShareReceiverKey);
        Assert.Equal(result.Leftover, again.Leftover);
        Assert.Equal(result.UnitAmount.OrderBy(k => k.Key), again.UnitAmount.OrderBy(k => k.Key));

        // 6. 零頭 < 任一可加格（取整單位 × 天數）的最小值
        var addable = active.Where(x => x.LockedUnitAmount is null).Select(x => c.RoundingUnit * x.Days).ToList();
        if (addable.Count > 0) Assert.True(result.Leftover < addable.Min(), "零頭還夠再加一格");
    }

    [Theory(Skip = SkipReason)]
    [MemberData(nameof(Cases))]
    public void Matches_hand_calculated_expectations(Case c)
    {
        if (c.ExpectedUnitAmount is null && c.ExpectedLeftover is null && c.ExpectErrors is null)
        {
            Assert.Fail($"{c.Id} 的預期值還沒填（§0.2：由你親手計算）。");
        }

        var result = Allocator.Allocate(c.Budget, c.Cells, c.RoundingUnit, c.ReleasedShareReceiverKey);

        if (c.ExpectErrors is { } expectErrors) Assert.Equal(expectErrors, result.Errors.Count > 0);
        if (c.ExpectedLeftover is { } leftover) Assert.Equal(leftover, result.Leftover);
        if (c.ExpectedUnitAmount is { } expected)
        {
            foreach (var (key, amount) in expected) Assert.Equal(amount, result.UnitAmount[key]);
        }
    }
}
