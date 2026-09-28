using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

public record SettingsDto(
    int MonthlyIncome,
    int CycleStartDay,
    DateOnly? StartDate,
    int? SettlementAccountId,
    bool SurplusToAccount,
    List<CategoryDto> Categories);

public record CategoryDto(
    int Id,
    string Name,
    CategoryGroup Group,
    BudgetMode Mode,
    decimal Percent,
    List<SlotDto> Slots,
    List<FixedItemDto> FixedItems);

public record SlotDto(int Id, string Name, int WorkdayAmount, int HolidayAmount);

public record FixedItemDto(
    int Id,
    string Name,
    int Amount,
    int? DueDay,
    bool IsSubscription,
    BillingCycle Cycle,
    int? BillingMonth,
    bool IsActive,
    DateOnly? ActiveFrom);

public record DayOverrideRequest(bool? IsHoliday);

public record CalendarDayDto(DateOnly Date, bool IsHoliday, string? Name);

public record MeDto(string UserId, string? UserName);
