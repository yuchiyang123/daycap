using DayCap.Api.Data;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IAutoMoneyService
{
    /// <summary>執行這個使用者到今天為止該做、還沒做的自動轉帳與定期定額。回傳這次新做了幾筆。</summary>
    Task<int> RunAsync(string userId, CancellationToken ct = default);
    Task<int> RunAllAsync(CancellationToken ct = default);
}

/// <summary>
/// 固定支出的自動執行：
/// - 轉到帳戶（例如緊急預備金每月 6 號存進本金戶）：扣款日當天記一筆帳戶互轉。
/// - 定期定額：扣款日（休市就下一個交易日）用當天收盤價買整數股，持股股數、平均成本跟著更新，錢從扣款帳戶出去。
/// 每一期用 AutoKey 確保只做一次；只往回補最近 31 天（第一次開啟時不會一路補到很久以前）。
/// 設定用扣款日那天有效的設定版本，所以「從明天起生效」的設定不會回頭補今天以前的。
/// </summary>
public class AutoMoneyService(DayCapDbContext db, ISettingsService settings, IQuoteService quotes, IAppClock clock,
    ILogger<AutoMoneyService> log) : IAutoMoneyService
{
    public const int LookbackDays = 31;

    public async Task<int> RunAllAsync(CancellationToken ct = default)
    {
        var users = await db.Profiles.AsNoTracking().Select(p => p.UserId).ToListAsync(ct);
        var done = 0;
        foreach (var u in users)
        {
            try
            {
                done += await RunAsync(u, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Auto money failed for {User}", u);
            }
        }
        return done;
    }

    public async Task<int> RunAsync(string userId, CancellationToken ct = default)
    {
        var timeline = await settings.GetTimelineAsync(userId, ct);
        if (!timeline.Versions.Any(v => v.Doc.Categories.Any(c => c.FixedItems.Any(f => f.IsAuto)))) return 0;

        var today = await settings.LogicalTodayAsync(userId, ct);
        var accounts = await db.CashAccounts.AsNoTracking().Where(a => a.UserId == userId && !a.IsArchived).Select(a => a.Id).ToListAsync(ct);
        var doneKeys = (await db.AccountTransfers.AsNoTracking().Where(t => t.UserId == userId && t.AutoKey != null).Select(t => t.AutoKey!).ToListAsync(ct))
            .Concat(await db.HoldingPurchases.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.AutoKey).ToListAsync(ct))
            .Concat(await db.AssetAdjustments.AsNoTracking().Where(a => a.UserId == userId && a.AutoKey != null).Select(a => a.AutoKey!).ToListAsync(ct))
            .ToHashSet();

        var created = 0;
        for (var d = today.AddDays(-LookbackDays); d <= today; d = d.AddDays(1))
        {
            var doc = timeline.For(d).Doc;
            // 這一天到期的固定支出（月繳看扣款日；季繳 / 年繳還要看月份）
            foreach (var charge in BudgetMath.FixedCharges(doc, d, d).Where(c => c.DueDate == d && c.FixedItemId is not null))
            {
                var item = doc.Categories.SelectMany(c => c.FixedItems).First(f => f.Id == charge.FixedItemId);
                if (!item.IsAuto) continue;
                if (item.FromAccountId is { } src && !accounts.Contains(src)) continue;

                if (item.ToAccountId is { } to)
                {
                    if (!accounts.Contains(to)) continue;
                    if (item.FromAccountId is { } from)
                    {
                        var key = $"fixed:{item.Id}:{d:yyyy-MM-dd}";
                        if (doneKeys.Contains(key)) continue;
                        db.AccountTransfers.Add(new AccountTransfer
                        {
                            UserId = userId, Date = d, Kind = TransferKind.Transfer, FromAccountId = from, ToAccountId = to,
                            Amount = item.Amount, Note = $"自動：{item.Name}", AutoKey = key, CreatedAt = clock.UtcNow,
                        });
                        doneKeys.Add(key);
                    }
                    else
                    {
                        // 錢從外部（沒登記的帳戶）存進來
                        var key = $"deposit:{item.Id}:{d:yyyy-MM-dd}";
                        if (doneKeys.Contains(key)) continue;
                        db.AssetAdjustments.Add(new AssetAdjustment
                        {
                            UserId = userId, CashAccountId = to, Date = d, Amount = item.Amount, Note = $"自動存入：{item.Name}",
                            Source = "auto", AutoKey = key, CreatedAt = clock.UtcNow,
                        });
                        doneKeys.Add(key);
                    }
                    created++;
                }
                else if (item.HoldingId is { } holdingId)
                {
                    var key = $"dca:{item.Id}:{d:yyyy-MM-dd}";
                    if (doneKeys.Contains(key))
                    {
                        // 已經買過，但設定是「錢先從外部存進」而那筆存入沒記到（例如買進後才補勾這個選項）：補上
                        var depositKey = $"deposit:{item.Id}:{d:yyyy-MM-dd}";
                        if (item.FundedExternally == true && !doneKeys.Contains(depositKey))
                        {
                            var bought = await db.HoldingPurchases.AsNoTracking()
                                .FirstOrDefaultAsync(p => p.UserId == userId && p.AutoKey == key && !p.IsVoid, ct);
                            if (bought is not null && accounts.Contains(bought.FromAccountId))
                            {
                                db.AssetAdjustments.Add(new AssetAdjustment
                                {
                                    UserId = userId, CashAccountId = bought.FromAccountId, Date = bought.TradeDate, Amount = bought.Budget,
                                    Note = $"自動存入：{item.Name}", Source = "auto", AutoKey = depositKey, CreatedAt = clock.UtcNow,
                                });
                                doneKeys.Add(depositKey);
                                created++;
                            }
                        }
                        continue;
                    }
                    if (await BuyAsync(userId, item, holdingId, d, today, key, ct))
                    {
                        doneKeys.Add(key);
                        created++;
                    }
                }
            }
        }
        if (created > 0) await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>定期定額買進：收盤價還查不到（今天還沒收盤、休市）就先不做，下次再試。</summary>
    private async Task<bool> BuyAsync(string userId, FixedItemDoc item, int holdingId, DateOnly due, DateOnly today, string key, CancellationToken ct)
    {
        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.Id == holdingId && h.UserId == userId, ct);
        if (holding is null || holding.ManualPrice is not null) return false; // 手動價格的標的抓不到當天收盤價

        var close = await quotes.GetCloseOnOrAfterAsync(holding.Symbol, due, ct);
        if (close is not { } c || c.TradeDate > today) return false;
        // 今天的收盤價要等收盤後才有（約 14:00）；資料源有了就代表已經收盤
            if (item.FundedExternally == true)
            {
                // 錢先從外部存進扣款帳戶（例如薪轉戶轉進玉山），再從扣款帳戶買
                db.AssetAdjustments.Add(new AssetAdjustment
                {
                    UserId = userId, CashAccountId = item.FromAccountId!.Value, Date = c.TradeDate, Amount = item.Amount,
                    Note = $"自動存入：{item.Name}", Source = "auto", AutoKey = $"deposit:{item.Id}:{due:yyyy-MM-dd}", CreatedAt = clock.UtcNow,
                });
            }
        var shares = Math.Floor(item.Amount / c.Close);
        var spent = Math.Round(shares * c.Close, 0, MidpointRounding.AwayFromZero);

        if (shares > 0)
        {
            var oldCost = holding.AvgCost * holding.Shares;
            holding.Shares += shares;
            holding.AvgCost = Math.Round((oldCost + shares * c.Close) / holding.Shares, 4);
        }
        db.HoldingPurchases.Add(new HoldingPurchase
        {
            UserId = userId, HoldingId = holding.Id, FixedItemId = item.Id, DueDate = due, TradeDate = c.TradeDate,
            Budget = item.Amount, Price = c.Close, Shares = shares, Spent = spent, FromAccountId = item.FromAccountId!.Value,
            AutoKey = key, CreatedAt = clock.UtcNow,
        });
        return true;
    }
}

/// <summary>每小時跑一次自動轉帳 / 定期定額（收盤價下午才有，所以不能只在半夜跑）。</summary>
public class AutoMoneyWorker(IServiceScopeFactory scopes, ILogger<AutoMoneyWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAutoMoneyService>().RunAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Auto money round failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
