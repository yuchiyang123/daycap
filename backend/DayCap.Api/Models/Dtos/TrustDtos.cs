namespace DayCap.Api.Models.Dtos;

/// <summary>登入中的裝置（§21.3）。Current = 現在這個瀏覽器。</summary>
public record SessionView(int Id, string Device, DateTime FirstSeenAt, DateTime LastSeenAt, bool Revoked, bool Current);

/// <summary>修改紀錄（§21.2）：Action = 新增 / 修改 / 刪除 / 新版本 / 更正；ReplacesId = 取代了哪一筆。</summary>
public record HistoryItem(DateTime At, string Kind, string Action, string Summary, int Id, int? ReplacesId);

public record DeleteAccountRequest(string Confirm);
