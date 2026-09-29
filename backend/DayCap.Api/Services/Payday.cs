using DayCap.Api.Models.Settings;

namespace DayCap.Api.Services;

/// <summary>
/// 發薪日規則（§3.4）：每月 Day 號（超過當月天數就是月底）；那天不是工作日時依 Shift
/// 往前或往後找最近的工作日，None 就不調整。工作日＝行事曆上不是假日的日子（補班日算工作日），
/// 跟餐費平日／假日用同一份行事曆（§3.5）。
/// </summary>
public static class Payday
{
    /// <summary>某年某月的預定發薪日。isHoliday 要能回答前後一兩週的日子。</summary>
    public static DateOnly Scheduled(int year, int month, PaydayRule rule, Func<DateOnly, bool> isHoliday)
    {
        var d = new DateOnly(year, month, Math.Min(rule.Day, DateTime.DaysInMonth(year, month)));
        if (rule.Shift == HolidayShift.None) return d;
        var step = rule.Shift == HolidayShift.Before ? -1 : 1;
        for (var guard = 0; guard < 40 && isHoliday(d); guard++) d = d.AddDays(step);
        return d;
    }

    /// <summary>date 之後（不含當天）的第一個預定發薪日。</summary>
    public static DateOnly NextAfter(DateOnly date, PaydayRule rule, Func<DateOnly, bool> isHoliday)
    {
        var month = new DateOnly(date.Year, date.Month, 1).AddMonths(-1); // 往前推可能落到上個月底，從上個月開始看
        for (var i = 0; i < 4; i++, month = month.AddMonths(1))
        {
            var p = Scheduled(month.Year, month.Month, rule, isHoliday);
            if (p > date) return p;
        }
        throw new InvalidOperationException("找不到下一個發薪日。");
    }

    /// <summary>date 當天或之前最近的預定發薪日。</summary>
    public static DateOnly LatestOnOrBefore(DateOnly date, PaydayRule rule, Func<DateOnly, bool> isHoliday)
    {
        var month = new DateOnly(date.Year, date.Month, 1).AddMonths(1); // 往後推可能落到下個月初
        for (var i = 0; i < 4; i++, month = month.AddMonths(-1))
        {
            var p = Scheduled(month.Year, month.Month, rule, isHoliday);
            if (p <= date) return p;
        }
        throw new InvalidOperationException("找不到上一個發薪日。");
    }
}
