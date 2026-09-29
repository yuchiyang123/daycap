using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Settings;

namespace DayCap.Api.Services;

/// <summary>
/// 類別細項（§13）：月類別預算只有總額，超標時看不出是哪一項。回報可以標細項，細項可以設每期上限。
/// 這裡只在重播之後彙總（不動 BudgetEngine）：超過上限只提醒，錢怎麼扣照原本分類的規則。
/// </summary>
public static class SubItems
{
    public static PeriodView Decorate(PeriodView view, SettingsDocument doc)
    {
        var configured = doc.Categories
            .SelectMany(c => (c.SubItems ?? []).Select(si => (c.Id, si.Name, si.Cap)))
            .ToList();
        var spent = view.Entries
            .Where(e => !string.IsNullOrEmpty(e.SubItem))
            .GroupBy(e => (e.CategoryId, e.SubItem!))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Actual + e.JarCovered));

        var result = configured
            .Select(x => Row(x.Id, x.Name, x.Cap, spent.GetValueOrDefault((x.Id, x.Name))))
            .ToList();
        // 回報時臨時打的細項（設定裡沒有）也列出來，沒有上限
        result.AddRange(spent.Keys
            .Where(k => !configured.Any(x => x.Id == k.CategoryId && x.Name == k.Item2))
            .Select(k => Row(k.CategoryId, k.Item2, null, spent[k])));

        return view with { SubItems = result };
    }

    private static SubItemView Row(int categoryId, string name, decimal? cap, decimal spent) =>
        new(categoryId, name, cap, spent, cap is { } c ? Math.Max(0, spent - c) : 0);
}
