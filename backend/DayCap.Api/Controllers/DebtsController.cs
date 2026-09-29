using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>應收應付（§14 共用支出）。</summary>
[ApiController]
[Authorize]
[Route("api/debts")]
public class DebtsController(IDebtService debts) : ControllerBase
{
    [HttpGet]
    public Task<DebtsView> List(CancellationToken ct) => debts.ListAsync(User.GetUserId(), ct);

    [HttpPost]
    public Task<DebtsView> Create(CreateDebtRequest req, CancellationToken ct) => debts.CreateAsync(User.GetUserId(), req, ct);

    [HttpPost("{id:int}/settle")]
    public Task<DebtsView> Settle(int id, SettleDebtRequest req, CancellationToken ct) => debts.SettleAsync(User.GetUserId(), id, req, ct);

    [HttpDelete("{id:int}")]
    public Task<DebtsView> Delete(int id, CancellationToken ct) => debts.DeleteAsync(User.GetUserId(), id, ct);
}
