using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

public record AssetsView(
    int CashTotal,
    int InvestmentTotal,
    int CostTotal,
    List<CashAccountDto> CashAccounts,
    List<HoldingView> Holdings,
    List<GoalView> Goals,
    List<AssetSnapshotDto> History,
    DateTime? QuotesFetchedAt);

public record CashAccountDto(int Id, string Name, int Balance);

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
    int MarketValue,
    int Cost,
    int Pnl);

public record GoalDto(int Id, string Name, int TargetAmount, DateOnly TargetDate, GoalScope Scope);

public record GoalView(
    int Id,
    string Name,
    int TargetAmount,
    DateOnly TargetDate,
    GoalScope Scope,
    int Current,
    decimal Progress,
    int MonthsLeft,
    int MonthlyNeeded);

public record AssetSnapshotDto(DateOnly Date, int Cash, int Investments);

public record SaveAssetsRequest(List<CashAccountDto> CashAccounts, List<HoldingDto> Holdings, List<GoalDto> Goals);
