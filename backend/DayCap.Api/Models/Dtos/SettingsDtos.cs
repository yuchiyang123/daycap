using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;

namespace DayCap.Api.Models.Dtos;

public record SettingsDto(
    string LogicalDayStart,
    decimal MonthlyIncome,
    PaydayRule Payday,
    DateOnly? StartDate,
    List<CategoryDto> Categories);

public record CategoryDto(
    int Id,
    string Name,
    CategoryGroup Group,
    BudgetMode Mode,
    decimal Percent,
    List<SlotDto> Slots,
    List<FixedItemDto> FixedItems);

public record SlotDto(int Id, string Name, string Start, decimal WorkdayAmount, decimal HolidayAmount);

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

public record MeDto(string UserId, string? UserName);
