using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>罐子：預約支出、年繳預留款、儲蓄目標（§11.2）。</summary>
[ApiController]
[Authorize]
[Route("api/jars")]
public class JarsController(IJarService jars) : ControllerBase
{
    [HttpGet]
    public Task<List<JarView>> List(CancellationToken ct) => jars.ListAsync(User.GetUserId(), ct);

    [HttpPost]
    public Task<List<JarView>> Create(SaveJarRequest req, CancellationToken ct) => jars.CreateAsync(User.GetUserId(), req, ct);

    [HttpPut("{id:int}")]
    public Task<List<JarView>> Update(int id, SaveJarRequest req, CancellationToken ct) => jars.UpdateAsync(User.GetUserId(), id, req, ct);

    /// <summary>Amount &gt; 0 存進罐子（從待分配池），&lt; 0 拿回池子。</summary>
    [HttpPost("{id:int}/move")]
    public Task<List<JarView>> Move(int id, JarMoveRequest req, CancellationToken ct) => jars.MoveAsync(User.GetUserId(), id, req.Amount, ct);

    [HttpPost("{id:int}/close")]
    public Task<List<JarView>> Close(int id, CancellationToken ct) => jars.CloseAsync(User.GetUserId(), id, ct);
}
