using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/assets")]
public class AssetsController(IAssetService assets) : ControllerBase
{
    [HttpGet]
    public Task<AssetsView> Get([FromQuery] bool refresh, CancellationToken ct) => assets.GetAsync(User.GetUserId(), refresh, ct);

    [HttpPut]
    public Task<AssetsView> Save(SaveAssetsRequest req, CancellationToken ct) => assets.SaveAsync(User.GetUserId(), req, ct);
}
