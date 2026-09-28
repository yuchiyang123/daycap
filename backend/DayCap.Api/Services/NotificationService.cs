using System.Text.Json;
using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public record NotificationDto(
    int Id,
    string Kind,
    string Title,
    List<string> Lines,
    // warn = 有超支風險 / 已扣資產，ok = 在預算內
    string Tone,
    DateTime CreatedAt,
    bool Read,
    bool ShowPopup);

public record NotificationsView(int Unread, List<NotificationDto> Items);

public interface INotificationService
{
    Task<NotificationsView> GetAsync(string userId, CancellationToken ct = default);
    Task MarkPopupShownAsync(string userId, int id, CancellationToken ct = default);
    Task MarkAllReadAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// 鈴鐺通知。目前兩種：
/// - reminder：發薪日前 5 天（= 週期最後一天往前 4 天）起產生一筆，內容每次打開都用最新數字算；
///   只有那一天第一次打開 app 會跳出來。
/// - settlement：週期結算時產生（見 PeriodService），結算當天跳出來。
/// </summary>
public class NotificationService(
    DayCapDbContext db,
    IPeriodService periods,
    ISettingsService settings,
    IAppClock clock) : INotificationService
{
    public const int ReminderDaysBeforePayday = 5;

    public async Task<NotificationsView> GetAsync(string userId, CancellationToken ct = default)
    {
        var today = clock.Today;
        PeriodView? current = null;
        try
        {
            current = await periods.GetCurrentAsync(userId, ct); // 也會順便跑結算
        }
        catch (NotStartedException)
        {
            // 還沒開始就沒有提醒
        }

        if (current is not null)
        {
            var payday = current.EndDate.AddDays(1);
            var remindOn = payday.AddDays(-ReminderDaysBeforePayday);
            var key = $"reminder:{current.Id}";
            if (today >= remindOn && today <= current.EndDate && remindOn > current.StartDate
                && !await db.Notifications.AnyAsync(n => n.UserId == userId && n.Key == key, ct))
            {
                db.Notifications.Add(new Notification
                {
                    UserId = userId,
                    Key = key,
                    Kind = "reminder",
                    PeriodId = current.Id,
                    PopupOn = remindOn,
                    CreatedAt = clock.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
        }

        var profile = await settings.EnsureProfileAsync(userId, ct);
        var accountName = profile.SettlementAccountId is { } aid
            ? await db.CashAccounts.Where(c => c.Id == aid && c.UserId == userId).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;

        var rows = await db.Notifications.Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(30)
            .ToListAsync(ct);

        var items = rows.Select(n =>
        {
            var (title, lines, tone) = n.Kind switch
            {
                "reminder" when current is not null && n.PeriodId == current.Id => Reminder(current, accountName, today),
                "reminder" => ("發薪日前提醒", new List<string> { "這一期已經結束了。" }, "ok"),
                "settlement" => Settlement(n.Payload),
                _ => ("通知", new List<string>(), "ok"),
            };
            var showPopup = n.PopupOn == today && n.PopupShownAt is null;
            return new NotificationDto(n.Id, n.Kind, title, lines, tone, n.CreatedAt, n.ReadAt is not null, showPopup);
        }).ToList();

        return new NotificationsView(items.Count(i => !i.Read), items);
    }

    public async Task MarkPopupShownAsync(string userId, int id, CancellationToken ct = default)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct)
                ?? throw new NotFoundException("找不到這則通知。");
        n.PopupShownAt ??= clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAllReadAsync(string userId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        foreach (var n in await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync(ct))
        {
            n.ReadAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>用本期最新的數字組提醒內容：會不會超、超多少、發薪日後會怎樣、要怎麼救。</summary>
    public static (string Title, List<string> Lines, string Tone) Reminder(PeriodView p, string? accountName, DateOnly today)
    {
        var payday = p.EndDate.AddDays(1);
        var daysLeft = Math.Max(0, p.EndDate.DayNumber - today.DayNumber + 1);
        var pool = p.Pool.Balance;
        var over = p.Categories.Where(c => c.Mode != BudgetMode.Fixed && c.Projected > c.Budget).ToList();
        var lines = new List<string> { $"發薪日是 {payday:M/d}，本期還剩 {daysLeft} 天。" };

        if (pool < 0)
        {
            lines.Add($"照目前的花費和排程，本期會超支 {-pool:N0}。");
            lines.Add(accountName is null
                ? "發薪日後會自動從資產扣除——但還沒設定結算帳戶，請到設定選一個。"
                : $"發薪日後會自動從「{accountName}」扣除 {-pool:N0}。");
            if (daysLeft > 0)
                lines.Add($"想保住資產：接下來每天少花約 {(int)Math.Ceiling(-pool / (decimal)daysLeft):N0}，或到總覽把其他分類的錢挪過來。");
        }
        else if (over.Count > 0)
        {
            lines.Add($"有分類會超過額度，但待定區還有 {pool:N0} 可以補，目前不會動到資產。");
        }
        else
        {
            lines.Add($"目前都在預算內，待定區還有 {pool:N0}。");
        }

        foreach (var c in over)
        {
            lines.Add($"{c.Name}：預計 {c.Projected:N0} / 額度 {c.Budget:N0}（超 {c.Projected - c.Budget:N0}）");
        }

        var tone = pool < 0 || over.Count > 0 ? "warn" : "ok";
        return (pool < 0 ? "發薪日前提醒：本期可能超支" : "發薪日前提醒", lines, tone);
    }

    private static (string Title, List<string> Lines, string Tone) Settlement(string? payload)
    {
        var s = payload is null ? null : JsonSerializer.Deserialize<SettlementPayload>(payload);
        if (s is null) return ("結算", [], "ok");

        var lines = new List<string>();
        if (s.Balance < 0)
        {
            lines.Add(s.Applied < 0
                ? $"本期超支 {-s.Balance:N0}，已從「{s.AccountName}」扣除。"
                : $"本期超支 {-s.Balance:N0}。還沒設定結算帳戶，所以沒有自動扣除，請到設定選一個帳戶。");
        }
        else if (s.Balance > 0)
        {
            lines.Add(s.Applied > 0
                ? $"本期結餘 {s.Balance:N0}，已存入「{s.AccountName}」。"
                : $"本期結餘 {s.Balance:N0}，沒有自動存入（設定頁可以改成結餘自動存入帳戶）。");
        }
        else
        {
            lines.Add("本期剛好用完，資產沒有變動。");
        }
        return ($"{s.Label} 結算", lines, s.Balance < 0 ? "warn" : "ok");
    }
}
