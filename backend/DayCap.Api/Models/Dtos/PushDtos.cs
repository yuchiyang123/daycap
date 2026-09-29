namespace DayCap.Api.Models.Dtos;

/// <summary>瀏覽器 PushSubscription.toJSON() 的格式。</summary>
public record PushSubscribeRequest(string Endpoint, PushKeys Keys);

public record PushKeys(string P256dh, string Auth);

public record PushUnsubscribeRequest(string Endpoint);

/// <summary>每晚通知的設定（§9.4）。Devices = 已經訂閱的裝置數（唯讀）。</summary>
public record PushSettingsDto(bool Enabled, string Time, int Devices = 0);
