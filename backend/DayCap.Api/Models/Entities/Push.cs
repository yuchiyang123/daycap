namespace DayCap.Api.Models.Entities;

/// <summary>一台裝置的 Web Push 訂閱（§9.4）。同一個使用者可以有多台。</summary>
public class PushEndpoint
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSuccessAt { get; set; }
    public int FailCount { get; set; }
}

/// <summary>伺服器自己產生、要跨重啟保留的值（例如 Web Push 的 VAPID 金鑰）。</summary>
public class AppSecret
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
