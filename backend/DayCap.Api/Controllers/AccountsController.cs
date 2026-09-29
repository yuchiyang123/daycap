using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>帳戶、轉帳 / 繳卡費、對帳（§5、§12.1）。</summary>
[ApiController]
[Authorize]
[Route("api/accounts")]
public class AccountsController(IAccountService accounts) : ControllerBase
{
    [HttpGet]
    public Task<AccountsView> Get(CancellationToken ct) => accounts.GetAsync(User.GetUserId(), ct);

    [HttpPut]
    public Task<AccountsView> Save(List<AccountEdit> edits, CancellationToken ct) => accounts.SaveAccountsAsync(User.GetUserId(), edits, ct);

    [HttpGet("transfers")]
    public Task<List<TransferView>> Transfers([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        accounts.ListTransfersAsync(User.GetUserId(), from, to, ct);

    [HttpPost("transfers")]
    public Task<TransferView> AddTransfer(CreateTransferRequest req, CancellationToken ct) => accounts.AddTransferAsync(User.GetUserId(), req, ct);

    [HttpDelete("transfers/{id:int}")]
    public async Task<IActionResult> DeleteTransfer(int id, CancellationToken ct)
    {
        await accounts.DeleteTransferAsync(User.GetUserId(), id, ct);
        return NoContent();
    }

    [HttpGet("reconciliations")]
    public Task<List<ReconciliationSummary>> Reconciliations(CancellationToken ct) => accounts.ListReconciliationsAsync(User.GetUserId(), ct);

    [HttpPost("reconciliations/preview")]
    public Task<ReconciliationResult> Preview(CreateReconciliationRequest req, CancellationToken ct) =>
        accounts.PreviewReconciliationAsync(User.GetUserId(), req, ct);

    [HttpPost("reconciliations")]
    public Task<ReconciliationResult> Create(CreateReconciliationRequest req, CancellationToken ct) =>
        accounts.CreateReconciliationAsync(User.GetUserId(), req, ct);

    [HttpDelete("reconciliations/{id:int}")]
    public async Task<IActionResult> DeleteReconciliation(int id, CancellationToken ct)
    {
        await accounts.DeleteReconciliationAsync(User.GetUserId(), id, ct);
        return NoContent();
    }
}
