using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public class SettingsController(ISettingsService settings) : ControllerBase
{
    [HttpGet]
    public Task<SettingsView> Get(CancellationToken ct) => settings.GetAsync(User.GetUserId(), ct);

    /// <summary>新增一筆設定版本：一般從明天起生效；帶 CorrectionFrom 是更正過去（必須附原因）。</summary>
    [HttpPut]
    public Task<SettingsView> Save(SaveSettingsRequest req, CancellationToken ct) => settings.SaveAsync(User.GetUserId(), req, ct);
}
