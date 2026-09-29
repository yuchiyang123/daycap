using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

public record AssetsView(
    decimal CashTotal,
    decimal InvestmentTotal,
    decimal CostTotal,
    List<CashAccountDto> CashAccounts,
    List<HoldingView> Holdings,
    List<GoalView> Goals,
    List<AssetSnapshotDto> History,
    DateTime? QuotesFetchedAt);

public record CashAccountDto(int Id, string Name, decimal Balance);

public record HoldingDto(int Id, string Symbol, string Name, decimal Shares, decimal AvgCost, decimal? ManualPrice);

public record HoldingView(
    int Id,
    string Symbol,
    string Name,
    decimal Shares,
    decimal AvgCost,
    decimal? ManualPrice,
    decimal? Price,
    string PriceSource,
    DateOnly? PriceDate,
    decimal MarketValue,
    decimal Cost,
    decimal Pnl);

public record GoalDto(int Id, string Name, decimal TargetAmount, DateOnly TargetDate, GoalScope Scope);

public record GoalView(
    int Id,
    string Name,
    decimal TargetAmount,
    DateOnly TargetDate,
    GoalScope Scope,
    decimal Current,
    decimal Progress,
    int MonthsLeft,
    decimal MonthlyNeeded);

public record AssetSnapshotDto(DateOnly Date, decimal Cash, decimal Investments);

public record AssetAdjustmentView(
    int Id,
    int CashAccountId,
    string AccountName,
    DateOnly Date,
    decimal Amount,
    string Note,
    string Source,
    int? PeriodId,
    DateTime CreatedAt);

/// <summary>Amount 帶正負號：正 = 存入 / 收入，負 = 支出。</summary>
public record CreateAssetAdjustmentRequest(int CashAccountId, DateOnly Date, decimal Amount, string? Note);

public record SaveAssetsRequest(List<CashAccountDto> CashAccounts, List<HoldingDto> Holdings, List<GoalDto> Goals);
