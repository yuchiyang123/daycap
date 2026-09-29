namespace DayCap.Api.Models.Entities;

/// <summary>
/// 登入中的裝置（§21.3）。用 DayCap 自己發的裝置 cookie（隨機值）辨認，而不是 token：
/// Mini-SSO 的 access token 會一直被 refresh 換新，只看 token 的話撤銷不住。
/// 撤銷後這台裝置的請求一律 401，前端會自己呼叫 Mini-SSO 登出（連 refresh token 一起撤銷）。
/// </summary>
public class UserSession
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string? UserAgent { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>「登出其他所有裝置」時留下的那一台：不受 <see cref="UserProfile.TokensValidAfter"/> 影響。</summary>
    public bool KeepAfterCutoff { get; set; }
}
