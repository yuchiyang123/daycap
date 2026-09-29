namespace DayCap.Api.Models.Entities;

/// <summary>應收（我代墊，資產）/ 應付（別人先幫我付，負債）（§14）。</summary>
public enum DebtKind { Receivable, Payable }

/// <summary>
/// 一筆應收或應付（事實）。第一版對象只是名字。
/// 應收：多付的錢從 AccountId 出去，不是花費、不影響預算；應付：別人付的，我的份照扣預算，錢還沒從我的帳戶出去。
/// </summary>
public class Debt : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DebtKind Kind { get; set; }
    public string Counterparty { get; set; } = "";
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>應收：代墊的錢從哪個帳戶出去（選填）。應付：null。</summary>
    public int? AccountId { get; set; }

    /// <summary>從哪筆回報分帳出來的（刪回報時一起作廢）。手動記的借貸為 null。</summary>
    public int? SourceEntryId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}

/// <summary>收回應收 / 還掉應付（事實）。應收：錢進 AccountId；應付：錢從 AccountId 出去。預算不動。</summary>
public class DebtSettlement : IFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int DebtId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
    public int? AccountId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ReplacesId { get; set; }
    public bool IsVoid { get; set; }
}
