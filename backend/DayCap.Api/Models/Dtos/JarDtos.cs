using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

/// <summary>罐子（§11.2）。Need = 還差多少到目標；DaysLeft = 離到期日幾天（沒到期日為 null）。</summary>
public record JarView(
    int Id,
    JarKind Kind,
    string Name,
    decimal TargetAmount,
    decimal Balance,
    decimal Need,
    DateOnly? DueDate,
    int? DaysLeft,
    decimal? MonthlyAmount,
    decimal? AutoSurplusPercent,
    int? FixedItemId,
    bool Closed);

public record SaveJarRequest(
    JarKind Kind,
    string Name,
    decimal TargetAmount,
    DateOnly? DueDate,
    decimal? MonthlyAmount = null,
    decimal? AutoSurplusPercent = null,
    int? FixedItemId = null);

public record JarMoveRequest(decimal Amount);
