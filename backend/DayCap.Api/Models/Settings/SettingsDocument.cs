using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Settings;

/// <summary>
/// 一整份預算設定。存成 <see cref="SettingsVersion"/> 的 JSON，帶生效日（REQUIREMENTS §4.1）。
/// 分類、時段、固定項目的 Id 在各版本間不變，回報靠這些 Id 對應。
/// </summary>
public record SettingsDocument(
    // 邏輯日起點，"HH:mm"（§3.1）
    string LogicalDayStart,
    decimal MonthlyIncome,
    PaydayRule Payday,
    List<CategoryDoc> Categories,
    // % 算在哪個基準上（§7）；null = 舊設定，當成「佔收入」
    PercentBase? PercentBase = null,
    // 固定收入 / 非固定收入（非固定用上期實際收入當 % 基準）；null = 固定
    IncomeKind? IncomeKind = null,
    // 超支護欄（§10.2）：每個時段最多被攤到原本金額的幾 %；null = 50
    decimal? GuardrailFloorPercent = null)
{
    [JsonIgnore]
    public decimal FloorPercent => GuardrailFloorPercent ?? 50m;

    [JsonIgnore]
    public PercentBase Base => PercentBase ?? global::DayCap.Api.Models.Settings.PercentBase.Income;

    [JsonIgnore]
    public IncomeKind Kind => IncomeKind ?? global::DayCap.Api.Models.Settings.IncomeKind.Fixed;

    [JsonIgnore]
    public TimeSpan DayStart => ParseTime(LogicalDayStart);

    public static TimeSpan ParseTime(string hhmm) =>
        TimeSpan.ParseExact(hhmm, @"hh\:mm", CultureInfo.InvariantCulture);
}

public enum HolidayShift { None, Before, After }

/// <summary>§7：Income = 佔月收入；AfterFixed = 佔「月收入扣掉固定支出後的餘額」（規格預設）。</summary>
public enum PercentBase { Income, AfterFixed }

public enum IncomeKind { Fixed, Variable }

/// <summary>
/// §8.2 自動分配模式的參數（每日類分類）。每個時段在平日、假日各是一格，
/// 權重＝時段權重 ×（假日時再乘 HolidayMultiplier），由分配器 Allocator.Allocate 算出單價。
/// </summary>
public record MealAuto(bool Enabled, decimal HolidayMultiplier, decimal RoundingUnit);

/// <summary>發薪日：每月 Day 號；遇到假日（行事曆上不是工作日）時依 Shift 調整（§3.4）。</summary>
public record PaydayRule(int Day, HolidayShift Shift);

public record CategoryDoc(
    int Id,
    string Name,
    CategoryGroup Group,
    BudgetMode Mode,
    decimal Percent,
    List<SlotDoc> Slots,
    List<FixedItemDoc> FixedItems,
    // 「用 % 計算」沒勾 = 固定金額 Amount（§7）；null = 用 %
    bool? UsePercent = null,
    decimal? Amount = null,
    // 區塊底線（§7、§8.1，範本套用時給分配器用）
    decimal? Floor = null,
    // 每日類的自動分配（§8.2）；null 或 Enabled=false = 自己設定
    MealAuto? Auto = null,
    // 類別細項（§13）：回報可以標細項，細項可選擇性設每期上限（只提醒，不改預算規則）
    List<SubItemDoc>? SubItems = null)
{
    [JsonIgnore]
    public bool IsPercent => UsePercent ?? true;
}

/// <summary>
/// 時段。只存開始時間，結束＝同分類下一個時段的開始；最後一個時段到隔天邏輯日起點（§3.2 邊界銜接）。
/// 同一分類的第一個時段必須從邏輯日起點開始，這樣 24 小時一定被切滿、不會有空隙或重疊。
/// </summary>
public record SlotDoc(
    int Id,
    string Name,
    string Start,
    decimal WorkdayAmount,
    decimal HolidayAmount,
    // §8.2 自動分配：時段權重、各格鎖定單價、各格底線（都可空）
    decimal? Weight = null,
    decimal? WorkdayLock = null,
    decimal? HolidayLock = null,
    decimal? WorkdayFloor = null,
    decimal? HolidayFloor = null,
    // 假日這格自己的權重；null = Weight × 假日倍率。從「自己設定」切到自動時用來讓數字不跳（§8.2）
    decimal? HolidayWeight = null);

/// <summary>類別細項（§13），例如娛樂 → 遊戲（每期最多 500）。</summary>
public record SubItemDoc(string Name, decimal? Cap);

public record FixedItemDoc(
    int Id,
    string Name,
    decimal Amount,
    int? DueDay,
    bool IsSubscription,
    BillingCycle Cycle,
    int? BillingMonth,
    bool IsActive,
    DateOnly? ActiveFrom,
    // 自動執行（扣款日當天）：從 FromAccountId 轉到 ToAccountId（例如存進本金戶），
    // 或用當天收盤價定期定額買進 HoldingId 這檔股票。兩者擇一，都空＝只是預算上的固定支出。
    int? FromAccountId = null,
    int? ToAccountId = null,
    int? HoldingId = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAuto => FromAccountId is not null && (ToAccountId is not null || HoldingId is not null);
}

public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(SettingsDocument doc) => JsonSerializer.Serialize(doc, Options);

    public static SettingsDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<SettingsDocument>(json, Options) ?? throw new InvalidOperationException("設定版本內容無法解析。");
}
