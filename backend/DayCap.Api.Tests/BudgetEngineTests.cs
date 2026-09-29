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

    private static (BudgetPeriod Period, PeriodPlan Plan) Make(decimal foodBudget = 3000, decimal funBudget = 1000, decimal income = 10000,
        List<TodayLowering>? lowerings = null)
    {
        var period = new BudgetPeriod { Id = 1, UserId = "u", StartDate = Start, EndDate = End };
        var allocations = new List<DayAllocation>();
        for (var d = Start; d <= End; d = d.AddDays(1))
        {
            allocations.Add(new DayAllocation { Date = d, CategoryId = Food, SlotId = Breakfast, SlotName = "早餐", SortOrder = 0, Planned = 100, BaselinePlanned = 100 });
            allocations.Add(new DayAllocation { Date = d, CategoryId = Food, SlotId = Lunch, SlotName = "午餐", SortOrder = 1, Planned = 200, BaselinePlanned = 200 });
        }
        var plan = new PeriodPlan
        {
            Income = income,
            Categories =
            [
                new PeriodCategory { CategoryId = Food, Name = "餐費", Mode = BudgetMode.Daily, Budget = foodBudget, SortOrder = 0 },
                new PeriodCategory { CategoryId = Fun, Name = "娛樂", Mode = BudgetMode.Envelope, Budget = funBudget, SortOrder = 1 },
            ],
            Allocations = allocations,
            Lowerings = lowerings ?? [],
        };
        return (period, plan);
    }

    private static int nextId = 1;

    private static Entry Report(DateOnly date, int? slot, decimal amount, bool usePool = true,
        EntryInputMode mode = EntryInputMode.Actual, int category = Food, DateOnly? reportedOn = null) => new()
    {
        Id = nextId++,
        Date = date,
        CategoryId = category,
        SlotId = slot,
        InputMode = mode,
        InputAmount = amount,
        UsePool = usePool,
        // 台北中午 = UTC 04:00，換算回邏輯日一定是同一天
        CreatedAt = At(reportedOn ?? date).AddSeconds(nextId),
    };

    private static DateTime At(DateOnly d, int utcHour = 4) => d.ToDateTime(new TimeOnly(utcHour, 0), DateTimeKind.Utc);

    private static Models.Dtos.PeriodView Run(BudgetPeriod p, PeriodPlan plan, DateOnly today) =>
        BudgetEngine.Compute(p, plan, today, utc => DateOnly.FromDateTime(utc.AddHours(8 - 4)), new Dictionary<DateOnly, DayInfo>());

    [Fact]
    public void Unreported_slots_are_treated_as_spent_exactly_as_planned()
    {
        var (p, plan) = Make();
        var view = Run(p, plan, new DateOnly(2026, 10, 4));

        var food = view.Categories.Single(c => c.CategoryId == Food);
        Assert.Equal(4 * 300, food.Spent);
        Assert.Equal(6 * 300, food.PlannedRemaining);
        Assert.All(view.Days, d => Assert.Equal(0, d.Net));
        Assert.Equal(6000, view.Pool.Balance);
    }

    [Fact]
    public void Spending_less_than_planned_moves_the_difference_into_the_pool()
    {
        var (p, plan) = Make();
        p.Entries.Add(Report(Start, Breakfast, 60));

        var view = Run(p, plan, Start);

        Assert.Equal(6000 + 40, view.Pool.Balance);
        Assert.Equal(40, view.Days[0].Net);
    }

    [Fact]
    public void Overspend_is_covered_by_the_pool_first_when_allowed()
    {
        var (p, plan) = Make();
        p.Entries.Add(Report(Start, Breakfast, 130));

        var view = Run(p, plan, Start);

        Assert.Equal(6000 - 30, view.Pool.Balance);
        Assert.All(view.Days.Skip(1), d => Assert.Equal(d.BasePlanned, d.Planned));
    }

    [Fact]
    public void Overspend_without_pool_is_spread_over_later_unreported_slots()
    {
        var (p, plan) = Make();
        p.Entries.Add(Report(Start, Lunch, 470, usePool: false)); // 超支 270

        var view = Run(p, plan, Start);

        Assert.Equal(6000, view.Pool.Balance);
        Assert.Equal(270, view.Days.Skip(1).Sum(d => d.BasePlanned - d.Planned));
        Assert.Equal(270, view.Entries.Single().Spread);
    }

    [Fact]
    public void Overspend_on_the_last_day_cannot_be_spread_and_drives_the_pool_negative()
    {
        var (p, plan) = Make(foodBudget: 3000, funBudget: 7000);
        p.Entries.Add(Report(End, Lunch, 500, usePool: false));

        var view = Run(p, plan, End);

        Assert.Equal(300, view.Entries.Single().Unabsorbed);
        Assert.Equal(-300, view.Pool.Balance);
    }

    [Fact]
    public void A_replacing_report_supersedes_the_old_one_and_the_old_one_is_kept()
    {
        var (p, plan) = Make();
        var first = Report(Start, Breakfast, 150);
        var second = Report(Start, Breakfast, 80);
        second.ReplacesId = first.Id;
        p.Entries.Add(first);
        p.Entries.Add(second);

        var view = Run(p, plan, Start);

        Assert.Equal(second.Id, view.Entries.Single().Id);
        Assert.Equal(6000 + 20, view.Pool.Balance);
        Assert.Equal(2, p.Entries.Count); // 舊的那筆還在（只新增，§2.2）
    }

    [Fact]
    public void A_void_record_removes_the_effect_of_the_report_it_points_to()
    {
        var (p, plan) = Make();
        var e = Report(Start, Lunch, 470, usePool: false);
        p.Entries.Add(e);
        p.Entries.Add(new Entry { Id = nextId++, Date = Start, CategoryId = Food, SlotId = Lunch, IsVoid = true, ReplacesId = e.Id, CreatedAt = At(Start, 5) });

        var view = Run(p, plan, Start);

        Assert.Empty(view.Entries);
        Assert.All(view.Days, d => Assert.Equal(d.BasePlanned, d.Planned));
        Assert.Equal(6000, view.Pool.Balance);
    }

    [Fact]
    public void Lowering_today_applies_only_to_slots_not_reported_before_the_change()
    {
        var changedAt = At(Start, 6);
        var (p, plan) = Make(lowerings:
        [
            new TodayLowering(Start, Food, Breakfast, "早餐", 60, changedAt),
            new TodayLowering(Start, Food, Lunch, "午餐", 150, changedAt),
        ]);
        p.Entries.Add(Report(Start, Breakfast, 100)); // 改設定之前已經回報早餐（剛好）

        var view = Run(p, plan, Start);

        var today = view.Days[0];
        Assert.Equal(100, today.Slots.Single(s => s.SlotId == Breakfast).Planned); // 已回報的不動
        Assert.Equal(150, today.Slots.Single(s => s.SlotId == Lunch).Planned);     // 還沒回報的取較低值
        Assert.Contains(view.Pool.Lines, l => l.Kind == "Lower" && l.Amount == 50);
        Assert.Equal(6000 + 50, view.Pool.Balance);
    }

    [Fact]
    public void Envelope_overspend_draws_the_excess_from_the_pool()
    {
        var (p, plan) = Make(funBudget: 1000);
        p.Entries.Add(Report(Start, null, 800, category: Fun));
        p.Entries.Add(Report(Start, null, 500, category: Fun));

        var view = Run(p, plan, Start);

        Assert.Equal(1300, view.Categories.Single(c => c.CategoryId == Fun).Spent);
        Assert.Equal(6000 - 300, view.Pool.Balance);
    }

    [Theory]
    [InlineData(10, new[] { 1, 1, 1 }, new[] { 1, 1, 1 })]
    [InlineData(2, new[] { 100, 100, 100 }, new[] { 1, 1, 0 })]
    [InlineData(100, new[] { 50, 150 }, new[] { 25, 75 })]
    [InlineData(7, new[] { 0, 10 }, new[] { 0, 7 })]
    public void Distribute_is_proportional_and_never_exceeds_a_weight(int amount, int[] weights, int[] expected)
    {
        var result = BudgetEngine.Distribute(amount, weights.Select(w => (decimal)w).ToList());
        Assert.Equal(expected.Select(x => (decimal)x), result);
    }
}
