using System.Security.Cryptography;
using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface ISessionService
{
    /// <summary>JWT 驗證過之後呼叫：這台裝置被撤銷、或這個 token 在「登出其他所有裝置」之前發的，回傳 false。</summary>
    Task<bool> CheckAsync(string userId, string deviceId, DateTime? issuedAtUtc, string? userAgent, CancellationToken ct = default);
    Task<List<SessionView>> ListAsync(string userId, string? deviceId, CancellationToken ct = default);
    Task<List<SessionView>> RevokeAsync(string userId, int sessionId, string? deviceId, CancellationToken ct = default);
    Task<List<SessionView>> RevokeOthersAsync(string userId, string? deviceId, CancellationToken ct = default);
}

/// <summary>登入中的裝置與撤銷（§21.3）。</summary>
public class SessionService(DayCapDbContext db, IAppClock clock) : ISessionService
{
    public const string DeviceCookie = "daycap_device";
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    public static string NewDeviceId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public async Task<bool> CheckAsync(string userId, string deviceId, DateTime? issuedAtUtc, string? userAgent, CancellationToken ct = default)
    {
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct);
        if (session is { RevokedAt: not null }) return false;

        if (session is not { KeepAfterCutoff: true })
        {
            var cutoff = await db.Profiles.Where(p => p.UserId == userId).Select(p => p.TokensValidAfter).FirstOrDefaultAsync(ct);
            if (cutoff is { } c && (session is not null && session.FirstSeenAt < c || issuedAtUtc is null || issuedAtUtc < c)) return false;
        }

        var now = clock.UtcNow;
        if (session is null)
        {
            db.UserSessions.Add(new UserSession
            {
                UserId = userId, DeviceId = deviceId, UserAgent = Trim(userAgent), FirstSeenAt = now, LastSeenAt = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // 同一台裝置兩個請求同時進來：另一個已經記下了
            }
        }
        else if (session.UserId != userId)
        {
            // 同一台裝置換了登入的人：舊的那筆留給原本的人，這台改記在新的人名下
            session.UserId = userId;
            session.FirstSeenAt = now;
            session.LastSeenAt = now;
            session.KeepAfterCutoff = false;
            await db.SaveChangesAsync(ct);
        }
        else if (now - session.LastSeenAt > TouchEvery)
        {
            session.LastSeenAt = now;
            session.UserAgent = Trim(userAgent) ?? session.UserAgent;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public async Task<List<SessionView>> ListAsync(string userId, string? deviceId, CancellationToken ct = default)
    {
        var cutoff = await db.Profiles.Where(p => p.UserId == userId).Select(p => p.TokensValidAfter).FirstOrDefaultAsync(ct);
        var sessions = await db.UserSessions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
        return sessions
            .Select(s => new SessionView(s.Id, Describe(s.UserAgent), s.FirstSeenAt, s.LastSeenAt,
                s.RevokedAt is not null || (cutoff is { } c && !s.KeepAfterCutoff && s.FirstSeenAt < c && s.DeviceId != deviceId),
                s.DeviceId == deviceId))
            .OrderByDescending(s => s.Current).ThenBy(s => s.Revoked).ThenByDescending(s => s.LastSeenAt)
            .Take(50)
            .ToList();
    }

    public async Task<List<SessionView>> RevokeAsync(string userId, int sessionId, string? deviceId, CancellationToken ct = default)
    {
        var s = await db.UserSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, ct) ?? throw new NotFoundException("找不到這個裝置。");
        if (s.DeviceId == deviceId) throw new ValidationException("這是現在這台，要登出請用「登出」。");
        s.RevokedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, deviceId, ct);
    }

    public async Task<List<SessionView>> RevokeOthersAsync(string userId, string? deviceId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        foreach (var s in await db.UserSessions.Where(x => x.UserId == userId).ToListAsync(ct))
        {
            if (s.DeviceId == deviceId) s.KeepAfterCutoff = true;
            else s.RevokedAt ??= now;
        }
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is not null) profile.TokensValidAfter = now;
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, deviceId, ct);
    }

    private static string? Trim(string? ua) => string.IsNullOrEmpty(ua) ? null : ua[..Math.Min(ua.Length, 300)];

    /// <summary>把 User-Agent 變成看得懂的裝置名稱（大概就好）。</summary>
    public static string Describe(string? ua)
    {
        if (string.IsNullOrEmpty(ua)) return "不明裝置";
        var device = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android"
            : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS X") ? "Mac" : ua.Contains("Linux") ? "Linux" : "其他";
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("CriOS") || ua.Contains("Chrome/") ? "Chrome"
            : ua.Contains("Firefox/") ? "Firefox" : ua.Contains("Safari/") ? "Safari" : "瀏覽器";
        return $"{device}・{browser}";
    }
}
