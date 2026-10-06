namespace DayCap.Api.Models.Entities;

/// <summary>§5.1：銀行、現金、電子支付儲值是資產；信用卡是負債（餘額＝欠多少）。</summary>
public enum AccountType { Bank, Cash, EWallet, CreditCard }

/// <summary>
/// 帳戶。餘額不直接存：由「最近一次對帳的實際餘額」加上之後已知的移動（轉帳、繳卡費、資產加減、
/// 有記付款帳戶的回報）推算（§2.2）。Balance 欄位是舊版直接存的數字，只在第一次換新模型時當成期初對帳。
/// </summary>
public class CashAccount
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public AccountType Type { get; set; } = AccountType.Bank;
    public bool IsArchived { get; set; }
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

    /// <summary>護欄選「從存款吸收」時由那筆回報產生（刪回報時一起作廢）。</summary>
    public int? SourceEntryId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}

/// <summary>
/// 對帳（事實，§12.1）：某一天結束時各帳戶的實際餘額。不存差額，差額由重播算。
/// IsFull = 當時所有帳戶都有填，才能拿來和預期餘額比；只填部分帳戶（例如新增帳戶的期初餘額）只更新那些帳戶。
/// </summary>
public class Reconciliation : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public bool IsFull { get; set; }

    /// <summary>負差額（花多了）時，先從待分配池扣；不夠或不勾才攤到之後的日子。</summary>
    public bool UsePool { get; set; } = true;

    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }

    public List<ReconciliationLine> Lines { get; set; } = [];
}

public class ReconciliationLine
{
    public int Id { get; set; }
    public int ReconciliationId { get; set; }
    public int AccountId { get; set; }

    /// <summary>資產帳戶＝餘額；信用卡＝欠款（正數）。</summary>
    public decimal Balance { get; set; }
}

public enum TransferKind { Transfer, CardPayment }

/// <summary>帳戶之間的移動（事實）：互轉、繳卡費。都不算花費，淨資產不變（§5）。</summary>
public class AccountTransfer : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public TransferKind Kind { get; set; }
    public int FromAccountId { get; set; }
    public int ToAccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }

    /// <summary>系統自動產生的（固定支出設定成轉帳）：「fixed:{項目Id}:{扣款日}」，同一筆只會產生一次。手動記的為 null。</summary>
    public string? AutoKey { get; set; }
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
