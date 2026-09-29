using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

/// <summary>
/// 回報時分帳（§14）。IPaid：我全付了 Total，我的份是回報的金額，其餘記應收；
/// TheyPaid：對方先付，我的份（回報的金額）照扣預算並記應付，錢沒有從我的帳戶出去。
/// </summary>
public record SplitRequest(SplitKind Kind, string Counterparty, decimal? Total);

public enum SplitKind { IPaid, TheyPaid }

public record DebtView(
    int Id,
    DebtKind Kind,
    string Counterparty,
    decimal Amount,
    decimal Settled,
    decimal Outstanding,
    DateOnly Date,
    string? Note,
    int? AccountId,
    int? SourceEntryId);

public record CounterpartyBalance(string Counterparty, decimal Receivable, decimal Payable, decimal Net);

public record DebtsView(List<DebtView> Debts, decimal ReceivableTotal, decimal PayableTotal, List<CounterpartyBalance> People);

/// <summary>手動記一筆借貸（例如借錢給朋友）：不是花費，只是錢出去 / 進來。</summary>
public record CreateDebtRequest(DebtKind Kind, string Counterparty, decimal Amount, DateOnly? Date, int? AccountId, string? Note);

public record SettleDebtRequest(decimal Amount, int? AccountId, DateOnly? Date);
