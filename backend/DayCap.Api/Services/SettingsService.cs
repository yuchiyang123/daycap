using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface ISettingsService
{
    Task<SettingsDto> GetAsync(string userId, CancellationToken ct = default);
    Task<SettingsDto> SaveAsync(string userId, SettingsDto dto, CancellationToken ct = default);
    Task<UserProfile> EnsureProfileAsync(string userId, CancellationToken ct = default);
    Task<List<Category>> GetActiveCategoriesAsync(string userId, CancellationToken ct = default);
}

public class SettingsService(DayCapDbContext db) : ISettingsService
{
    public async Task<SettingsDto> GetAsync(string userId, CancellationToken ct = default)
    {
        var profile = await EnsureProfileAsync(userId, ct);
        var categories = await GetActiveCategoriesAsync(userId, ct);
        return ToDto(profile, categories);
    }

    public async Task<SettingsDto> SaveAsync(string userId, SettingsDto dto, CancellationToken ct = default)
    {
        Validate(dto);
        var profile = await EnsureProfileAsync(userId, ct);
        profile.MonthlyIncome = dto.MonthlyIncome;
        profile.CycleStartDay = dto.CycleStartDay;
        profile.StartDate = dto.StartDate;

        var existing = await db.Categories
            .Include(c => c.Slots).Include(c => c.FixedItems)
            .Where(c => c.UserId == userId)
            .ToListAsync(ct);
        var byId = existing.ToDictionary(c => c.Id);
        var keep = new HashSet<int>();

        for (var i = 0; i < dto.Categories.Count; i++)
        {
            var c = dto.Categories[i];
            if (c.Id == 0 || !byId.TryGetValue(c.Id, out var entity))
            {
                entity = new Category { UserId = userId };
                db.Categories.Add(entity);
            }
            else
            {
                keep.Add(entity.Id);
            }

            entity.Name = c.Name.Trim();
            entity.Group = c.Group;
            entity.Mode = c.Mode;
            entity.Percent = c.Mode == BudgetMode.Fixed ? 0 : Math.Round(c.Percent, 2);
            entity.SortOrder = i;
            entity.IsArchived = false;

            SyncSlots(entity, c.Slots);
            SyncFixedItems(entity, c.FixedItems);
        }

        foreach (var c in existing.Where(c => !keep.Contains(c.Id)))
        {
            c.IsArchived = true;
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(userId, ct);
    }

    public async Task<UserProfile> EnsureProfileAsync(string userId, CancellationToken ct = default)
    {
        var profile = await db.Profiles.FindAsync([userId], ct);
        if (profile is not null) return profile;

        profile = new UserProfile { UserId = userId, MonthlyIncome = 55000, CycleStartDay = 1 };
        db.Profiles.Add(profile);
        db.Categories.AddRange(DefaultCategories(userId));
        await db.SaveChangesAsync(ct);
        return profile;
    }

    public Task<List<Category>> GetActiveCategoriesAsync(string userId, CancellationToken ct = default) =>
        db.Categories
            .Include(c => c.Slots.OrderBy(s => s.SortOrder))
            .Include(c => c.FixedItems.OrderBy(f => f.Id))
            .Where(c => c.UserId == userId && !c.IsArchived)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

    private void SyncSlots(Category entity, List<SlotDto> slots)
    {
        var incoming = slots.Where(s => s.Id != 0).Select(s => s.Id).ToHashSet();
        foreach (var removed in entity.Slots.Where(s => !incoming.Contains(s.Id)).ToList())
        {
            entity.Slots.Remove(removed);
            db.DailySlots.Remove(removed);
        }
        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            var slot = s.Id == 0 ? null : entity.Slots.FirstOrDefault(x => x.Id == s.Id);
            if (slot is null)
            {
                slot = new DailySlot();
                entity.Slots.Add(slot);
            }
            slot.Name = s.Name.Trim();
            slot.WorkdayAmount = s.WorkdayAmount;
            slot.HolidayAmount = s.HolidayAmount;
            slot.SortOrder = i;
        }
    }

    private void SyncFixedItems(Category entity, List<FixedItemDto> items)
    {
        var incoming = items.Where(s => s.Id != 0).Select(s => s.Id).ToHashSet();
        foreach (var removed in entity.FixedItems.Where(s => !incoming.Contains(s.Id)).ToList())
        {
            entity.FixedItems.Remove(removed);
            db.FixedItems.Remove(removed);
        }
        foreach (var f in items)
        {
            var item = f.Id == 0 ? null : entity.FixedItems.FirstOrDefault(x => x.Id == f.Id);
            if (item is null)
            {
                item = new FixedItem();
                entity.FixedItems.Add(item);
            }
            item.Name = f.Name.Trim();
            item.Amount = f.Amount;
            item.DueDay = f.DueDay;
            item.IsSubscription = f.IsSubscription;
            item.Cycle = f.Cycle;
            item.BillingMonth = f.Cycle == BillingCycle.Monthly ? null : f.BillingMonth ?? 1;
            item.IsActive = f.IsActive;
            item.ActiveFrom = f.ActiveFrom;
        }
    }

    private static void Validate(SettingsDto dto)
    {
        if (dto.MonthlyIncome is < 0 or > 100_000_000) throw new ValidationException("月收入超出範圍。");
        if (dto.CycleStartDay is < 1 or > 28) throw new ValidationException("週期起始日要在 1 到 28 之間。");
        if (dto.Categories.Count == 0) throw new ValidationException("至少要有一個分類。");
        if (dto.Categories.Count > 16) throw new ValidationException("分類最多 16 個。");

        foreach (var c in dto.Categories)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 40) throw new ValidationException("分類名稱不能空白，最多 40 字。");
            if (c.Percent is < 0 or > 100) throw new ValidationException($"「{c.Name}」的百分比要在 0 到 100 之間。");
            if (c.Slots.Count > 12) throw new ValidationException($"「{c.Name}」時段最多 12 個。");
            if (c.Mode == BudgetMode.Daily && c.Slots.Count == 0) throw new ValidationException($"「{c.Name}」是每日額度，至少要有一個時段。");
            foreach (var s in c.Slots)
            {
                if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > 40) throw new ValidationException($"「{c.Name}」有時段名稱空白。");
                if (s.WorkdayAmount is < 0 or > 1_000_000 || s.HolidayAmount is < 0 or > 1_000_000) throw new ValidationException($"「{c.Name}・{s.Name}」金額超出範圍。");
            }
            foreach (var f in c.FixedItems)
            {
                if (string.IsNullOrWhiteSpace(f.Name) || f.Name.Trim().Length > 60) throw new ValidationException($"「{c.Name}」有固定項目名稱空白。");
                if (f.Amount is < 0 or > 100_000_000) throw new ValidationException($"「{f.Name}」金額超出範圍。");
                if (f.DueDay is < 1 or > 31) throw new ValidationException($"「{f.Name}」扣款日要在 1 到 31 之間。");
                if (f.BillingMonth is < 1 or > 12) throw new ValidationException($"「{f.Name}」扣款月份要在 1 到 12 之間。");
            }
        }
    }

    private static SettingsDto ToDto(UserProfile profile, List<Category> categories) => new(
        profile.MonthlyIncome,
        profile.CycleStartDay,
        profile.StartDate,
        categories.Select(c => new CategoryDto(
            c.Id, c.Name, c.Group, c.Mode, c.Percent,
            c.Slots.OrderBy(s => s.SortOrder).Select(s => new SlotDto(s.Id, s.Name, s.WorkdayAmount, s.HolidayAmount)).ToList(),
            c.FixedItems.OrderBy(f => f.Id).Select(f => new FixedItemDto(f.Id, f.Name, f.Amount, f.DueDay, f.IsSubscription, f.Cycle, f.BillingMonth, f.IsActive, f.ActiveFrom)).ToList()
        )).ToList());

    /// <summary>第一次登入時給一份以 55K 月薪為例的起始設定，全部都能在設定頁改。</summary>
    private static IEnumerable<Category> DefaultCategories(string userId)
    {
        var order = 0;
        Category Make(string name, CategoryGroup group, BudgetMode mode, decimal percent) =>
            new() { UserId = userId, Name = name, Group = group, Mode = mode, Percent = percent, SortOrder = order++ };

        var housing = Make("居住", CategoryGroup.Housing, BudgetMode.Fixed, 0);
        housing.FixedItems.Add(new FixedItem { Name = "房租", Amount = 12000, DueDay = 5 });
        housing.FixedItems.Add(new FixedItem { Name = "水電瓦斯", Amount = 1200, DueDay = 20 });

        var bills = Make("固定帳單", CategoryGroup.Other, BudgetMode.Fixed, 0);
        bills.FixedItems.Add(new FixedItem { Name = "手機月租", Amount = 599, DueDay = 15 });
        bills.FixedItems.Add(new FixedItem { Name = "影音串流", Amount = 390, DueDay = 8, IsSubscription = true });

        var food = Make("餐費", CategoryGroup.Food, BudgetMode.Daily, 22);
        food.Slots.Add(new DailySlot { Name = "早餐", WorkdayAmount = 70, HolidayAmount = 90, SortOrder = 0 });
        food.Slots.Add(new DailySlot { Name = "午餐", WorkdayAmount = 120, HolidayAmount = 180, SortOrder = 1 });
        food.Slots.Add(new DailySlot { Name = "晚餐", WorkdayAmount = 150, HolidayAmount = 200, SortOrder = 2 });

        var transport = Make("交通", CategoryGroup.Transport, BudgetMode.Daily, 4);
        transport.Slots.Add(new DailySlot { Name = "通勤", WorkdayAmount = 60, HolidayAmount = 0, SortOrder = 0 });

        var savings = Make("儲蓄", CategoryGroup.Savings, BudgetMode.Fixed, 0);
        savings.FixedItems.Add(new FixedItem { Name = "0050 定期定額", Amount = 10000, DueDay = 6 });
        savings.FixedItems.Add(new FixedItem { Name = "緊急預備金", Amount = 5000, DueDay = 6 });

        return
        [
            housing,
            bills,
            food,
            transport,
            Make("衣著", CategoryGroup.Clothing, BudgetMode.Envelope, 3),
            Make("進修", CategoryGroup.Education, BudgetMode.Envelope, 3),
            Make("娛樂", CategoryGroup.Leisure, BudgetMode.Envelope, 8),
            savings,
        ];
    }
}
