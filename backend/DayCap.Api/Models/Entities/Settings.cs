namespace DayCap.Api.Models.Entities;

public enum CategoryGroup { Food, Clothing, Housing, Transport, Education, Leisure, Savings, Other }

/// <summary>
/// Fixed：鎖死，金額 = 固定項目加總，不能回報超支；
/// Daily：拆成每日時段（早餐/午餐…），分上班日/假日金額，是這個 app 的核心；
/// Envelope：整月一個額度，花了就扣，沒有每日上限（衣、樂這類）。
/// </summary>
public enum BudgetMode { Fixed, Daily, Envelope }

public enum BillingCycle { Monthly, Yearly, Quarterly }

public class UserProfile
{
    public string UserId { get; set; } = "";
    public int MonthlyIncome { get; set; }

    /// <summary>週期起始日（發薪日），1–28。週期 = 這天到下個月同一天的前一天。</summary>
    public int CycleStartDay { get; set; } = 1;

    /// <summary>
    /// 從哪天開始算（例如拿到第一份薪水那天）。這天之前完全不排額度、不開週期；
    /// 第一期從這天開始，到下一個週期起始日的前一天（可能不滿一個月）。null = 馬上開始。
    /// </summary>
    public DateOnly? StartDate { get; set; }

    /// <summary>期末結算用的存款帳戶：超支從這裡扣；勾了 SurplusToAccount 的話結餘存進這裡。</summary>
    public int? SettlementAccountId { get; set; }

    public bool SurplusToAccount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Category
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public CategoryGroup Group { get; set; }
    public BudgetMode Mode { get; set; }

    /// <summary>佔月收入的百分比（0–100）。Fixed 模式不用這個欄位，金額由固定項目決定。</summary>
    public decimal Percent { get; set; }

    public int SortOrder { get; set; }

    /// <summary>設定頁刪掉的分類只封存不刪除，舊週期的明細才還對得上名字。</summary>
    public bool IsArchived { get; set; }

    public List<DailySlot> Slots { get; set; } = [];
    public List<FixedItem> FixedItems { get; set; } = [];
}

public class DailySlot
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string Name { get; set; } = "";
    public int WorkdayAmount { get; set; }
    public int HolidayAmount { get; set; }
    public int SortOrder { get; set; }
}

public class FixedItem
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string Name { get; set; } = "";
    public int Amount { get; set; }

    /// <summary>每月扣款日（1–31，超過當月天數時落在月底），只拿來顯示。</summary>
    public int? DueDay { get; set; }

    public bool IsSubscription { get; set; }
    public BillingCycle Cycle { get; set; } = BillingCycle.Monthly;

    /// <summary>年繳：扣款月份；季繳：第一個扣款月份（之後每 3 個月一次）。月繳時忽略。</summary>
    public int? BillingMonth { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 從哪一天開始的週期才算這筆。回報花費時順手登記的訂閱，這期已經當成花費扣過了，
    /// 要從下一期才變成固定支出，不然重建本期時會重複扣。
    /// </summary>
    public DateOnly? ActiveFrom { get; set; }
}

/// <summary>使用者手動把某天改成假日/上班日（請假、補班、颱風假）。</summary>
public class DayTypeOverride
{
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public bool IsHoliday { get; set; }
}

/// <summary>政府行政機關辦公日曆表的快取，一年抓一次。</summary>
public class CalendarDay
{
    public DateOnly Date { get; set; }
    public bool IsHoliday { get; set; }
    public string Description { get; set; } = "";
}
