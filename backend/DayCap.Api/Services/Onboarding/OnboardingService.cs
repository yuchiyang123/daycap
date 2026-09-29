using System.Text.Json;
using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services.Allocation;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services.Onboarding;

public interface IOnboardingService
{
    Task<OnboardingState> GetAsync(string userId, CancellationToken ct = default);
    Task<TemplatePreview> PreviewAsync(string userId, ApplyTemplateRequest req, CancellationToken ct = default);
    Task<TemplatePreview> ApplyAsync(string userId, ApplyTemplateRequest req, CancellationToken ct = default);
    Task SkipAsync(string userId, CancellationToken ct = default);
    Task<List<string>> MarkTipSeenAsync(string userId, string key, CancellationToken ct = default);
}

/// <summary>
/// 範本套用與新手引導（§20）。區塊金額交給 <see cref="Allocator.Allocate"/>（§8.1：範本套用分大區塊）：
/// 每個區塊一格、天數 1、權重＝範本 %、勾掉的鎖在 0、填了金額的鎖在該金額；儲蓄是受保護層（層級 0，最後才被擠），
/// 釋出份額的接收格子是儲蓄。
/// 分配器還沒實作時不自己重分配：勾選的照範本 %、填了金額的用填的，勾掉或釋出的份額留在待分配池，並清楚標示。
/// </summary>
public class OnboardingService(DayCapDbContext db, ISettingsService settings, IAppClock clock) : IOnboardingService
{
    public const decimal BlockRoundingUnit = 1m;
    public const decimal MealRoundingUnit = 5m;

    public async Task<OnboardingState> GetAsync(string userId, CancellationToken ct = default)
    {
        var profile = await settings.EnsureProfileAsync(userId, ct);
        return new OnboardingState(
            profile.OnboardedAt is null,
            profile.TemplateCode,
            Templates.All.Select(t => new TemplateDto(t.Code, t.Version, t.Name,
                t.Blocks.Select(b => new TemplateBlockDto(b.Key, b.Name, b.Percent, b.DefaultChecked, b.Hint)).ToList(),
                t.FixedItemNames)).ToList());
    }

    public async Task<TemplatePreview> PreviewAsync(string userId, ApplyTemplateRequest req, CancellationToken ct = default)
    {
        var (template, blocks) = Resolve(req);
        var preview = Compute(template, req, blocks);
        if (preview.Errors.Count > 0) return preview;

        var doc = BuildDocument(template, req, preview);
        var estimate = await settings.EstimateAsync(userId, SettingsService.ToDto(doc, null), ct);
        return preview with { Meals = MealPreview(doc, estimate) };
    }

    public async Task<TemplatePreview> ApplyAsync(string userId, ApplyTemplateRequest req, CancellationToken ct = default)
    {
        var profile = await settings.EnsureProfileAsync(userId, ct);
        var (template, blocks) = Resolve(req);
        var preview = Compute(template, req, blocks);
        if (preview.Errors.Count > 0) throw new ValidationException(string.Join("；", preview.Errors));

        var doc = BuildDocument(template, req, preview);

        // 還沒有任何紀錄時，已經建立的期間只是空殼：刪掉，讓新的發薪日重新切期間
        if (!await HasAnyFactsAsync(userId, ct))
            await db.Periods.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);

        // 範本值「複製」成使用者自己的設定（§20.6）：從最早就生效，取代初始預設
        await settings.SaveAsync(userId, new SaveSettingsRequest(
            SettingsService.ToDto(doc, profile.StartDate), DateOnly.MinValue, $"套用範本「{template.Name}」v{template.Version}"), ct);

        profile = await settings.EnsureProfileAsync(userId, ct);
        profile.TemplateCode = template.Code;
        profile.TemplateVersion = template.Version;
        profile.TemplateSnapshot = JsonSerializer.Serialize(new { request = req, blocks = preview.Blocks }, SettingsJson.Options);
        profile.OnboardedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return preview;
    }

    public async Task SkipAsync(string userId, CancellationToken ct = default)
    {
        var profile = await settings.EnsureProfileAsync(userId, ct);
        profile.OnboardedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<string>> MarkTipSeenAsync(string userId, string key, CancellationToken ct = default)
    {
        key = key.Trim().ToLowerInvariant();
        if (key.Length is 0 or > 32 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ValidationException("提示代號不正確。");
        var profile = await settings.EnsureProfileAsync(userId, ct);
        var seen = SeenTips(profile);
        if (!seen.Contains(key))
        {
            seen.Add(key);
            profile.SeenTips = string.Join(",", seen);
            await db.SaveChangesAsync(ct);
        }
        return seen;
    }

    public static List<string> SeenTips(UserProfile p) =>
        (p.SeenTips ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ---------------- 計算 ----------------

    private static (Template, List<(TemplateBlock Block, BlockChoice Choice)>) Resolve(ApplyTemplateRequest req)
    {
        var template = Templates.Find(req.TemplateCode) ?? throw new ValidationException("找不到這個範本。");
        if (req.Income is <= 0 or > 100_000_000) throw new ValidationException("月收入要大於 0。");
        if (req.PaydayDay is < 1 or > 31) throw new ValidationException("發薪日要是 1 到 31 號。");
        var blocks = template.Blocks.Select(b =>
        {
            var c = req.Blocks.FirstOrDefault(x => x.Key == b.Key) ?? new BlockChoice(b.Key, b.DefaultChecked, null);
            if (c.Amount is < 0) throw new ValidationException($"「{b.Name}」金額不能是負的。");
            return (b, c);
        }).ToList();
        return (template, blocks);
    }

    /// <summary>
    /// 基準＝套用勾選後、套用鎖定前（§20.5 規則 7）；結果＝再加上鎖定。兩次都交給分配器，差額是實際重算的前後差。
    /// </summary>
    private static TemplatePreview Compute(Template template, ApplyTemplateRequest req, List<(TemplateBlock Block, BlockChoice Choice)> blocks)
    {
        var income = req.Income;
        var errors = new List<string>();
        var warnings = new List<string>();

        var lockedTotal = blocks.Where(x => x.Choice.Checked && x.Choice.Amount is not null).Sum(x => x.Choice.Amount!.Value);
        if (lockedTotal > income) errors.Add($"填的固定金額合計 {lockedTotal:N0} 已經超過月收入 {income:N0}。");
        var savingsFloor = req.SavingsMinPercent is > 0 ? Math.Round(income * req.SavingsMinPercent.Value / 100m, 0) : (decimal?)null;
        if (savingsFloor is not null && lockedTotal + savingsFloor > income)
            errors.Add($"固定金額 {lockedTotal:N0} 加上儲蓄底線 {savingsFloor:N0} 超過月收入。");
        if (errors.Count > 0) return Empty(template, blocks, errors);

        List<AllocationCell> Cells(bool withLocks) => blocks.Select(x => new AllocationCell(
            x.Block.Key,
            1,
            x.Block.Percent,
            !x.Choice.Checked ? 0m : withLocks ? x.Choice.Amount : null,
            x.Block.Key == Template.Savings ? savingsFloor : null,
            x.Block.Key == Template.Savings ? 0 : 1)).ToList();

        Dictionary<string, decimal> baseline, result;
        decimal leftover;
        var allocatorReady = true;
        try
        {
            var b = Allocator.Allocate(income, Cells(false), BlockRoundingUnit, Template.Savings);
            var r = Allocator.Allocate(income, Cells(true), BlockRoundingUnit, Template.Savings);
            if (b.Errors.Count > 0 || r.Errors.Count > 0)
                return Empty(template, blocks, [.. b.Errors, .. r.Errors.Except(b.Errors)]);
            warnings.AddRange(r.Warnings);
            baseline = b.UnitAmount.ToDictionary(k => k.Key, k => k.Value);
            result = r.UnitAmount.ToDictionary(k => k.Key, k => k.Value);
            leftover = r.Leftover;
        }
        catch (NotImplementedException)
        {
            // 不自己重分配：照範本 %、鎖定用填的；勾掉 / 釋出的份額留在待分配池
            allocatorReady = false;
            baseline = blocks.ToDictionary(x => x.Block.Key, x => x.Choice.Checked ? Math.Floor(income * x.Block.Percent / 100m) : 0m);
            result = blocks.ToDictionary(x => x.Block.Key, x => x.Choice.Checked ? x.Choice.Amount ?? baseline[x.Block.Key] : 0m);
            var total = result.Values.Sum();
            if (total > income)
            {
                errors.Add($"分配器還沒實作，填的金額比範本份額多出來的部分沒辦法由其他區塊吸收：合計 {total:N0} 超過月收入 {income:N0}。請調低金額或取消勾選其他區塊。");
                return Empty(template, blocks, errors);
            }
            leftover = income - total;
            warnings.Add("分配器（§8.1）還沒實作：勾掉或填得比範本少的份額不會自動轉進儲蓄，先留在待分配池；填得比範本多的也不會由其他區塊分攤。");
        }

        var rows = blocks.Select(x =>
        {
            var key = x.Block.Key;
            var before = baseline.GetValueOrDefault(key);
            var after = result.GetValueOrDefault(key);
            return new TemplateBlockPreview(key, x.Block.Name, x.Choice.Checked, x.Choice.Amount is not null && x.Choice.Checked,
                x.Block.Percent, before, after, after - before, income == 0 ? 0 : Math.Round(after / income * 100m, 1), x.Block.Hint);
        }).ToList();
        return new TemplatePreview(template.Code, template.Version, template.Name, income, allocatorReady, rows, leftover, null, errors, warnings);
    }

    private static TemplatePreview Empty(Template t, List<(TemplateBlock Block, BlockChoice Choice)> blocks, List<string> errors) =>
        new(t.Code, t.Version, t.Name, 0, true,
            blocks.Select(x => new TemplateBlockPreview(x.Block.Key, x.Block.Name, x.Choice.Checked, x.Choice.Amount is not null,
                x.Block.Percent, 0, 0, 0, 0, x.Block.Hint)).ToList(), 0, null, errors, []);

    /// <summary>
    /// 預覽結果 → 設定文件（§20.5 規則 2）：房租、孝親費、保險、儲蓄轉成固定支出；
    /// 餐費（每日、早午晚自動分配）、交通、娛樂（月額度）用 %，基準是「扣掉固定支出後」。
    /// </summary>
    private static SettingsDocument BuildDocument(Template t, ApplyTemplateRequest req, TemplatePreview p)
    {
        decimal Amount(string key) => p.Blocks.FirstOrDefault(b => b.Key == key)?.Amount ?? 0;
        var payDay = Math.Min(req.PaydayDay, 28);
        FixedItemDoc Item(string name, decimal amount, int? due = null) =>
            new(0, name, amount, due ?? payDay, false, BillingCycle.Monthly, null, true, null);

        var housing = new List<FixedItemDoc>();
        if (Amount(Template.Rent) > 0) housing.Add(Item("房租", Amount(Template.Rent)));
        if (Amount(Template.Family) > 0) housing.Add(Item("孝親費", Amount(Template.Family)));
        var head = new List<CategoryDoc>();
        var tail = new List<CategoryDoc>();
        if (housing.Count > 0) head.Add(new(0, "居住", CategoryGroup.Housing, BudgetMode.Fixed, 0, [], housing));
        if (Amount(Template.Insurance) > 0)
            head.Add(new(0, "保險", CategoryGroup.Other, BudgetMode.Fixed, 0, [], [Item("保險（每月提撥）", Amount(Template.Insurance))]));
        if (Amount(Template.Savings) > 0)
            tail.Add(new(0, "儲蓄", CategoryGroup.Savings, BudgetMode.Fixed, 0, [], [Item("每月儲蓄", Amount(Template.Savings))]));

        var fixedTotal = head.Concat(tail).SelectMany(c => c.FixedItems).Sum(i => i.Amount);
        var pctBase = req.Income - fixedTotal;
        // 設定裡的 % 存到小數兩位（SettingsService.Validate），換算回金額每個分類可能差 1 元，差額留在待分配池
        decimal Pct(string key) => pctBase <= 0 ? 0 : Math.Round(Amount(key) / pctBase * 100m, 2);

        var m = t.Meals;
        var food = new CategoryDoc(0, "餐費", CategoryGroup.Food, BudgetMode.Daily, Pct(Template.Food),
            [
                new(0, "早餐", SettingsService.DefaultDayStart, 0, 0, Weight: m.Breakfast),
                new(0, "午餐", "10:30", 0, 0, Weight: m.Lunch),
                new(0, "晚餐", "16:00", 0, 0, Weight: m.Dinner),
            ], [],
            Auto: new MealAuto(true, m.HolidayMultiplier, MealRoundingUnit));

        List<CategoryDoc> cats =
        [
            .. head,
            food,
            new(0, "交通", CategoryGroup.Transport, BudgetMode.Envelope, Pct(Template.Transport), [], []),
            new(0, "娛樂", CategoryGroup.Leisure, BudgetMode.Envelope, Pct(Template.Leisure), [], []),
            .. tail,
        ];

        // 四捨五入後 % 合計可能變成 100.01：多出來的從最大的那個分類扣
        var excess = cats.Where(x => x.Mode != BudgetMode.Fixed).Sum(x => x.Percent) - 100m;
        if (excess > 0)
        {
            var biggest = cats.Where(x => x.Mode != BudgetMode.Fixed).MaxBy(x => x.Percent)!;
            cats[cats.IndexOf(biggest)] = biggest with { Percent = biggest.Percent - excess };
        }

        // 先給暫時的負數 Id（試算時各分類 / 時段才分得開），存檔時 AssignIds 會換成正式 Id
        int c = 0, sl = 0, it = 0;
        cats = cats.Select(x => x with
        {
            Id = --c,
            Slots = x.Slots.Select(y => y with { Id = --sl }).ToList(),
            FixedItems = x.FixedItems.Select(y => y with { Id = --it }).ToList(),
        }).ToList();

        return new SettingsDocument(SettingsService.DefaultDayStart, req.Income, new PaydayRule(req.PaydayDay, HolidayShift.Before),
            cats, PercentBase.AfterFixed, IncomeKind.Fixed);
    }

    /// <summary>§20.5 規則 10：每餐金額由分配器依這期實際平日 / 假日天數算。</summary>
    private static MealPreviewDto? MealPreview(SettingsDocument doc, SettingsEstimate est)
    {
        var idx = doc.Categories.FindIndex(c => c.Mode == BudgetMode.Daily);
        if (idx < 0) return null;
        var cat = doc.Categories[idx];
        var budget = est.Categories.FirstOrDefault(c => c.Index == idx)?.Budget ?? 0;
        var days = est.Weekdays + est.Holidays;
        var (units, _, problem) = BudgetMath.AutoUnits(cat, budget, est.Weekdays, est.Holidays);
        var slots = units is null
            ? null
            : cat.Slots.Select(s => new MealSlotPreview(s.Name,
                units.GetValueOrDefault($"{s.Id}:w"), units.GetValueOrDefault($"{s.Id}:h"))).ToList();
        return new MealPreviewDto(est.PeriodStart, est.PeriodEnd, est.Weekdays, est.Holidays, budget,
            days == 0 ? 0 : Math.Floor(budget / days), slots, problem);
    }

    private async Task<bool> HasAnyFactsAsync(string userId, CancellationToken ct)
    {
        var periodIds = db.Periods.Where(p => p.UserId == userId).Select(p => p.Id);
        return await db.Entries.AnyAsync(e => periodIds.Contains(e.PeriodId), ct)
               || await db.PoolTransfers.AnyAsync(t => periodIds.Contains(t.PeriodId), ct)
               || await db.IncomeAdjustments.AnyAsync(a => periodIds.Contains(a.PeriodId), ct)
               || await db.Reconciliations.AnyAsync(r => r.UserId == userId, ct)
               || await db.AccountTransfers.AnyAsync(t => t.UserId == userId, ct)
               || await db.AssetAdjustments.AnyAsync(a => a.UserId == userId, ct);
    }
}
