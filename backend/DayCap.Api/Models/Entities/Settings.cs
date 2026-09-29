namespace DayCap.Api.Models.Entities;

public enum CategoryGroup { Food, Clothing, Housing, Transport, Education, Leisure, Savings, Other }

/// <summary>
/// Fixed：鎖死，金額 = 固定項目加總，不能回報超支；
/// Daily：拆成每日時段（早餐/午餐…），分上班日/假日金額，是這個 app 的核心；
/// Envelope：整月一個額度，花了就扣，沒有每日上限（衣、樂這類）。
/// </summary>
public enum BudgetMode { Fixed, Daily, Envelope }

public enum BillingCycle { Monthly, Yearly, Quarterly }

/// <summary>
/// 一份預算設定版本（§4.1）。EffectiveFrom 是邏輯日；重播時每一天套用那天有效的版本
/// （EffectiveFrom ≤ 那天的版本中，生效日最晚的；同一天生效的取最後建立的）。
/// 一般儲存的生效日是明天；IsCorrection = 走「更正」流程改過去，必須附原因。
/// </summary>
public class SettingsVersion
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>建立當下的邏輯日。今天建立、明天生效的版本，今天還沒回報的時段取較低值（§4.1 決定）。</summary>
    public DateOnly CreatedOn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsCorrection { get; set; }
    public string? Note { get; set; }

    /// <summary><see cref="Settings.SettingsDocument"/> 的 JSON。</summary>
    public string Document { get; set; } = "";
}

public class UserProfile
{
    public string UserId { get; set; } = "";

    /// <summary>下一個可用的分類 / 時段 / 固定項目 Id（跨版本不重複）。</summary>
    public int NextSettingsId { get; set; } = 1;

    // ---- 舊欄位（已不使用）：改存在設定版本裡，保留給舊資料匯入 ----
    public int MonthlyIncome { get; set; }

    public int CycleStartDay { get; set; } = 1;

    /// <summary>
    /// 從哪天開始算（例如拿到第一份薪水那天）。這天之前完全不排額度、不開週期；
    /// 第一期從這天開始，到下一個週期起始日的前一天（可能不滿一個月）。null = 馬上開始。
    /// </summary>
    public DateOnly? StartDate { get; set; }

    // 期末自動從存款扣 / 結餘存入：依規格 §2.1 關閉（2026-09-29），欄位保留不用
    public int? SettlementAccountId { get; set; }

    public bool SurplusToAccount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>舊的覆寫式設定（已不寫入）。第一次讀設定時匯入成設定版本，下一版移除。</summary>
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
