namespace DayCap.Api.Models.Entities;

/// <summary>分期輸入方式（§15）：簡易＝每月 X 元共 N 期；細部＝本金、年利率、期數、手續費。</summary>
public enum InstallmentMode { Simple, Detailed }

/// <summary>提前還款後：每期金額變少（期數不變）或期數變短（金額不變）。</summary>
public enum PrepayMode { ReduceAmount, ReduceTerm }

/// <summary>
/// 一筆分期（事實）。每期繳款＝這期的固定支出（算在 CategoryId 那個固定類別）；剩下沒繳的是負債。
/// 繳款表不存，由 <see cref="Services.InstallmentSchedule"/> 從這些欄位和提前還款推出來。
/// </summary>
public class Installment : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public int CategoryId { get; set; }
    public InstallmentMode Mode { get; set; }

    /// <summary>簡易：每期金額。</summary>
    public decimal? MonthlyAmount { get; set; }
    public int Periods { get; set; }

    /// <summary>細部：本金、年利率（%）、手續費總額（平均攤在每一期）。</summary>
    public decimal? Principal { get; set; }
    public decimal? AnnualRatePercent { get; set; }
    public decimal? Fee { get; set; }

    /// <summary>第一期繳款日；之後每月同一天（月底不足就落在月底）。</summary>
    public DateOnly FirstDueDate { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}

/// <summary>提前還款（事實）：從資產付，負債減少，不算花費、不扣預算（§15）。</summary>
public class InstallmentPrepayment : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int InstallmentId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public int? AccountId { get; set; }
    public PrepayMode Mode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}
