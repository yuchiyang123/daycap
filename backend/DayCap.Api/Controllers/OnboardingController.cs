using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services.Onboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>範本與新手引導（§20）。</summary>
[ApiController]
[Authorize]
[Route("api/onboarding")]
public class OnboardingController(IOnboardingService onboarding) : ControllerBase
{
    [HttpGet]
    public Task<OnboardingState> Get(CancellationToken ct) => onboarding.GetAsync(User.GetUserId(), ct);

    [HttpPost("preview")]
    public Task<TemplatePreview> Preview(ApplyTemplateRequest req, CancellationToken ct) => onboarding.PreviewAsync(User.GetUserId(), req, ct);

    [HttpPost("apply")]
    public Task<TemplatePreview> Apply(ApplyTemplateRequest req, CancellationToken ct) => onboarding.ApplyAsync(User.GetUserId(), req, ct);

    /// <summary>不用範本，自己到設定頁設定。</summary>
    [HttpPost("skip")]
    public async Task<IActionResult> Skip(CancellationToken ct)
    {
        await onboarding.SkipAsync(User.GetUserId(), ct);
        return NoContent();
    }
}
