using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>
/// 前端進站先打這支判斷有沒有登入。走自己的後端（而不是直接問 Mini-SSO 的 /api/auth/me），
/// 這樣本機開發用 DevAuth 時也是同一條路。
/// </summary>
[ApiController]
[Authorize]
[Route("api/me")]
public class MeController : ControllerBase
{
    [HttpGet]
    public MeDto Get() => new(User.GetUserId(), User.GetUserName());
}
