using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;

namespace DayCap.Api.Models.Dtos;

public record SettingsDto(
    string LogicalDayStart,
    decimal MonthlyIncome,
    PaydayRule Payday,
    DateOnly? StartDate,
    List<CategoryDto> Categories,
    PercentBase PercentBase = PercentBase.Income,
    IncomeKind IncomeKind = IncomeKind.Fixed);

public record CategoryDto(
    int Id,
    string Name,
    CategoryGroup Group,
    BudgetMode Mode,
    decimal Percent,
    List<SlotDto> Slots,
    List<FixedItemDto> FixedItems,
    bool UsePercent = true,
    decimal? Amount = null,
    decimal? Floor = null,
    MealAuto? Auto = null);

public record SlotDto(
    int Id,
    string Name,
    string Start,
    decimal WorkdayAmount,
    decimal HolidayAmount,
    decimal? Weight = null,
    decimal? WorkdayLock = null,
    decimal? HolidayLock = null,
    decimal? WorkdayFloor = null,
    decimal? HolidayFloor = null,
    decimal? HolidayWeight = null);

/// <summary>設定頁即時合計（§7、§8.2）：用草稿算出下一期（新設定生效那期）的額度與排程。</summary>
public record SettingsEstimate(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int Weekdays,
    int Holidays,
    decimal Income,
    decimal FixedTotal,
    decimal PercentBaseAmount,
    decimal PercentTotal,
    decimal Unallocated,
    List<CategoryEstimate> Categories,
    List<string> Errors);

public record CategoryEstimate(int Index, decimal Budget, decimal WeekdayTotal, decimal HolidayTotal, decimal Scheduled, decimal Over);

/// <summary>§8.2 分配器預覽：直接把格子交給 Allocator.Allocate（使用者實作）。</summary>
public record AllocatePreviewRequest(decimal Budget, List<Services.Allocation.AllocationCell> Cells, decimal RoundingUnit, string? ReleasedShareReceiverKey);

public record FixedItemDto(
    int Id,
    string Name,
    decimal Amount,
    int? DueDay,
    bool IsSubscription,
    BillingCycle Cycle,
    int? BillingMonth,
    bool IsActive,
    DateOnly? ActiveFrom);

public record SettingsVersionSummary(int Id, DateOnly EffectiveFrom, DateTime CreatedAt, bool IsCorrection, string? Note);

/// <summary>
/// 設定頁顯示的是「最新的一份」（可能明天才生效）。Today 是邏輯日，
/// 一般儲存的新版本從 Today + 1 起生效。
/// </summary>
public record SettingsView(
    SettingsDto Settings,
    DateOnly EffectiveFrom,
    DateOnly Today,
    List<SettingsVersionSummary> Versions);

/// <summary>CorrectionFrom 有值 = 更正流程：生效日可以早於明天，但 CorrectionNote 必填。</summary>
public record SaveSettingsRequest(SettingsDto Settings, DateOnly? CorrectionFrom, string? CorrectionNote);

public record DayOverrideRequest(bool? IsHoliday);

public record CalendarDayDto(DateOnly Date, bool IsHoliday, string? Name);

public record MeDto(string UserId, string? UserName, bool Onboarded = true, List<string>? SeenTips = null);
