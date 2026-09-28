using DayCap.Api.Models.Entities;
using DayCap.Api.Services;

namespace DayCap.Api.Tests;

public class BudgetEngineTests
{
    // 2026-10-01 ~ 2026-10-10，每天早餐 100、午餐 200（10 天共 3,000），餐費額度 3,000，收入 10,000。
    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly DateOnly End = new(2026, 10, 10);
    private const int Food = 1;
    private const int Fun = 2;
    private const int Breakfast = 11;
    private const int Lunch = 12;

    private static BudgetPeriod MakePeriod(int foodBudget = 3000, int funBudget = 1000, int income = 10000)
    {
        var p = new BudgetPeriod { Id = 1, UserId = "u", StartDate = Start, EndDate = End, Income = income };
        p.Categories.Add(new PeriodCategory { CategoryId = Food, Name = "餐費", Mode = BudgetMode.Daily, Budget = foodBudget, SortOrder = 0 });
        p.Categories.Add(new PeriodCategory { CategoryId = Fun, Name = "娛樂", Mode = BudgetMode.Envelope, Budget = funBudget, SortOrder = 1 });
        for (var d = Start; d <= End; d = d.AddDays(1))
        {
            p.Allocations.Add(new DayAllocation { Date = d, CategoryId = Food, SlotId = Breakfast, SlotName = "早餐", SortOrder = 0, Planned = 100 });
            p.Allocations.Add(new DayAllocation { Date = d, CategoryId = Food, SlotId = Lunch, SlotName = "午餐", SortOrder = 1, Planned = 200 });
        }
        return p;
    }

    private static int nextId = 1;

    private static Entry Report(DateOnly date, int? slot, int amount, bool usePool = true,
        EntryInputMode mode = EntryInputMode.Actual, int category = Food, DateOnly? reportedOn = null) => new()
    {
        Id = nextId++,
        Date = date,
        CategoryId = category,
        SlotId = slot,
        InputMode = mode,
        InputAmount = amount,
        UsePool = usePool,
        // 台北中午 = UTC 04:00，換算回本地日期一定是同一天
        CreatedAt = (reportedOn ?? date).ToDateTime(new TimeOnly(4, 0), DateTimeKind.Utc).AddSeconds(nextId),
    };

    private static Models.Dtos.PeriodView Run(BudgetPeriod p, DateOnly today) =>
        BudgetEngine.Compute(p, today, utc => DateOnly.FromDateTime(utc.AddHours(8)), new Dictionary<DateOnly, DayInfo>());

    [Fact]
    public void Unreported_slots_are_treated_as_spent_exactly_as_planned()
    {
        var view = Run(MakePeriod(), new DateOnly(2026, 10, 4));

        var food = view.Categories.Single(c => c.CategoryId == Food);
        Assert.Equal(4 * 300, food.Spent);            // 1~4 號照預算
        Assert.Equal(6 * 300, food.PlannedRemaining); // 5~10 號還沒到
        Assert.All(view.Days, d => Assert.Equal(0, d.Net));
        // 收入 10,000 − 餐費 3,000 − 娛樂 1,000 = 6,000 未分配
        Assert.Equal(6000, view.Pool.Balance);
    }

    [Fact]
    public void Spending_less_than_planned_moves_the_difference_into_the_pool()
    {
        var p = MakePeriod();
        p.Entries.Add(Report(Start, Breakfast, 60));

        var view = Run(p, Start);

        Assert.Equal(6000 + 40, view.Pool.Balance);
        Assert.Equal(40, view.Days[0].Net);
        Assert.Contains(view.Pool.Lines, l => l.Kind == "Surplus" && l.Amount == 40);
    }

    [Fact]
    public void Overspend_is_covered_by_the_pool_first_when_allowed()
    {
        var p = MakePeriod();
        p.Entries.Add(Report(Start, Breakfast, 130));

        var view = Run(p, Start);

        Assert.Equal(6000 - 30, view.Pool.Balance);
        Assert.Equal(-30, view.Days[0].Net);
        // 後面的日子不受影響
        Assert.All(view.Days.Skip(1), d => Assert.Equal(d.BasePlanned, d.Planned));
    }

    [Fact]
    public void Overspend_without_pool_is_spread_proportionally_over_later_unreported_slots()
    {
        var p = MakePeriod();
        p.Entries.Add(Report(Start, Lunch, 470, usePool: false)); // 超支 270

        var view = Run(p, Start);

        Assert.Equal(6000, view.Pool.Balance);
        var later = view.Days.Skip(1).ToList();
        Assert.Equal(270, later.Sum(d => d.BasePlanned - d.Planned));
        // 9 天、每天早餐 100 / 午餐 200：依比例 = 早餐每天少 10、午餐每天少 20
        Assert.All(later, d =>
        {
            Assert.Equal(90, d.Slots.Single(s => s.SlotId == Breakfast).Planned);
            Assert.Equal(180, d.Slots.Single(s => s.SlotId == Lunch).Planned);
        });
        var entry = view.Entries.Single();
        Assert.Equal(270, entry.Spread);
        Assert.Equal(18, entry.SpreadSlots);
    }

    [Fact]
    public void Partial_pool_covers_what_it_can_and_the_rest_is_spread()
    {
        var p = MakePeriod(foodBudget: 3000, funBudget: 7000); // 未分配 = 0
        p.Transfers(new PoolTransfer { Id = 1, Date = Start, Amount = 50, CreatedAt = Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) });
        p.Entries.Add(Report(Start, Lunch, 300)); // 超支 100：待定區 50 + 攤 50

        var view = Run(p, Start);

        var entry = view.Entries.Single();
        Assert.Equal(50, entry.FromPool);
        Assert.Equal(50, entry.Spread);
        Assert.Equal(0, view.Pool.Balance);
    }

    [Fact]
    public void Overspend_on_the_last_day_cannot_be_spread_and_drives_the_pool_negative()
    {
        var p = MakePeriod(foodBudget: 3000, funBudget: 7000);
        p.Entries.Add(Report(End, Lunch, 500, usePool: false));

        var view = Run(p, End);

        var entry = view.Entries.Single();
        Assert.Equal(0, entry.Spread);
        Assert.Equal(300, entry.Unabsorbed);
        Assert.Equal(-300, view.Pool.Balance);
    }

    [Fact]
    public void Overage_input_mode_adds_to_the_current_effective_amount()
    {
        var p = MakePeriod();
        p.Entries.Add(Report(Start, Breakfast, 30, mode: EntryInputMode.Overage));

        var entry = Run(p, Start).Entries.Single();

        Assert.Equal(130, entry.Actual);
        Assert.Equal(30, entry.Diff);
    }

    [Fact]
    public void Back_dated_report_only_spreads_to_days_after_the_day_it_was_reported()
    {
        var p = MakePeriod();
        // 10/2 的午餐超支 90，10/4 才補回報：10/3、10/4 已經過去不能動，只攤到 10/5~10/10
        p.Entries.Add(Report(new DateOnly(2026, 10, 2), Lunch, 290, usePool: false, reportedOn: new DateOnly(2026, 10, 4)));

        var view = Run(p, new DateOnly(2026, 10, 4));

        Assert.All(view.Days.Where(d => d.Date <= new DateOnly(2026, 10, 4)), d => Assert.Equal(d.BasePlanned, d.Planned));
        Assert.Equal(90, view.Days.Where(d => d.Date > new DateOnly(2026, 10, 4)).Sum(d => d.BasePlanned - d.Planned));
    }

    [Fact]
    public void Reporting_the_same_slot_again_replaces_the_previous_report()
    {
        var p = MakePeriod();
        p.Entries.Add(Report(Start, Breakfast, 150));
        p.Entries.Add(Report(Start, Breakfast, 80));

        var view = Run(p, Start);

        Assert.Single(view.Entries);
        Assert.Equal(6000 + 20, view.Pool.Balance);
    }

    [Fact]
    public void Removing_a_report_undoes_all_of_its_effects()
    {
        var p = MakePeriod();
        var e = Report(Start, Lunch, 470, usePool: false);
        p.Entries.Add(e);
        Assert.NotEqual(0, Run(p, Start).Entries.Single().Spread);

        p.Entries.Remove(e);
        var view = Run(p, Start);

        Assert.All(view.Days, d => Assert.Equal(d.BasePlanned, d.Planned));
        Assert.Equal(6000, view.Pool.Balance);
    }

    [Fact]
    public void Slots_already_reported_in_the_future_are_not_reduced_by_a_spread()
    {
        var p = MakePeriod();
        var tomorrow = Start.AddDays(1);
        p.Entries.Add(Report(tomorrow, Lunch, 200, reportedOn: Start)); // 先回報明天午餐
        p.Entries.Add(Report(Start, Lunch, 470, usePool: false));

        var view = Run(p, Start);

        var tomorrowLunch = view.Days[1].Slots.Single(s => s.SlotId == Lunch);
        Assert.Equal(200, tomorrowLunch.Planned);
    }

    [Fact]
    public void Envelope_overspend_draws_the_excess_from_the_pool()
    {
        var p = MakePeriod(funBudget: 1000);
        p.Entries.Add(Report(Start, null, 800, category: Fun));
        p.Entries.Add(Report(Start, null, 500, category: Fun)); // 累計 1,300，超過 300

        var view = Run(p, Start);

        Assert.Equal(1300, view.Categories.Single(c => c.CategoryId == Fun).Spent);
        Assert.Equal(6000 - 300, view.Pool.Balance);
        // 月額度類不算進每日 +/-
        Assert.Equal(0, view.Days[0].Net);
    }

    [Fact]
    public void Daily_schedule_below_the_category_budget_puts_the_gap_into_the_opening_pool()
    {
        var view = Run(MakePeriod(foodBudget: 3500), Start);

        Assert.Equal(10000 - 3500 - 1000 + 500, view.Pool.Opening);
        Assert.Contains(view.Pool.Lines, l => l.Kind == "Opening" && l.Amount == 500);
    }

    [Theory]
    [InlineData(10, new[] { 1, 1, 1 }, new[] { 1, 1, 1 })]
    [InlineData(2, new[] { 100, 100, 100 }, new[] { 1, 1, 0 })]
    [InlineData(100, new[] { 50, 150 }, new[] { 25, 75 })]
    [InlineData(7, new[] { 0, 10 }, new[] { 0, 7 })]
    public void Distribute_is_proportional_and_never_exceeds_a_weight(int amount, int[] weights, int[] expected)
    {
        var result = BudgetEngine.Distribute(amount, weights);
        Assert.Equal(expected, result);
        Assert.True(result.Zip(weights).All(x => x.First <= x.Second));
    }
}

internal static class PeriodTestExtensions
{
    public static void Transfers(this BudgetPeriod p, PoolTransfer t) => p.PoolTransfers.Add(t);
}
