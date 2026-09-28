using DayCap.Api.Common;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public class NotificationsController(INotificationService notifications) : ControllerBase
{
    [HttpGet]
    public Task<NotificationsView> Get(CancellationToken ct) => notifications.GetAsync(User.GetUserId(), ct);

    [HttpPost("{id:int}/popup-shown")]
    public async Task<IActionResult> PopupShown(int id, CancellationToken ct)
    {
        await notifications.MarkPopupShownAsync(User.GetUserId(), id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(User.GetUserId(), ct);
        return NoContent();
    }
}
