namespace DayCap.Api.Models.Entities;

/// <summary>罐子的種類（§11.2）：預約支出、年繳預留款、儲蓄目標都是「有目標金額（與到期日）的罐子」。</summary>
public enum JarKind { Reservation, Annual, Goal }

/// <summary>
/// 罐子（容器，和帳戶一樣可以改名、改目標、關閉；錢的進出是事實）。
/// 餘額不存：＝ 存進去的待分配池調整（<see cref="PoolTransfer.JarId"/>，金額為負）取反 − 從罐子付的回報（<see cref="Entry.JarCovered"/>）。
/// </summary>
public class Jar
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public JarKind Kind { get; set; }
    public string Name { get; set; } = "";
    public decimal TargetAmount { get; set; }

    /// <summary>預約：哪天要付；年繳：下次繳費日；目標：希望哪天存到（可空）。</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>年繳每期提撥多少（預設目標 ÷ 12，無條件進位）。</summary>
    public decimal? MonthlyAmount { get; set; }

    /// <summary>自動規則（§11.2 第 3 點）：時段省下的錢，自動存進來幾 %。</summary>
    public decimal? AutoSurplusPercent { get; set; }

    /// <summary>由哪個年繳固定支出轉過來（轉的時候那個固定支出從明天起停用）。</summary>
    public int? FixedItemId { get; set; }

    public DateTime? ClosedAt { get; set; }

    /// <summary>預約付掉之後自動關閉：記下是哪筆回報，刪掉回報時重新打開。</summary>
    public int? ClosedByEntryId { get; set; }

    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
