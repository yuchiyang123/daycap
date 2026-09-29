using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;

namespace DayCap.Api.Services;

/// <summary>
/// 一個使用者的所有設定版本，依生效日排好。重播時用 <see cref="For"/> 取「某一天有效的版本」。
/// </summary>
public sealed class SettingsTimeline
{
    public IReadOnlyList<(SettingsVersion Version, SettingsDocument Doc)> Versions { get; }

    public SettingsTimeline(IEnumerable<SettingsVersion> versions)
    {
        Versions = versions
            .OrderBy(v => v.EffectiveFrom).ThenBy(v => v.CreatedAt).ThenBy(v => v.Id)
            .Select(v => (v, SettingsJson.Deserialize(v.Document)))
            .ToList();
        if (Versions.Count == 0) throw new InvalidOperationException("沒有任何設定版本。");
    }

    /// <summary>某一天有效的版本：生效日 ≤ 那天的版本中最晚的一份；同一天生效的取最後建立的。</summary>
    public (SettingsVersion Version, SettingsDocument Doc) For(DateOnly day)
    {
        (SettingsVersion, SettingsDocument)? found = null;
        foreach (var v in Versions)
        {
            if (v.Version.EffectiveFrom <= day) found = v;
            else break;
        }
        return found ?? Versions[0];
    }

    /// <summary>最新的一份（設定頁編輯的就是這份，可能明天才生效）。</summary>
    public (SettingsVersion Version, SettingsDocument Doc) Latest => Versions[^1];

    /// <summary>
    /// 在 day 當天建立、但 day 之後才生效的版本（一般儲存）。
    /// 依 §4.1 的決定：這些版本對 day 當天還沒回報的時段取較低值。
    /// </summary>
    public IEnumerable<(SettingsVersion Version, SettingsDocument Doc)> CreatedOnButLater(DateOnly day) =>
        Versions.Where(v => v.Version.CreatedOn == day && v.Version.EffectiveFrom > day);
}
