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

    // 生活錢對帳：沒登記的帳戶總額 − 信用卡未繳，對到記帳算出來應該剩的錢
    [HttpGet("living-checks")]
    public Task<List<LivingCheckSummary>> LivingChecks(CancellationToken ct) => accounts.ListLivingChecksAsync(User.GetUserId(), ct);

    [HttpPost("living-checks/preview")]
    public Task<ReconciliationResult> PreviewLiving(CreateLivingCheckRequest req, CancellationToken ct) =>
        accounts.PreviewLivingCheckAsync(User.GetUserId(), req, ct);

    [HttpPost("living-checks")]
    public Task<ReconciliationResult> CreateLiving(CreateLivingCheckRequest req, CancellationToken ct) =>
        accounts.CreateLivingCheckAsync(User.GetUserId(), req, ct);

    [HttpDelete("living-checks/{id:int}")]
    public async Task<IActionResult> DeleteLiving(int id, CancellationToken ct)
    {
        await accounts.DeleteLivingCheckAsync(User.GetUserId(), id, ct);
        return NoContent();
    }

    [HttpDelete("reconciliations/{id:int}")]
    public async Task<IActionResult> DeleteReconciliation(int id, CancellationToken ct)
    {
        await accounts.DeleteReconciliationAsync(User.GetUserId(), id, ct);
        return NoContent();
    }
}
