using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

/// <summary>
/// Balance 是推算的：最近一次對帳 + 之後已知的移動。信用卡的 Balance 是欠款（正數）。
/// AsOf = 那次對帳的日期；Estimated = 對帳之後有沒有記付款帳戶的回報會讓它不準（只是提醒）。
/// </summary>
public record AccountView(
    int Id,
    string Name,
    AccountType Type,
    decimal Balance,
    DateOnly? ReconciledOn,
    decimal CardSpendThisPeriod,
    bool IsArchived);

public record AccountsView(List<AccountView> Accounts, decimal NetLiquid, DateOnly? LastFullReconciliation);

/// <summary>新增帳戶時 OpeningBalance 會記成一筆只含這個帳戶的對帳；既有帳戶只能改名稱和類型。</summary>
public record AccountEdit(int Id, string Name, AccountType Type, decimal? OpeningBalance);

public record CreateTransferRequest(DateOnly Date, TransferKind Kind, int FromAccountId, int ToAccountId, decimal Amount, string? Note);

public record TransferView(int Id, DateOnly Date, TransferKind Kind, int FromAccountId, string FromName, int ToAccountId, string ToName, decimal Amount, string? Note, DateTime CreatedAt);

public record ReconcileLineInput(int AccountId, decimal Balance);

public record CreateReconciliationRequest(DateOnly Date, List<ReconcileLineInput> Lines, bool UsePool, string? Note);

/// <summary>
/// 試算／結果：Expected 是依上次對帳 + 期間已知的收支推出來的；Diff = Actual − Expected。
/// HasBaseline = false 表示這是第一次完整對帳，只當基準、不產生差額。
/// LargeDiff = 差額超過本期變動預算 30%，先問是不是漏記大額或固定支出（§12.1）。
/// </summary>
public record ReconciliationResult(
    int? Id,
    DateOnly Date,
    bool HasBaseline,
    DateOnly? BaselineDate,
    decimal Expected,
    decimal Actual,
    decimal Diff,
    decimal FromPool,
    decimal Spread,
    decimal Unabsorbed,
    bool LargeDiff,
    decimal PoolBefore,
    decimal PoolAfter);

public record ReconciliationSummary(int Id, DateOnly Date, bool IsFull, decimal Net, string? Note, DateTime CreatedAt, List<ReconcileLineInput> Lines);
