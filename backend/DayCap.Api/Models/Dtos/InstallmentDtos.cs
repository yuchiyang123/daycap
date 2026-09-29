using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

public record CreateInstallmentRequest(
    string Name,
    int CategoryId,
    InstallmentMode Mode,
    int Periods,
    DateOnly FirstDueDate,
    decimal? MonthlyAmount = null,
    decimal? Principal = null,
    decimal? AnnualRatePercent = null,
    decimal? Fee = null,
    string? Note = null);

public record PrepayRequest(decimal Amount, PrepayMode Mode, int? AccountId, DateOnly? Date);

public record InstallmentRowView(int Index, DateOnly DueDate, decimal Payment, decimal Principal, decimal Interest, decimal Fee, decimal BalanceAfter);

public record PrepaymentView(int Id, DateOnly Date, decimal Amount, PrepayMode Mode, int? AccountId);

/// <summary>
/// 分期（§15）。Remaining＝之後還要繳的總額（含利息手續費）；PrincipalRemaining＝剩下的本金（負債）；
/// CostTotal＝整筆分期的利息＋手續費。
/// </summary>
public record InstallmentView(
    int Id,
    string Name,
    int CategoryId,
    InstallmentMode Mode,
    decimal? MonthlyAmount,
    int Periods,
    decimal? Principal,
    decimal? AnnualRatePercent,
    decimal? Fee,
    DateOnly FirstDueDate,
    string? Note,
    int TotalPayments,
    int PaidPayments,
    decimal Remaining,
    decimal PrincipalRemaining,
    DateOnly? NextDueDate,
    decimal? NextPayment,
    decimal CostTotal,
    List<InstallmentRowView> Schedule,
    List<PrepaymentView> Prepayments);
