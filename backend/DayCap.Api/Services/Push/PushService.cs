using System.Globalization;
using System.Net;
using System.Text.Json;
using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using WebPush;

namespace DayCap.Api.Services.Push;

public record PushMessage(string Title, string Body, string Url);

public interface IPushService
{
    Task<string> PublicKeyAsync(CancellationToken ct = default);
    Task SubscribeAsync(string userId, PushSubscribeRequest req, CancellationToken ct = default);
    Task UnsubscribeAsync(string userId, string endpoint, CancellationToken ct = default);
    Task<PushSettingsDto> GetSettingsAsync(string userId, CancellationToken ct = default);
    Task<PushSettingsDto> SaveSettingsAsync(string userId, PushSettingsDto req, CancellationToken ct = default);
    /// <summary>立刻發一則「今晚的內容」給這個使用者（設定頁的測試按鈕）。回傳成功送達的裝置數。</summary>
    Task<int> SendNowAsync(string userId, CancellationToken ct = default);
    /// <summary>排程器每分鐘呼叫：到時間、今天還沒發過的人各發一則。</summary>
    Task<int> SendDueAsync(CancellationToken ct = default);
}

/// <summary>
/// 每晚通知（§9.4）：一天最多一則，點開就是今天的卡片疊。超支、發薪等當天的站內通知併進同一則，不另外推。
/// VAPID 金鑰第一次用到時產生並存在資料庫，不需要額外設定。
/// </summary>
public class PushService(DayCapDbContext db, IPeriodService periods, IAppClock clock, ILogger<PushService> log) : IPushService
{
    private const string PublicKeyName = "vapid-public";
    private const string PrivateKeyName = "vapid-private";
    private const string Subject = "https://daycap.matthewyu.uk";

    public async Task<string> PublicKeyAsync(CancellationToken ct = default) => (await KeysAsync(ct)).Public;

    public async Task SubscribeAsync(string userId, PushSubscribeRequest req, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ValidationException("推播訂閱的網址不正確。");
        if (string.IsNullOrWhiteSpace(req.Keys?.P256dh) || string.IsNullOrWhiteSpace(req.Keys?.Auth))
            throw new ValidationException("推播訂閱缺少金鑰。");

        var existing = await db.PushEndpoints.FirstOrDefaultAsync(p => p.Endpoint == req.Endpoint, ct);
        if (existing is null)
        {
            db.PushEndpoints.Add(new PushEndpoint
            {
                UserId = userId, Endpoint = req.Endpoint, P256dh = req.Keys.P256dh, Auth = req.Keys.Auth, CreatedAt = clock.UtcNow,
            });
        }
        else
        {
            // 同一台裝置換了登入的人：訂閱跟著現在的人
            existing.UserId = userId;
            existing.P256dh = req.Keys.P256dh;
            existing.Auth = req.Keys.Auth;
            existing.FailCount = 0;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UnsubscribeAsync(string userId, string endpoint, CancellationToken ct = default) =>
        await db.PushEndpoints.Where(p => p.UserId == userId && p.Endpoint == endpoint).ExecuteDeleteAsync(ct);

    public async Task<PushSettingsDto> GetSettingsAsync(string userId, CancellationToken ct = default)
    {
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        var devices = await db.PushEndpoints.CountAsync(p => p.UserId == userId, ct);
        return new PushSettingsDto(profile?.PushEnabled ?? true, profile?.PushTime ?? "21:30", devices);
    }

    public async Task<PushSettingsDto> SaveSettingsAsync(string userId, PushSettingsDto req, CancellationToken ct = default)
    {
        if (!TimeOnly.TryParseExact(req.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ValidationException("時間格式要是 HH:mm。");
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct) ?? throw new NotFoundException("找不到使用者設定。");
        profile.PushEnabled = req.Enabled;
        profile.PushTime = req.Time;
        await db.SaveChangesAsync(ct);
        return await GetSettingsAsync(userId, ct);
    }

    public async Task<int> SendNowAsync(string userId, CancellationToken ct = default)
    {
        var message = await BuildAsync(userId, ct) ?? new PushMessage("日額", "通知測試：收到這則就代表設定好了。", "/");
        return await SendAsync(userId, message, ct);
    }

    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var local = TaipeiClock.ToLocal(clock.UtcNow);
        var today = DateOnly.FromDateTime(local);
        var candidates = await db.Profiles
            .Where(p => p.PushEnabled && (p.LastPushOn == null || p.LastPushOn < today))
            .Where(p => db.PushEndpoints.Any(e => e.UserId == p.UserId))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var profile in candidates.Where(p => NightlyPush.IsDue(local, p.PushTime, p.LastPushOn)))
        {
            // 先記下「今天發過了」再發：寧可漏一則，也不要重複發（§9.4 一天最多一則）
            profile.LastPushOn = today;
            await db.SaveChangesAsync(ct);
            try
            {
                var message = await BuildAsync(profile.UserId, ct);
                if (message is not null) sent += await SendAsync(profile.UserId, message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Nightly push failed for {User}", profile.UserId);
            }
        }
        return sent;
    }

    private async Task<PushMessage?> BuildAsync(string userId, CancellationToken ct)
    {
        PeriodView view;
        try
        {
            view = await periods.GetCurrentAsync(userId, ct);
        }
        catch (NotStartedException)
        {
            return null;
        }
        var unread = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);
        return NightlyPush.Build(view, unread);
    }

    private async Task<int> SendAsync(string userId, PushMessage message, CancellationToken ct)
    {
        var keys = await KeysAsync(ct);
        var vapid = new VapidDetails(Subject, keys.Public, keys.Private);
        var payload = JsonSerializer.Serialize(new { title = message.Title, body = message.Body, url = message.Url });
        var client = new WebPushClient();
        var ok = 0;
        foreach (var ep in await db.PushEndpoints.Where(p => p.UserId == userId).ToListAsync(ct))
        {
            try
            {
                await client.SendNotificationAsync(new PushSubscription(ep.Endpoint, ep.P256dh, ep.Auth), payload, vapid, ct);
                ep.LastSuccessAt = clock.UtcNow;
                ep.FailCount = 0;
                ok++;
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                db.PushEndpoints.Remove(ep); // 裝置取消了訂閱
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ep.FailCount++;
                if (ep.FailCount >= 10) db.PushEndpoints.Remove(ep);
                log.LogWarning(ex, "Push to one device failed ({Count})", ep.FailCount);
            }
        }
        await db.SaveChangesAsync(ct);
        return ok;
    }

    private async Task<(string Public, string Private)> KeysAsync(CancellationToken ct)
    {
        var pub = await db.AppSecrets.FindAsync([PublicKeyName], ct);
        var priv = await db.AppSecrets.FindAsync([PrivateKeyName], ct);
        if (pub is not null && priv is not null) return (pub.Value, priv.Value);

        var keys = VapidHelper.GenerateVapidKeys();
        db.AppSecrets.Add(new AppSecret { Key = PublicKeyName, Value = keys.PublicKey });
        db.AppSecrets.Add(new AppSecret { Key = PrivateKeyName, Value = keys.PrivateKey });
        await db.SaveChangesAsync(ct);
        return (keys.PublicKey, keys.PrivateKey);
    }
}

/// <summary>每晚那一則的內容與時機（純函式，好測）。</summary>
public static class NightlyPush
{
    public static bool IsDue(DateTime localNow, string pushTime, DateOnly? lastPushOn)
    {
        if (!TimeOnly.TryParseExact(pushTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return false;
        var today = DateOnly.FromDateTime(localNow);
        return (lastPushOn is null || lastPushOn < today) && TimeOnly.FromDateTime(localNow) >= at;
    }

    public static PushMessage Build(PeriodView view, int unreadNotifications)
    {
        var today = view.Days.FirstOrDefault(d => d.Status == "today");
        var parts = new List<string>();
        if (today is not null)
        {
            var open = today.Slots.Where(s => s.Actual is null && s.Planned > 0).ToList();
            if (open.Count > 0)
                parts.Add($"還有 {open.Count} 個時段沒確認（{string.Join("、", open.Select(s => s.Name))}），滑一下就好。");
            else
                parts.Add(today.Net switch
                {
                    > 0 => $"今天都確認了，省下 {today.Net:N0}。",
                    < 0 => $"今天都確認了，超支 {-today.Net:N0}，會從之後的日子慢慢攤回。",
                    _ => "今天都確認了，剛好照預算。",
                });
        }
        if (unreadNotifications > 0) parts.Add($"另外有 {unreadNotifications} 則通知。");
        return new PushMessage("今晚確認一下", string.Join("", parts), "/");
    }
}
