namespace DayCap.Api.Models.Entities;

public class CashAccount
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Balance { get; set; }
    public int SortOrder { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Holding
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";

    /// <summary>台股代號（例如 0050、2330），用來抓收盤價。</summary>
    public string Symbol { get; set; } = "";

    public string Name { get; set; } = "";
    public decimal Shares { get; set; }

    /// <summary>平均成本（每股）。</summary>
    public decimal AvgCost { get; set; }

    /// <summary>手動價格。有填就不用抓到的收盤價（例如海外標的、抓不到的代號）。</summary>
    public decimal? ManualPrice { get; set; }

    public int SortOrder { get; set; }
}

public enum GoalScope { All, Cash, Investments }

public class Goal
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal TargetAmount { get; set; }
    public DateOnly TargetDate { get; set; }
    public GoalScope Scope { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>每天第一次看資產頁時記一筆，畫資產走勢用。同一天重複更新就覆蓋。</summary>
public class AssetSnapshot
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Cash { get; set; }
    public decimal Investments { get; set; }
}

/// <summary>收盤價快取（證交所 / 櫃買中心 OpenAPI）。</summary>
public class PriceQuote
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public DateOnly TradeDate { get; set; }
    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// 直接加減某個存款帳戶的一筆紀錄（記帳頁的「資產加減」，或週期結算自動扣除 / 存入）。
/// 新增時同步改帳戶餘額，刪除時反向還原。
/// </summary>
public class AssetAdjustment : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int CashAccountId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Note { get; set; } = "";

    /// <summary>manual = 記帳頁手動；settlement = 週期結算。</summary>
    public string Source { get; set; } = "manual";

    public int? PeriodId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}

/// <summary>
/// 站內通知（右上角鈴鐺）。Key 對同一件事唯一，避免重複產生。
/// PopupOn = 只有這天第一次打開 app 時跳出來；ReadAt = 在鈴鐺裡看過（紅點消失）。
/// </summary>
public class Notification
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public int? PeriodId { get; set; }
    public DateOnly? PopupOn { get; set; }
    public DateTime? PopupShownAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? Payload { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
