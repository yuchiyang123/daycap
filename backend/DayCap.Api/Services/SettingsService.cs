using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface ISettingsService
{
    Task<SettingsView> GetAsync(string userId, CancellationToken ct = default);
    Task<SettingsView> SaveAsync(string userId, SaveSettingsRequest req, CancellationToken ct = default);
    Task<UserProfile> EnsureProfileAsync(string userId, CancellationToken ct = default);
    Task<SettingsTimeline> GetTimelineAsync(string userId, CancellationToken ct = default);

    /// <summary>現在的邏輯日（依目前有效版本的邏輯日起點）。</summary>
    Task<DateOnly> LogicalTodayAsync(string userId, CancellationToken ct = default);

    /// <summary>回報時登記的訂閱：在最新設定裡加一筆固定項目，存成明天生效的新版本（呼叫端負責 SaveChanges）。</summary>
    Task AddFixedItemAsync(string userId, int categoryId, FixedItemDoc item, CancellationToken ct = default);

    /// <summary>設定頁即時合計：用草稿算出新設定生效那一期的額度與每日類排程（§7、§8.2）。</summary>
    Task<SettingsEstimate> EstimateAsync(string userId, SettingsDto draft, CancellationToken ct = default);

    /// <summary>月結的「下期調整」（§12.2 第 4 步）：改時段金額，存成從 effectiveFrom 起生效的新版本（呼叫端負責 SaveChanges）。</summary>
    Task ApplySlotChangesAsync(string userId, List<Models.Dtos.SlotChange> changes, DateOnly effectiveFrom, string note, CancellationToken ct = default);
}

/// <summary>
/// 設定版本化（§4.1）：每次儲存都新增一筆版本，不覆寫舊的。
/// 一般儲存的生效日＝明天（邏輯日）；更正流程可以指定過去的日期，但要填原因。
/// 使用者第一次讀設定時：有舊的覆寫式設定就匯入，沒有就給一份預設值；這份初始版本從最早開始生效。
/// </summary>
public class SettingsService(DayCapDbContext db, IAppClock clock, ICalendarService calendar) : ISettingsService
{
    public const string DefaultDayStart = "04:00";

    public async Task<SettingsView> GetAsync(string userId, CancellationToken ct = default)
    {
        var profile = await EnsureProfileAsync(userId, ct);
        var timeline = await GetTimelineAsync(userId, ct);
        var (latest, doc) = timeline.Latest;
        var today = clock.LogicalToday(timeline.For(clock.Today).Doc.DayStart);
        return new SettingsView(
            ToDto(doc, profile.StartDate),
            latest.EffectiveFrom,
            today,
            timeline.Versions
                .OrderByDescending(v => v.Version.CreatedAt)
                .Select(v => new SettingsVersionSummary(v.Version.Id, v.Version.EffectiveFrom, v.Version.CreatedAt, v.Version.IsCorrection, v.Version.Note))
                .ToList());
    }

    public async Task<SettingsView> SaveAsync(string userId, SaveSettingsRequest req, CancellationToken ct = default)
    {
        var profile = await EnsureProfileAsync(userId, ct);
        var timeline = await GetTimelineAsync(userId, ct);
        var today = clock.LogicalToday(timeline.For(clock.Today).Doc.DayStart);

        var doc = Validate(req.Settings);
        doc = AssignIds(doc, profile);

        DateOnly effectiveFrom;
        string? note = null;
        if (req.CorrectionFrom is { } from)
        {
            if (from > today) throw new ValidationException("更正的生效日不能晚於今天；要從明天起生效就用一般儲存。");
            note = req.CorrectionNote?.Trim();
            if (string.IsNullOrEmpty(note) || note.Length < 2) throw new ValidationException("更正過去的設定必須寫原因。");
            effectiveFrom = from;
        }
        else
        {
            effectiveFrom = today.AddDays(1);
        }

        // §8.2：自己設定模式下，每日類的排程超過額度就擋住儲存（不只變紅）
        // 更正可以從很久以前生效，檢查「從現在起受影響的那一期」
        var estimate = await EstimateDocAsync(userId, doc, effectiveFrom < today ? today : effectiveFrom, ct);
        var over = estimate.Categories.Where(c => c.Over > 0).ToList();
        if (over.Count > 0)
        {
            var parts = over.Select(c =>
            {
                var cat = doc.Categories[c.Index];
                return $"「{cat.Name}」平日 {c.WeekdayTotal:N0} + 假日 {c.HolidayTotal:N0} = {c.Scheduled:N0}，超過額度 {c.Budget:N0}（多 {c.Over:N0}）";
            });
            throw new ValidationException($"{string.Join("；", parts)}。請調低金額或提高額度。");
        }

        profile.StartDate = req.Settings.StartDate;

        // 跟最新一份一模一樣就不另存版本（例如只改了開始日期，它不屬於版本）
        var json = SettingsJson.Serialize(doc);
        if (req.CorrectionFrom is null && json == timeline.Latest.Version.Document)
        {
            await db.SaveChangesAsync(ct);
            return await GetAsync(userId, ct);
        }

        db.SettingsVersions.Add(new SettingsVersion
        {
            UserId = userId,
            EffectiveFrom = effectiveFrom,
            CreatedOn = today,
            CreatedAt = clock.UtcNow,
            IsCorrection = req.CorrectionFrom is not null,
            Note = note is null ? null : note[..Math.Min(note.Length, 200)],
            Document = json,
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(userId, ct);
    }

    public async Task AddFixedItemAsync(string userId, int categoryId, FixedItemDoc item, CancellationToken ct = default)
    {
        var profile = await EnsureProfileAsync(userId, ct);
        var timeline = await GetTimelineAsync(userId, ct);
        var today = clock.LogicalToday(timeline.For(clock.Today).Doc.DayStart);
        var latest = timeline.Latest.Doc;
        var cat = latest.Categories.FirstOrDefault(c => c.Id == categoryId)
                  ?? throw new ValidationException("找不到要放訂閱的固定支出分類。");
        if (cat.Mode != BudgetMode.Fixed) throw new ValidationException("訂閱只能放進固定支出（鎖定）的分類。");

        var withItem = latest with
        {
            Categories = latest.Categories.Select(c => c.Id != categoryId ? c : c with
            {
                FixedItems = [.. c.FixedItems, item with { Id = profile.NextSettingsId++ }],
            }).ToList(),
        };
        db.SettingsVersions.Add(new SettingsVersion
        {
            UserId = userId,
            EffectiveFrom = today.AddDays(1),
            CreatedOn = today,
            CreatedAt = clock.UtcNow,
            Note = $"登記訂閱：{item.Name}",
            Document = SettingsJson.Serialize(withItem),
        });
    }

    public async Task ApplySlotChangesAsync(string userId, List<Models.Dtos.SlotChange> changes, DateOnly effectiveFrom, string note, CancellationToken ct = default)
    {
        if (changes.Count == 0) return;
        var profile = await EnsureProfileAsync(userId, ct);
        var timeline = await GetTimelineAsync(userId, ct);
        var today = clock.LogicalToday(timeline.For(clock.Today).Doc.DayStart);
        if (effectiveFrom <= today) throw new ValidationException("下期調整的生效日要在今天之後。");
        var latest = timeline.Latest.Doc;
        var changed = latest with
        {
            Categories = latest.Categories.Select(c => c with
            {
                Slots = c.Slots.Select(sl =>
                {
                    var ch = changes.FirstOrDefault(x => x.CategoryId == c.Id && x.SlotId == sl.Id);
                    return ch is null ? sl : sl with { WorkdayAmount = Math.Round(ch.WorkdayAmount, 0), HolidayAmount = Math.Round(ch.HolidayAmount, 0) };
                }).ToList(),
            }).ToList(),
        };
        var doc = Validate(ToDto(changed, profile.StartDate));
        var estimate = await EstimateDocAsync(userId, doc, effectiveFrom, ct);
        var over = estimate.Categories.FirstOrDefault(c => c.Over > 0);
        if (over is not null)
            throw new ValidationException($"「{doc.Categories[over.Index].Name}」調整後超過額度 {over.Over:N0}，請少調一點。");
        db.SettingsVersions.Add(new SettingsVersion
        {
            UserId = userId,
            EffectiveFrom = effectiveFrom,
            CreatedOn = today,
            CreatedAt = clock.UtcNow,
            Note = note,
            Document = SettingsJson.Serialize(doc),
        });
    }

    public async Task<SettingsEstimate> EstimateAsync(string userId, SettingsDto draft, CancellationToken ct = default)
    {
        var timeline = await GetTimelineAsync(userId, ct);
        var today = clock.LogicalToday(timeline.For(clock.Today).Doc.DayStart);
        SettingsDocument doc;
        try
        {
            doc = Validate(draft);
        }
        catch (ValidationException ex)
        {
            return new SettingsEstimate(today, today, 0, 0, 0, 0, 0, 0, 0, [], [ex.Message]);
        }
        return await EstimateDocAsync(userId, doc, today.AddDays(1), ct);
    }

    /// <summary>新設定生效那天所在的那一期：已經存在的期間就用它的起訖，否則依發薪規則推。</summary>
    private async Task<SettingsEstimate> EstimateDocAsync(string userId, SettingsDocument doc, DateOnly date, CancellationToken ct)
    {
        var existing = await db.Periods.AsNoTracking()
            .Where(p => p.UserId == userId && p.StartDate <= date && p.EndDate >= date)
            .Select(p => new { p.StartDate, p.EndDate }).FirstOrDefaultAsync(ct);
        DateOnly start, end;
        if (existing is not null)
        {
            (start, end) = (existing.StartDate, existing.EndDate);
        }
        else
        {
            var around = await calendar.GetDaysAsync(userId, date.AddDays(-80), date.AddDays(80), ct);
            bool Off(DateOnly d) => around.TryGetValue(d, out var i) ? i.IsHoliday : d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            start = Payday.LatestOnOrBefore(date, doc.Payday, Off);
            end = Payday.NextAfter(start, doc.Payday, Off).AddDays(-1);
        }

        var days = await calendar.GetDaysAsync(userId, start, end, ct);
        var weekdays = days.Count(d => !d.Value.IsHoliday);
        var holidays = days.Count(d => d.Value.IsHoliday);
        var charges = BudgetMath.FixedCharges(doc, start, end);
        var budgets = BudgetMath.CategoryBudgets(doc, charges, doc.MonthlyIncome);
        var fixedTotal = charges.Sum(f => f.Amount);
        var pctBase = doc.Base == PercentBase.AfterFixed ? Math.Max(0, doc.MonthlyIncome - fixedTotal) : doc.MonthlyIncome;

        var cats = doc.Categories.Select((c, i) =>
        {
            decimal wd = 0, hd = 0;
            if (c.Mode == BudgetMode.Daily)
            {
                var (units, _, _) = BudgetMath.AutoUnits(c, budgets[c.Id], weekdays, holidays);
                foreach (var s in c.Slots)
                {
                    wd += (units?.GetValueOrDefault($"{s.Id}:w") ?? s.WorkdayAmount) * weekdays;
                    hd += (units?.GetValueOrDefault($"{s.Id}:h") ?? s.HolidayAmount) * holidays;
                }
            }
            var scheduled = wd + hd;
            // 只有「自己設定」的每日類要擋；自動分配由分配器保證不超過
            var manual = c.Mode == BudgetMode.Daily && c.Auto is not { Enabled: true };
            return new CategoryEstimate(i, budgets[c.Id], wd, hd, scheduled, manual ? Math.Max(0, scheduled - budgets[c.Id]) : 0);
        }).ToList();

        return new SettingsEstimate(start, end, weekdays, holidays, doc.MonthlyIncome, fixedTotal, pctBase,
            doc.Categories.Where(c => c.Mode != BudgetMode.Fixed && c.IsPercent).Sum(c => c.Percent),
            doc.MonthlyIncome - budgets.Values.Sum(), cats, []);
    }

    public async Task<UserProfile> EnsureProfileAsync(string userId, CancellationToken ct = default)
    {
        var profile = await db.Profiles.FindAsync([userId], ct);
        var isNew = profile is null;
        if (profile is null)
        {
            profile = new UserProfile { UserId = userId, MonthlyIncome = 55000, CycleStartDay = 1 };
            db.Profiles.Add(profile);
        }

        var hasVersion = db.SettingsVersions.Local.Any(v => v.UserId == userId)
                         || await db.SettingsVersions.AnyAsync(v => v.UserId == userId, ct);
        if (!hasVersion)
        {
            var legacy = await db.Categories
                .Include(c => c.Slots).Include(c => c.FixedItems)
                .Where(c => c.UserId == userId && !c.IsArchived)
                .OrderBy(c => c.SortOrder)
                .ToListAsync(ct);

            SettingsDocument doc;
            string note;
            if (legacy.Count > 0)
            {
                doc = FromLegacy(profile, legacy);
                var maxId = legacy
                    .SelectMany(c => new[] { c.Id }.Concat(c.Slots.Select(s => s.Id)).Concat(c.FixedItems.Select(f => f.Id)))
                    .DefaultIfEmpty(0).Max();
                profile.NextSettingsId = Math.Max(profile.NextSettingsId, maxId + 1);
                note = "由舊設定匯入";
            }
            else
            {
                doc = AssignIds(DefaultDocument(), profile);
                note = "初始設定";
            }

            db.SettingsVersions.Add(new SettingsVersion
            {
                UserId = userId,
                EffectiveFrom = DateOnly.MinValue,
                CreatedOn = clock.Today,
                CreatedAt = clock.UtcNow,
                Note = note,
                Document = SettingsJson.Serialize(doc),
            });
            await db.SaveChangesAsync(ct);
        }
        else if (isNew)
        {
            await db.SaveChangesAsync(ct);
        }

        return profile;
    }

    public async Task<SettingsTimeline> GetTimelineAsync(string userId, CancellationToken ct = default)
    {
        await EnsureProfileAsync(userId, ct);
        var versions = await db.SettingsVersions.AsNoTracking().Where(v => v.UserId == userId).ToListAsync(ct);
        return new SettingsTimeline(versions);
    }

    public async Task<DateOnly> LogicalTodayAsync(string userId, CancellationToken ct = default)
    {
        var timeline = await GetTimelineAsync(userId, ct);
        return clock.LogicalToday(timeline.For(clock.Today).Doc.DayStart);
    }

    // ---------------- 驗證與轉換 ----------------

    private static SettingsDocument Validate(SettingsDto dto)
    {
        if (!TryParseTime(dto.LogicalDayStart, out var dayStart) || dayStart >= TimeSpan.FromHours(12))
            throw new ValidationException("邏輯日起點要是 00:00 到 11:59 之間的時間（HH:mm）。");
        if (dto.MonthlyIncome is < 0 or > 100_000_000) throw new ValidationException("月收入超出範圍。");
        if (dto.Payday is null || dto.Payday.Day is < 1 or > 31) throw new ValidationException("發薪日要在 1 到 31 號之間。");
        if (!Enum.IsDefined(dto.Payday.Shift)) throw new ValidationException("發薪日遇假日的處理方式不正確。");
        if (dto.Categories.Count == 0) throw new ValidationException("至少要有一個分類。");
        if (dto.Categories.Count > 16) throw new ValidationException("分類最多 16 個。");
        if (!Enum.IsDefined(dto.PercentBase) || !Enum.IsDefined(dto.IncomeKind)) throw new ValidationException("收入設定不正確。");
        var pctTotal = dto.Categories.Where(c => c.Mode != BudgetMode.Fixed && c.UsePercent).Sum(c => c.Percent);
        if (pctTotal > 100) throw new ValidationException($"用 % 計算的分類加起來是 {pctTotal:0.##}%，不能超過 100%（§7）。");

        foreach (var c in dto.Categories)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 40) throw new ValidationException("分類名稱不能空白，最多 40 字。");
            if (c.Percent is < 0 or > 100) throw new ValidationException($"「{c.Name}」的百分比要在 0 到 100 之間。");
            if (c.Slots.Count > 12) throw new ValidationException($"「{c.Name}」時段最多 12 個。");
            if (c.Mode == BudgetMode.Daily && c.Slots.Count == 0) throw new ValidationException($"「{c.Name}」是每日額度，至少要有一個時段。");
            if (c.Amount is < 0 or > 100_000_000 || c.Floor is < 0 or > 100_000_000) throw new ValidationException($"「{c.Name}」金額超出範圍。");
            if (c.Auto is { } auto && (auto.HolidayMultiplier is < 0.1m or > 5m || auto.RoundingUnit is < 1 or > 1000))
                throw new ValidationException($"「{c.Name}」假日倍率要在 0.1～5，取整單位要在 1～1000。");
            foreach (var s in c.Slots)
            {
                if (s.Weight is < 0 or > 1000 || s.HolidayWeight is < 0 or > 1000 || s.WorkdayLock is < 0 || s.HolidayLock is < 0 || s.WorkdayFloor is < 0 || s.HolidayFloor is < 0)
                    throw new ValidationException($"「{c.Name}・{s.Name}」權重、鎖定或底線不正確。");
            }

            // 時段邊界銜接（§3.2）：第一個從邏輯日起點開始，之後依序往後，最後一個到隔天起點
            var previous = -1;
            for (var i = 0; i < c.Slots.Count; i++)
            {
                var s = c.Slots[i];
                if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > 40) throw new ValidationException($"「{c.Name}」有時段名稱空白。");
                if (s.WorkdayAmount is < 0 or > 1_000_000 || s.HolidayAmount is < 0 or > 1_000_000)
                    throw new ValidationException($"「{c.Name}・{s.Name}」金額超出範圍。");
                if (!TryParseTime(s.Start, out var start)) throw new ValidationException($"「{c.Name}・{s.Name}」開始時間格式要是 HH:mm。");
                var offset = (int)((start - dayStart + TimeSpan.FromDays(1)).TotalMinutes % (24 * 60));
                if (i == 0 && offset != 0)
                    throw new ValidationException($"「{c.Name}」第一個時段要從邏輯日起點 {dayStart:hh\\:mm} 開始，24 小時才會切滿。");
                if (offset <= previous)
                    throw new ValidationException($"「{c.Name}」的時段要依時間先後排列，而且不能重疊。");
                previous = offset;
            }

            foreach (var f in c.FixedItems)
            {
                if (string.IsNullOrWhiteSpace(f.Name) || f.Name.Trim().Length > 60) throw new ValidationException($"「{c.Name}」有固定項目名稱空白。");
                if (f.Amount is < 0 or > 100_000_000) throw new ValidationException($"「{f.Name}」金額超出範圍。");
                if (f.DueDay is < 1 or > 31) throw new ValidationException($"「{f.Name}」扣款日要在 1 到 31 之間。");
                if (f.BillingMonth is < 1 or > 12) throw new ValidationException($"「{f.Name}」扣款月份要在 1 到 12 之間。");
            }
        }

        return new SettingsDocument(
            dayStart.ToString(@"hh\:mm"),
            Math.Round(dto.MonthlyIncome, 0),
            dto.Payday,
            dto.Categories.Select(c => new CategoryDoc(
                c.Id, c.Name.Trim(), c.Group, c.Mode,
                c.Mode == BudgetMode.Fixed ? 0 : Math.Round(c.Percent, 2),
                c.Slots.Select(s => new SlotDoc(s.Id, s.Name.Trim(), SettingsDocument.ParseTime(s.Start).ToString(@"hh\:mm"),
                    Math.Round(s.WorkdayAmount, 0), Math.Round(s.HolidayAmount, 0),
                    s.Weight, s.WorkdayLock, s.HolidayLock, s.WorkdayFloor, s.HolidayFloor, s.HolidayWeight)).ToList(),
                c.FixedItems.Select(f => new FixedItemDoc(f.Id, f.Name.Trim(), Math.Round(f.Amount, 0), f.DueDay, f.IsSubscription,
                    f.Cycle, f.Cycle == BillingCycle.Monthly ? null : f.BillingMonth ?? 1, f.IsActive, f.ActiveFrom)).ToList(),
                c.Mode == BudgetMode.Fixed ? null : c.UsePercent,
                c.Mode == BudgetMode.Fixed || c.UsePercent ? null : Math.Round(c.Amount ?? 0, 0),
                c.Floor,
                c.Mode == BudgetMode.Daily ? c.Auto : null
            )).ToList(),
            dto.PercentBase,
            dto.IncomeKind);
    }

    private static bool TryParseTime(string? s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s ?? "", @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out t) && t < TimeSpan.FromDays(1);

    /// <summary>
    /// 新的分類 / 時段 / 固定項目（Id ≤ 0）給一個跨版本不重複的 Id。
    /// 三種各自檢查重複：回報分別用 CategoryId、SlotId 對應，舊資料匯入的 Id 也是各自從 1 開始編，
    /// 分類 1 和時段 1 同時存在是正常的，不能因為「撞號」就把既有的 Id 換掉。
    /// </summary>
    private static SettingsDocument AssignIds(SettingsDocument doc, UserProfile profile)
    {
        var usedCategories = new HashSet<int>();
        var usedSlots = new HashSet<int>();
        var usedItems = new HashSet<int>();
        int Next(int id, HashSet<int> used)
        {
            if (id > 0 && used.Add(id)) return id;
            var n = profile.NextSettingsId++;
            used.Add(n);
            return n;
        }
        return doc with
        {
            Categories = doc.Categories.Select(c => c with
            {
                Id = Next(c.Id, usedCategories),
                Slots = c.Slots.Select(s => s with { Id = Next(s.Id, usedSlots) }).ToList(),
                FixedItems = c.FixedItems.Select(f => f with { Id = Next(f.Id, usedItems) }).ToList(),
            }).ToList(),
        };
    }

    public static SettingsDto ToDto(SettingsDocument d, DateOnly? startDate) => new(
        d.LogicalDayStart,
        d.MonthlyIncome,
        d.Payday,
        startDate,
        d.Categories.Select(c => new CategoryDto(
            c.Id, c.Name, c.Group, c.Mode, c.Percent,
            c.Slots.Select(s => new SlotDto(s.Id, s.Name, s.Start, s.WorkdayAmount, s.HolidayAmount,
                s.Weight, s.WorkdayLock, s.HolidayLock, s.WorkdayFloor, s.HolidayFloor, s.HolidayWeight)).ToList(),
            c.FixedItems.Select(f => new FixedItemDto(f.Id, f.Name, f.Amount, f.DueDay, f.IsSubscription, f.Cycle, f.BillingMonth, f.IsActive, f.ActiveFrom)).ToList(),
            c.IsPercent, c.Amount, c.Floor, c.Auto
        )).ToList(),
        d.Base,
        d.Kind);

    /// <summary>舊的覆寫式設定 → 一份設定文件（Id 沿用，回報才對得上）。</summary>
    private static SettingsDocument FromLegacy(UserProfile profile, List<Category> legacy) => new(
        DefaultDayStart,
        profile.MonthlyIncome,
        new PaydayRule(profile.CycleStartDay, HolidayShift.None),
        legacy.Select(c => new CategoryDoc(
            c.Id, c.Name, c.Group, c.Mode, c.Percent,
            c.Slots.OrderBy(s => s.SortOrder).Select((s, i) => new SlotDoc(
                s.Id, s.Name, DefaultSlotStart(i, c.Slots.Count), s.WorkdayAmount, s.HolidayAmount)).ToList(),
            c.FixedItems.OrderBy(f => f.Id).Select(f => new FixedItemDoc(
                f.Id, f.Name, f.Amount, f.DueDay, f.IsSubscription, f.Cycle, f.BillingMonth, f.IsActive, f.ActiveFrom)).ToList()
        )).ToList());

    /// <summary>沒有時間資料的舊時段：三餐給 04:00 / 10:30 / 16:00，其他數量從 04:00 起平均切。</summary>
    public static string DefaultSlotStart(int index, int count)
    {
        if (count == 3) return new[] { "04:00", "10:30", "16:00" }[index];
        var minutes = 4 * 60 + (int)Math.Round(index * 24.0 * 60 / Math.Max(1, count) / 30) * 30;
        return TimeSpan.FromMinutes(minutes % (24 * 60)).ToString(@"hh\:mm");
    }

    /// <summary>
    /// 第一次使用時的預設（以 55K 月薪為例），全部都能在設定頁改。Id 先填 0，由 AssignIds 配發。
    /// % 用規格 §7 的基準：佔「月收入扣掉固定支出後」。
    /// </summary>
    private static SettingsDocument DefaultDocument() => new(
        DefaultDayStart,
        55000,
        new PaydayRule(5, HolidayShift.Before),
        [
            new(0, "居住", CategoryGroup.Housing, BudgetMode.Fixed, 0, [],
            [
                new(0, "房租", 12000, 5, false, BillingCycle.Monthly, null, true, null),
                new(0, "水電瓦斯", 1200, 20, false, BillingCycle.Monthly, null, true, null),
            ]),
            new(0, "固定帳單", CategoryGroup.Other, BudgetMode.Fixed, 0, [],
            [
                new(0, "手機月租", 599, 15, false, BillingCycle.Monthly, null, true, null),
                new(0, "影音串流", 390, 8, true, BillingCycle.Monthly, null, true, null),
            ]),
            new(0, "餐費", CategoryGroup.Food, BudgetMode.Daily, 48,
            [
                new(0, "早餐", "04:00", 70, 90),
                new(0, "午餐", "10:30", 120, 180),
                new(0, "晚餐", "16:00", 150, 200),
            ], []),
            new(0, "交通", CategoryGroup.Transport, BudgetMode.Daily, 8, [new(0, "通勤", "04:00", 60, 0)], []),
            new(0, "衣著", CategoryGroup.Clothing, BudgetMode.Envelope, 6, [], []),
            new(0, "進修", CategoryGroup.Education, BudgetMode.Envelope, 6, [], []),
            new(0, "娛樂", CategoryGroup.Leisure, BudgetMode.Envelope, 17, [], []),
            new(0, "儲蓄", CategoryGroup.Savings, BudgetMode.Fixed, 0, [],
            [
                new(0, "0050 定期定額", 10000, 6, false, BillingCycle.Monthly, null, true, null),
                new(0, "緊急預備金", 5000, 6, false, BillingCycle.Monthly, null, true, null),
            ]),
        ],
        PercentBase.AfterFixed,
        IncomeKind.Fixed);
}
