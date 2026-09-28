namespace DayCap.Api.Models.Entities;

public class CashAccount
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Balance { get; set; }
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
    public int TargetAmount { get; set; }
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
    public int Cash { get; set; }
    public int Investments { get; set; }
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
