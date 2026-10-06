namespace DayCap.Api.Models.Entities;

/// <summary>
/// 定期定額買進（事實）：扣款日（遇到休市就是下一個交易日）用當天收盤價買整數股，
/// 錢從 FromAccountId 出去，持股的股數與平均成本同步更新。手續費未計（各家券商不同）。
/// </summary>
public class HoldingPurchase : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int HoldingId { get; set; }
    public int FixedItemId { get; set; }
    public DateOnly DueDate { get; set; }
    public DateOnly TradeDate { get; set; }
    public decimal Budget { get; set; }
    public decimal Price { get; set; }
    public decimal Shares { get; set; }
    public decimal Spent { get; set; }
    public int FromAccountId { get; set; }

    /// <summary>「dca:{項目Id}:{扣款日}」，同一期只會買一次。</summary>
    public string AutoKey { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}
