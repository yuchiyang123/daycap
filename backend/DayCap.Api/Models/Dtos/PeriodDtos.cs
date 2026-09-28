using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

public record PeriodSummaryDto(int Id, DateOnly StartDate, DateOnly EndDate, int Income);

public record PeriodView(
    int Id,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly Today,
    int Income,
    PoolView Pool,
    List<CategoryView> Categories,
    List<DayView> Days,
    List<EntryView> Entries,
    List<FixedChargeView> FixedCharges,
    List<PoolTransferView> Transfers);

public record PoolView(int Opening, int Balance, List<PoolLine> Lines);

/// <summary>
/// Kind：Opening（期初）、Surplus（少花存入）、Cover（超支從待定區扣）、
/// Unabsorbed（後續天數不夠攤，只好從待定區扣到負）、EnvelopeOver（月額度類超支）、Transfer（手動）。
/// </summary>
public record PoolLine(DateOnly Date, int Amount, string Kind, string Label, int? EntryId, int? TransferId);

public record CategoryView(
    int CategoryId,
    string Name,
    CategoryGroup Group,
    BudgetMode Mode,
    int Budget,
    int Scheduled,
    int Spent,
    int PlannedRemaining,
    int Projected);

public record DayView(
    DateOnly Date,
    bool IsHoliday,
    string? HolidayName,
    string Status,
    int BasePlanned,
    int Planned,
    int Net,
    List<SlotView> Slots,
    List<int> ExtraEntryIds);

public record SlotView(
    int CategoryId,
    int SlotId,
    string Name,
    int BasePlanned,
    int Planned,
    int? Actual,
    int? EntryId);

public record EntryView(
    int Id,
    DateOnly Date,
    int CategoryId,
    string CategoryName,
    int? SlotId,
    string? SlotName,
    EntryInputMode InputMode,
    int InputAmount,
    int Actual,
    int PlannedAtEntry,
    int Diff,
    bool UsePool,
    int FromPool,
    int Spread,
    int SpreadSlots,
    int Unabsorbed,
    int EnvelopeOver,
    string? Note,
    bool IsSubscription,
    DateTime CreatedAt);

public record FixedChargeView(int Id, int CategoryId, string Name, int Amount, DateOnly? DueDate, bool IsSubscription);

public record PoolTransferView(int Id, DateOnly Date, int Amount, string Note);

public record CreateEntryRequest(
    DateOnly Date,
    int CategoryId,
    int? SlotId,
    EntryInputMode InputMode,
    int Amount,
    bool UsePool,
    string? Note,
    SubscriptionRequest? Subscription);

/// <summary>回報時順便把它登記成訂閱：下個週期起變成固定支出。</summary>
public record SubscriptionRequest(string Name, int TargetCategoryId, BillingCycle Cycle, int? DueDay);

public record EntryPreview(EntryView Entry, int PoolBefore, int PoolAfter, int CategoryRemainingBefore, int CategoryRemainingAfter);

public record CreatePoolTransferRequest(DateOnly Date, int Amount, string Note);

public record RebuildRequest(DateOnly? FromDate);
