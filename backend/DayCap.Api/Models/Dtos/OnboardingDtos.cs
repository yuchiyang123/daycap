namespace DayCap.Api.Models.Dtos;

/// <summary>新手引導（§20）。Needed = 還沒走過引導（舊使用者視為已完成）。</summary>
public record OnboardingState(bool Needed, string? AppliedTemplate, List<TemplateDto> Templates);

public record TemplateDto(string Code, int Version, string Name, List<TemplateBlockDto> Blocks, List<string> FixedItemNames);

public record TemplateBlockDto(string Key, string Name, decimal Percent, bool DefaultChecked, string? Hint);

/// <summary>每個區塊：勾不勾、填了實際金額就鎖定（§20.5 規則 1–3）。</summary>
public record BlockChoice(string Key, bool Checked, decimal? Amount);

public record ApplyTemplateRequest(string TemplateCode, decimal Income, int PaydayDay, List<BlockChoice> Blocks, decimal? SavingsMinPercent = null);

/// <summary>
/// 套用預覽。Baseline＝套用勾選後、套用鎖定前；Amount＝最後金額；Delta＝兩者實際重算的差（§20.5 規則 7）。
/// AllocatorReady = false：分配器（§8.1）還沒實作，用保守做法（不重分配）。
/// </summary>
public record TemplatePreview(
    string Code,
    int Version,
    string Name,
    decimal Income,
    bool AllocatorReady,
    List<TemplateBlockPreview> Blocks,
    decimal Unallocated,
    MealPreviewDto? Meals,
    List<string> Errors,
    List<string> Warnings);

public record TemplateBlockPreview(
    string Key,
    string Name,
    bool Checked,
    bool Locked,
    decimal TemplatePercent,
    decimal Baseline,
    decimal Amount,
    decimal Delta,
    decimal ActualPercent,
    string? Hint);

/// <summary>餐費預覽（§20.5 規則 10）：這期實際的平日 / 假日天數；Slots = null 表示分配器還沒能算每餐金額。</summary>
public record MealPreviewDto(DateOnly PeriodStart, DateOnly PeriodEnd, int Weekdays, int Holidays, decimal Budget, decimal PerDayAverage,
    List<MealSlotPreview>? Slots, string? Problem);

public record MealSlotPreview(string Name, decimal Workday, decimal Holiday);
