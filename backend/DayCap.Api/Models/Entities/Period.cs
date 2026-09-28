namespace DayCap.Api.Models.Entities;

/// <summary>
/// 一個預算週期（通常一個月）。建立時把當下的設定「快照」下來：收入、各分類額度、
/// 每一天每一個時段的排程金額。之後改設定不會回頭改掉已經過去的日子。
///
/// 超支攤提、待定區餘額這些都不存，每次讀取時用 <see cref="Services.BudgetEngine"/>
/// 依序重播所有回報算出來——刪掉或修改一筆回報，後續影響自然就跟著修正。
/// </summary>
public class BudgetPeriod
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int Income { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RebuiltAt { get; set; }

    public List<PeriodCategory> Categories { get; set; } = [];
    public List<DayAllocation> Allocations { get; set; } = [];
    public List<PeriodFixedCharge> FixedCharges { get; set; } = [];
    public List<Entry> Entries { get; set; } = [];
    public List<PoolTransfer> PoolTransfers { get; set; } = [];
}

public class PeriodCategory
{
    public int Id { get; set; }
    public int PeriodId { get; set; }
    public int CategoryId { get; set; }
    public string Name { get; set; } = "";
    public CategoryGroup Group { get; set; }
    public BudgetMode Mode { get; set; }

    /// <summary>Fixed = 固定項目加總；Daily/Envelope = 收入 × 百分比。</summary>
    public int Budget { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>某一天某個時段的排程金額（未含攤提調整）。</summary>
public class DayAllocation
{
    public int Id { get; set; }
    public int PeriodId { get; set; }
    public DateOnly Date { get; set; }
    public int CategoryId { get; set; }
    public int SlotId { get; set; }
    public string SlotName { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsHoliday { get; set; }
    public int Planned { get; set; }
}

public class PeriodFixedCharge
{
    public int Id { get; set; }
    public int PeriodId { get; set; }
    public int CategoryId { get; set; }
    public int? FixedItemId { get; set; }
    public string Name { get; set; } = "";
    public int Amount { get; set; }
    public DateOnly? DueDate { get; set; }
    public bool IsSubscription { get; set; }
}

public enum EntryInputMode { Actual, Overage }

/// <summary>
/// 一筆回報。SlotId 有值 = 針對某天某時段（例如早餐）回報實際花費，
/// 同一天同一時段只會有一筆（再回報就是修改）；SlotId 為 null = 額外花費。
/// </summary>
public class Entry
{
    public int Id { get; set; }
    public int PeriodId { get; set; }
    public DateOnly Date { get; set; }
    public int CategoryId { get; set; }
    public int? SlotId { get; set; }

    public EntryInputMode InputMode { get; set; }

    /// <summary>使用者輸入的數字：實際價格，或超支金額（可為負，代表省下）。</summary>
    public int InputAmount { get; set; }

    /// <summary>超支時先從待定區扣；不夠或不勾，才攤到之後的日子。</summary>
    public bool UsePool { get; set; } = true;

    public string? Note { get; set; }
    public bool IsSubscription { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>手動調整待定區（例如月底把結餘轉去儲蓄，或臨時補錢進來）。</summary>
public class PoolTransfer
{
    public int Id { get; set; }
    public int PeriodId { get; set; }
    public DateOnly Date { get; set; }
    public int Amount { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
