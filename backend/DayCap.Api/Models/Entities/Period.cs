namespace DayCap.Api.Models.Entities;

/// <summary>
/// 一個預算週期。資料庫只存明確的起訖日（§3.4）與這期發生的「事實」（§2.2）；
/// 分類額度、每日時段金額、固定支出都不存，每次讀取時由「當天有效的設定版本 + 行事曆」
/// 算出（見 <see cref="Services.PlanBuilder"/>），再由 <see cref="Services.BudgetEngine"/> 重播事實。
/// </summary>
public class BudgetPeriod
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>發薪日按過「確認本期薪資」（沒有變動也算）。</summary>
    public DateTime? IncomeConfirmedAt { get; set; }

    /// <summary>期末結算時間。自動從存款扣已依規格 §2.1 關閉，SettlementAmount 固定為 0（舊資料可能有值）。</summary>
    public DateTime? SettledAt { get; set; }
    public decimal? SettlementAmount { get; set; }

    public List<Entry> Entries { get; set; } = [];
    public List<PoolTransfer> PoolTransfers { get; set; } = [];
    public List<IncomeAdjustment> IncomeAdjustments { get; set; } = [];

    // ---- 舊欄位（已不使用）：收入改由設定版本決定 ----
    public int Income { get; set; }
    public DateTime? RebuiltAt { get; set; }
}

/// <summary>
/// 所有「事實」共用的只新增規則（§2.2）：
/// 修改＝新增一筆 ReplacesId 指向舊的；刪除＝新增一筆 IsVoid=true、ReplacesId 指向要刪的。
/// 有效的事實＝不是作廢紀錄、也沒有被任何一筆取代的那些。
/// </summary>
public interface IFact
{
    int Id { get; }
    int? ReplacesId { get; }
    bool IsVoid { get; }
    DateTime CreatedAt { get; }
}

public static class FactExtensions
{
    public static List<T> Active<T>(this IEnumerable<T> facts) where T : IFact
    {
        var list = facts.ToList();
        var replaced = list.Where(f => f.ReplacesId is not null).Select(f => f.ReplacesId!.Value).ToHashSet();
        return list.Where(f => !f.IsVoid && !replaced.Contains(f.Id)).ToList();
    }
}

public enum EntryInputMode { Actual, Overage }

/// <summary>
/// 一筆回報（事實）。SlotId 有值 = 針對某天某時段回報實際花費；null = 額外花費。
/// Date 是邏輯日；OccurredAtUtc / TimeZoneId 是發生時刻（有的話，§3.3），CreatedAt 是登錄時間。
/// </summary>
public class Entry : IFact
{
    public int Id { get; set; }
    public int PeriodId { get; set; }
    public DateOnly Date { get; set; }
    public int CategoryId { get; set; }
    public int? SlotId { get; set; }

    public EntryInputMode InputMode { get; set; }

    /// <summary>使用者輸入的數字：實際價格，或超支金額（可為負，代表省下）。</summary>
    public decimal InputAmount { get; set; }

    /// <summary>超支時先從待定區扣；不夠或不勾，才攤到之後的日子。</summary>
    public bool UsePool { get; set; } = true;

    public string? Note { get; set; }
    public bool IsSubscription { get; set; }

    public DateTime? OccurredAtUtc { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}

/// <summary>
/// 待定區調整（事實）。CategoryId 為 null：錢進出待定區本身；
/// 有 CategoryId：把待定區的錢分配給那個分類（額度增加，每日類加到之後每天的時段上）。
/// </summary>
public class PoolTransfer : IFact
{
    public int Id { get; set; }
    public int PeriodId { get; set; }
    public DateOnly Date { get; set; }
    public int? CategoryId { get; set; }
    public decimal Amount { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}

public enum IncomeAdjustmentKind { SickLeave, PersonalLeave, MenstrualLeave, Overtime, Bonus, OtherDeduction, OtherAddition }

/// <summary>
/// 這期薪資的加減（事實）：請假扣薪、加班費、獎金……。Amount 已帶正負號（扣薪為負）。
/// 天數 / 時數只是紀錄怎麼算出來的，實際以 Amount 為準（使用者可以手改）。
/// </summary>
public class IncomeAdjustment : IFact
{
    public int Id { get; set; }
    public int PeriodId { get; set; }
    public IncomeAdjustmentKind Kind { get; set; }
    public decimal? Days { get; set; }
    public decimal? Hours { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}

// ---- 以下是重播時算出來的計畫（不存資料庫）----

/// <summary>這期某個分類的額度（由期間第一天有效的設定版本算出）。</summary>
public class PeriodCategory
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = "";
    public CategoryGroup Group { get; set; }
    public BudgetMode Mode { get; set; }
    public decimal Budget { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>某一天某個時段的排程金額（由那一天有效的設定版本算出，未含攤提調整）。</summary>
public class DayAllocation
{
    public DateOnly Date { get; set; }
    public int CategoryId { get; set; }
    public int SlotId { get; set; }
    public string SlotName { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsHoliday { get; set; }
    public decimal Planned { get; set; }

    /// <summary>若這期第一天的設定一路用到底，這一格會是多少（拿來把期中設定變更的影響另外列出）。</summary>
    public decimal BaselinePlanned { get; set; }

    /// <summary>這一格的金額來自哪個設定版本的生效日。</summary>
    public DateOnly VersionEffectiveFrom { get; set; }
}

public class PeriodFixedCharge
{
    public int CategoryId { get; set; }
    public int? FixedItemId { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public DateOnly? DueDate { get; set; }
    public bool IsSubscription { get; set; }
}
