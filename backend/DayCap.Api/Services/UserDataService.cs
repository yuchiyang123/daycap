using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IUserDataService
{
    /// <summary>全部資料（原始事實，含作廢與取代紀錄），JSON（§21.1）。</summary>
    Task<byte[]> ExportJsonAsync(string userId, CancellationToken ct = default);
    /// <summary>回報與額外花費（只有目前有效的），CSV，用 Excel 開得起來（UTF-8 BOM）。</summary>
    Task<byte[]> ExportEntriesCsvAsync(string userId, CancellationToken ct = default);
    /// <summary>刪除帳號：這個站上這個使用者的所有資料真的刪掉（§21.1）。</summary>
    Task DeleteAllAsync(string userId, string confirm, CancellationToken ct = default);
    /// <summary>修改紀錄（§21.2）：最近幾天新增 / 修改 / 刪除了什麼、取代哪一筆。</summary>
    Task<List<HistoryItem>> HistoryAsync(string userId, int days, CancellationToken ct = default);
}

public class UserDataService(DayCapDbContext db, IAppClock clock) : IUserDataService
{
    public const string DeletePhrase = "刪除我的資料";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<byte[]> ExportJsonAsync(string userId, CancellationToken ct = default)
    {
        var periodIds = await db.Periods.Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync(ct);
        var reconIds = await db.Reconciliations.Where(r => r.UserId == userId).Select(r => r.Id).ToListAsync(ct);
        var legacyCats = await db.Categories.AsNoTracking().Where(c => c.UserId == userId).Select(c => c.Id).ToListAsync(ct);
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);

        var data = new
        {
            exportedAt = clock.UtcNow,
            format = "DayCap export v1：全部是原始事實（含作廢與取代的紀錄），IsVoid / ReplacesId 說明它們的關係。",
            profile,
            settingsVersions = await db.SettingsVersions.AsNoTracking().Where(v => v.UserId == userId).OrderBy(v => v.CreatedAt).ToListAsync(ct),
            dayTypeOverrides = await db.DayTypeOverrides.AsNoTracking().Where(o => o.UserId == userId).ToListAsync(ct),
            periods = await db.Periods.AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.StartDate)
                .Select(p => new { p.Id, p.StartDate, p.EndDate, p.IncomeConfirmedAt, p.SettledAt, p.ClosedAt, p.CreatedAt }).ToListAsync(ct),
            entries = await db.Entries.AsNoTracking().Where(e => periodIds.Contains(e.PeriodId)).OrderBy(e => e.CreatedAt).ToListAsync(ct),
            poolTransfers = await db.PoolTransfers.AsNoTracking().Where(t => periodIds.Contains(t.PeriodId)).OrderBy(t => t.CreatedAt).ToListAsync(ct),
            incomeAdjustments = await db.IncomeAdjustments.AsNoTracking().Where(a => periodIds.Contains(a.PeriodId)).OrderBy(a => a.CreatedAt).ToListAsync(ct),
            periodCarryovers = await db.PeriodCarryovers.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct),
            monthEnds = await db.MonthEnds.AsNoTracking().Where(m => m.UserId == userId).ToListAsync(ct),
            accounts = await db.CashAccounts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct),
            reconciliations = await db.Reconciliations.AsNoTracking().Include(r => r.Lines).Where(r => r.UserId == userId).ToListAsync(ct),
            accountTransfers = await db.AccountTransfers.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(ct),
            assetAdjustments = await db.AssetAdjustments.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct),
            holdings = await db.Holdings.AsNoTracking().Where(h => h.UserId == userId).ToListAsync(ct),
            goals = await db.Goals.AsNoTracking().Where(g => g.UserId == userId).ToListAsync(ct),
            assetSnapshots = await db.AssetSnapshots.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct),
            jars = await db.Jars.AsNoTracking().Where(j => j.UserId == userId).ToListAsync(ct),
            debts = await db.Debts.AsNoTracking().Where(d => d.UserId == userId).ToListAsync(ct),
            debtSettlements = await db.DebtSettlements.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct),
            installments = await db.Installments.AsNoTracking().Where(i => i.UserId == userId).ToListAsync(ct),
            installmentPrepayments = await db.InstallmentPrepayments.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct),
            notifications = await db.Notifications.AsNoTracking().Where(n => n.UserId == userId).ToListAsync(ct),
            legacyCategories = await db.Categories.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct),
            legacySlots = await db.DailySlots.AsNoTracking().Where(s => legacyCats.Contains(s.CategoryId)).ToListAsync(ct),
            legacyFixedItems = await db.FixedItems.AsNoTracking().Where(f => legacyCats.Contains(f.CategoryId)).ToListAsync(ct),
            // 推播訂閱與登入裝置只列數量：訂閱金鑰、token 雜湊不適合放進會被轉寄的檔案
            pushDevices = await db.PushEndpoints.CountAsync(p => p.UserId == userId, ct),
            sessions = await db.UserSessions.CountAsync(s => s.UserId == userId, ct),
        };
        return JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
    }

    public async Task<byte[]> ExportEntriesCsvAsync(string userId, CancellationToken ct = default)
    {
        var periods = await db.Periods.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync(ct);
        var entries = (await db.Entries.AsNoTracking().Where(e => periods.Contains(e.PeriodId)).ToListAsync(ct)).Active()
            .OrderBy(e => e.Date).ThenBy(e => e.CreatedAt).ToList();
        var names = new Dictionary<int, string>();
        var slotNames = new Dictionary<int, string>();
        foreach (var v in await db.SettingsVersions.AsNoTracking().Where(v => v.UserId == userId).OrderBy(v => v.CreatedAt).ToListAsync(ct))
        {
            var doc = Models.Settings.SettingsJson.Deserialize(v.Document);
            foreach (var c in doc.Categories)
            {
                names[c.Id] = c.Name;
                foreach (var s in c.Slots) slotNames[s.Id] = s.Name;
            }
        }
        var accounts = await db.CashAccounts.AsNoTracking().Where(a => a.UserId == userId).ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        var sb = new StringBuilder();
        sb.AppendLine("日期,分類,時段,細項,輸入方式,金額,罐子付,付款帳戶,備註,登錄時間");
        foreach (var e in entries)
        {
            sb.AppendLine(string.Join(",",
                e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Csv(names.GetValueOrDefault(e.CategoryId, e.CategoryId.ToString(CultureInfo.InvariantCulture))),
                Csv(e.SlotId is { } s ? slotNames.GetValueOrDefault(s, "") : "額外花費"),
                Csv(e.SubItem),
                e.InputMode == EntryInputMode.Actual ? "實際" : "超支",
                e.InputAmount.ToString("0.##", CultureInfo.InvariantCulture),
                e.JarCovered.ToString("0.##", CultureInfo.InvariantCulture),
                Csv(e.AccountId is { } a ? accounts.GetValueOrDefault(a, "") : ""),
                Csv(e.Note),
                TaipeiClock.ToLocal(e.CreatedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // 防 CSV 公式注入：開頭是 = + - @ 的加一個單引號
        if (s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    public async Task DeleteAllAsync(string userId, string confirm, CancellationToken ct = default)
    {
        if (confirm?.Trim() != DeletePhrase) throw new ValidationException($"要輸入「{DeletePhrase}」才會刪除。");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var periodIds = db.Periods.Where(p => p.UserId == userId).Select(p => p.Id);
        var reconIds = db.Reconciliations.Where(r => r.UserId == userId).Select(r => r.Id);
        var legacyCats = db.Categories.Where(c => c.UserId == userId).Select(c => c.Id);

        await db.Entries.Where(e => periodIds.Contains(e.PeriodId)).ExecuteDeleteAsync(ct);
        await db.PoolTransfers.Where(t => periodIds.Contains(t.PeriodId)).ExecuteDeleteAsync(ct);
        await db.IncomeAdjustments.Where(a => periodIds.Contains(a.PeriodId)).ExecuteDeleteAsync(ct);
        await db.MonthEnds.Where(m => m.UserId == userId).ExecuteDeleteAsync(ct);
        await db.PeriodCarryovers.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ReconciliationLines.Where(l => reconIds.Contains(l.ReconciliationId)).ExecuteDeleteAsync(ct);
        await db.Reconciliations.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Periods.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.AccountTransfers.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.AssetAdjustments.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.CashAccounts.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Holdings.Where(h => h.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Goals.Where(g => g.UserId == userId).ExecuteDeleteAsync(ct);
        await db.AssetSnapshots.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Jars.Where(j => j.UserId == userId).ExecuteDeleteAsync(ct);
        await db.DebtSettlements.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Debts.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
        await db.InstallmentPrepayments.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Installments.Where(i => i.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(n => n.UserId == userId).ExecuteDeleteAsync(ct);
        await db.PushEndpoints.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.DayTypeOverrides.Where(o => o.UserId == userId).ExecuteDeleteAsync(ct);
        await db.SettingsVersions.Where(v => v.UserId == userId).ExecuteDeleteAsync(ct);
        await db.DailySlots.Where(s => legacyCats.Contains(s.CategoryId)).ExecuteDeleteAsync(ct);
        await db.FixedItems.Where(f => legacyCats.Contains(f.CategoryId)).ExecuteDeleteAsync(ct);
        await db.Categories.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Profiles.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        // 刪掉的頁面空間也清掉，資料不留在資料庫檔案的空白頁裡
        await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
    }

    public async Task<List<HistoryItem>> HistoryAsync(string userId, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 366);
        var since = clock.UtcNow.AddDays(-days);
        var periodIds = db.Periods.Where(p => p.UserId == userId).Select(p => p.Id);
        var items = new List<HistoryItem>();

        static string Action(IFact f) => f.IsVoid ? "刪除" : f.ReplacesId is not null ? "修改" : "新增";
        void Add(IEnumerable<IFact> facts, string kind, Func<IFact, string> describe, Func<IFact, DateTime> at)
        {
            foreach (var f in facts) items.Add(new HistoryItem(at(f), kind, Action(f), describe(f), IdOf(f), f.ReplacesId));
        }

        Add(await db.Entries.AsNoTracking().Where(e => periodIds.Contains(e.PeriodId) && e.CreatedAt >= since).ToListAsync(ct), "回報",
            f => f is Entry e ? $"{e.Date:M/d} {(e.SlotId is null ? "額外花費" : "時段")} {e.InputAmount:N0}{(string.IsNullOrEmpty(e.Note) ? "" : "・" + e.Note)}" : "",
            f => ((Entry)f).CreatedAt);
        Add(await db.PoolTransfers.AsNoTracking().Where(t => periodIds.Contains(t.PeriodId) && t.CreatedAt >= since).ToListAsync(ct), "待分配池",
            f => f is PoolTransfer t ? $"{t.Date:M/d} {t.Amount:+#,0;-#,0;0} {t.Note}" : "", f => ((PoolTransfer)f).CreatedAt);
        Add(await db.IncomeAdjustments.AsNoTracking().Where(a => periodIds.Contains(a.PeriodId) && a.CreatedAt >= since).ToListAsync(ct), "薪資調整",
            f => f is IncomeAdjustment a ? $"{a.Kind} {a.Amount:+#,0;-#,0;0}" : "", f => ((IncomeAdjustment)f).CreatedAt);
        Add(await db.Reconciliations.AsNoTracking().Where(r => r.UserId == userId && r.CreatedAt >= since).ToListAsync(ct), "對帳",
            f => f is Reconciliation r ? $"{r.Date:M/d} {(r.IsFull ? "完整對帳" : "部分帳戶")}{(string.IsNullOrEmpty(r.Note) ? "" : "・" + r.Note)}" : "",
            f => ((Reconciliation)f).CreatedAt);
        Add(await db.AccountTransfers.AsNoTracking().Where(t => t.UserId == userId && t.CreatedAt >= since).ToListAsync(ct), "轉帳",
            f => f is AccountTransfer t ? $"{t.Date:M/d} {t.Amount:N0}" : "", f => ((AccountTransfer)f).CreatedAt);
        Add(await db.AssetAdjustments.AsNoTracking().Where(a => a.UserId == userId && a.CreatedAt >= since).ToListAsync(ct), "資產加減",
            f => f is AssetAdjustment a ? $"{a.Date:M/d} {a.Amount:+#,0;-#,0;0} {a.Note}" : "", f => ((AssetAdjustment)f).CreatedAt);
        Add(await db.Debts.AsNoTracking().Where(d => d.UserId == userId && d.CreatedAt >= since).ToListAsync(ct), "應收應付",
            f => f is Debt d ? $"{d.Counterparty} {(d.Kind == DebtKind.Receivable ? "應收" : "應付")} {d.Amount:N0}" : "", f => ((Debt)f).CreatedAt);
        Add(await db.DebtSettlements.AsNoTracking().Where(s => s.UserId == userId && s.CreatedAt >= since).ToListAsync(ct), "收回 / 還款",
            f => f is DebtSettlement s ? $"{s.Date:M/d} {s.Amount:N0}" : "", f => ((DebtSettlement)f).CreatedAt);
        Add(await db.Installments.AsNoTracking().Where(i => i.UserId == userId && i.CreatedAt >= since).ToListAsync(ct), "分期",
            f => f is Installment i ? $"{i.Name}・{i.Periods} 期" : "", f => ((Installment)f).CreatedAt);
        Add(await db.InstallmentPrepayments.AsNoTracking().Where(p => p.UserId == userId && p.CreatedAt >= since).ToListAsync(ct), "提前還款",
            f => f is InstallmentPrepayment p ? $"{p.Date:M/d} {p.Amount:N0}" : "", f => ((InstallmentPrepayment)f).CreatedAt);
        Add(await db.PeriodCarryovers.AsNoTracking().Where(c => c.UserId == userId && c.CreatedAt >= since).ToListAsync(ct), "結轉",
            f => f is PeriodCarryover c ? $"{c.TargetDate:M/d} {c.Amount:+#,0;-#,0;0} {c.Label}" : "", f => ((PeriodCarryover)f).CreatedAt);

        foreach (var v in await db.SettingsVersions.AsNoTracking().Where(v => v.UserId == userId && v.CreatedAt >= since).ToListAsync(ct))
        {
            var from = v.EffectiveFrom == DateOnly.MinValue ? "最早" : v.EffectiveFrom.ToString("M/d", CultureInfo.InvariantCulture);
            items.Add(new HistoryItem(v.CreatedAt, "設定", v.IsCorrection ? "更正" : "新版本",
                $"{from} 起生效{(string.IsNullOrEmpty(v.Note) ? "" : "・" + v.Note)}", v.Id, null));
        }

        return items.OrderByDescending(i => i.At).Take(500).ToList();
    }

    private static int IdOf(IFact f) => f switch
    {
        Entry e => e.Id,
        PoolTransfer t => t.Id,
        IncomeAdjustment a => a.Id,
        Reconciliation r => r.Id,
        AccountTransfer t => t.Id,
        AssetAdjustment a => a.Id,
        Debt d => d.Id,
        DebtSettlement s => s.Id,
        Installment i => i.Id,
        InstallmentPrepayment p => p.Id,
        PeriodCarryover c => c.Id,
        _ => 0,
    };
}
