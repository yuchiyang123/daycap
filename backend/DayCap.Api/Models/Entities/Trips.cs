namespace DayCap.Api.Models.Entities;

/// <summary>
/// 旅遊（§17，事實）：這段日子的每日時段暫停（額度回待分配池），改用旅遊預算（一個預約罐子）付。
/// 幣別與匯率是回報時的預設值（匯率由使用者自己填，採用回報當下的值）。
/// 提早結束＝新增一筆取代它、結束日改早的紀錄。
/// </summary>
public class Trip : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal Budget { get; set; }
    public string? Currency { get; set; }
    public decimal? FxRate { get; set; }

    /// <summary>旅遊預算的罐子（預約支出）。</summary>
    public int JarId { get; set; }

    /// <summary>這天（邏輯日）起暫停的時段額度才回到池子；建立前的日子不回溯。</summary>
    public DateOnly CreatedOn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}
