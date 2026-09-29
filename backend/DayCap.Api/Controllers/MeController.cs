using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using DayCap.Api.Services.Onboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>
/// 前端進站先打這支判斷有沒有登入。走自己的後端（而不是直接問 Mini-SSO 的 /api/auth/me），
/// 這樣本機開發用 DevAuth 時也是同一條路。順便回傳要不要走新手引導、看過哪些一次性提示。
/// </summary>
[ApiController]
[Authorize]
[Route("api/me")]
public class MeController(ISettingsService settings, IOnboardingService onboarding) : ControllerBase
{
    [HttpGet]
    public async Task<MeDto> Get(CancellationToken ct)
    {
        var profile = await settings.EnsureProfileAsync(User.GetUserId(), ct);
        return new MeDto(User.GetUserId(), User.GetUserName(), profile.OnboardedAt is not null, OnboardingService.SeenTips(profile));
    }

    /// <summary>一次性提示（§20.9）看過了。</summary>
    [HttpPost("tips/{key}")]
    public Task<List<string>> SeenTip(string key, CancellationToken ct) => onboarding.MarkTipSeenAsync(User.GetUserId(), key, ct);
}
