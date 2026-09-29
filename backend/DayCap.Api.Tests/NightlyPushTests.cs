using DayCap.Api.Models.Dtos;
using DayCap.Api.Services.Push;

namespace DayCap.Api.Tests;

/// <summary>§9.4 每晚通知：一天最多一則、到時間才發、內容。</summary>
public class NightlyPushTests
{
    [Theory]
    [InlineData("2026-10-07 21:29", "21:30", null, false)]
    [InlineData("2026-10-07 21:30", "21:30", null, true)]
    [InlineData("2026-10-07 23:00", "21:30", "2026-10-07", false)] // 今天發過了
    [InlineData("2026-10-07 23:00", "21:30", "2026-10-06", true)]
    [InlineData("2026-10-07 23:00", "bad", null, false)]
    public void Due_once_a_day_after_the_chosen_time(string now, string time, string? last, bool due)
    {
        var lastOn = last is null ? (DateOnly?)null : DateOnly.Parse(last);
        Assert.Equal(due, NightlyPush.IsDue(DateTime.Parse(now), time, lastOn));
    }

    private static PeriodView View(params SlotView[] slots)
    {
        var day = new DayView(new DateOnly(2026, 10, 7), false, null, "today", 300, 300, slots.Sum(s => s.Actual is null ? 0 : s.Planned - s.Actual.Value), [.. slots], []);
        return new PeriodView(1, day.Date, day.Date, day.Date, "04:00", 1, 0, 0, 0, [], true, null,
            new PoolView(0, 0, []), [], [day], [], [], [], [], []);
    }

    [Fact]
    public void Lists_unconfirmed_slots_and_bundles_other_notifications()
    {
        var msg = NightlyPush.Build(View(
            new SlotView(1, 1, "午餐", 120, 120, 120, 5),
            new SlotView(1, 2, "晚餐", 150, 150, null, null)), unreadNotifications: 2);

        Assert.Contains("1 個時段沒確認", msg.Body);
        Assert.Contains("晚餐", msg.Body);
        Assert.DoesNotContain("午餐", msg.Body);
        Assert.Contains("2 則通知", msg.Body);
        Assert.Equal("/", msg.Url);
    }

    [Fact]
    public void All_confirmed_reports_the_day_result()
    {
        var msg = NightlyPush.Build(View(new SlotView(1, 1, "午餐", 120, 120, 100, 5)), 0);
        Assert.Contains("省下 20", msg.Body);
    }
}
