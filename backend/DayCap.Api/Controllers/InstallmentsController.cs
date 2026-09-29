using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>分期（§15）。</summary>
[ApiController]
[Authorize]
[Route("api/installments")]
public class InstallmentsController(IInstallmentService installments) : ControllerBase
{
    [HttpGet]
    public Task<List<InstallmentView>> List(CancellationToken ct) => installments.ListAsync(User.GetUserId(), ct);

    [HttpPost("preview")]
    public InstallmentView Preview(CreateInstallmentRequest req) => installments.Preview(req);

    [HttpPost]
    public Task<List<InstallmentView>> Create(CreateInstallmentRequest req, CancellationToken ct) => installments.CreateAsync(User.GetUserId(), req, ct);

    [HttpPost("{id:int}/prepay")]
    public Task<List<InstallmentView>> Prepay(int id, PrepayRequest req, CancellationToken ct) => installments.PrepayAsync(User.GetUserId(), id, req, ct);

    [HttpDelete("{id:int}")]
    public Task<List<InstallmentView>> Delete(int id, CancellationToken ct) => installments.DeleteAsync(User.GetUserId(), id, ct);
}
