using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>每晚通知（§9.4）：訂閱、設定、測試。</summary>
[ApiController]
[Authorize]
[Route("api/push")]
public class PushController(IPushService push) : ControllerBase
{
    [HttpGet("key")]
    public async Task<object> Key(CancellationToken ct) => new { publicKey = await push.PublicKeyAsync(ct) };

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(PushSubscribeRequest req, CancellationToken ct)
    {
        await push.SubscribeAsync(User.GetUserId(), req, ct);
        return NoContent();
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(PushUnsubscribeRequest req, CancellationToken ct)
    {
        await push.UnsubscribeAsync(User.GetUserId(), req.Endpoint, ct);
        return NoContent();
    }

    [HttpGet("settings")]
    public Task<PushSettingsDto> Settings(CancellationToken ct) => push.GetSettingsAsync(User.GetUserId(), ct);

    [HttpPut("settings")]
    public Task<PushSettingsDto> SaveSettings(PushSettingsDto req, CancellationToken ct) => push.SaveSettingsAsync(User.GetUserId(), req, ct);

    [HttpPost("test")]
    public async Task<object> Test(CancellationToken ct) => new { delivered = await push.SendNowAsync(User.GetUserId(), ct) };
}
