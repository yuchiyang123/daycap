using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/periods")]
public class PeriodsController(IPeriodService periods, IEntryService entries) : ControllerBase
{
    [HttpGet]
    public Task<List<PeriodSummaryDto>> List(CancellationToken ct) => periods.ListAsync(User.GetUserId(), ct);

    [HttpGet("current")]
    public Task<PeriodView> Current(CancellationToken ct) => periods.GetCurrentAsync(User.GetUserId(), ct);

    /// <summary>刪掉開始日期之前的試用週期。</summary>
    [HttpDelete("before-start")]
    public async Task<object> DeleteBeforeStart(CancellationToken ct) =>
        new { deleted = await periods.DeleteBeforeStartAsync(User.GetUserId(), ct) };

    [HttpGet("{id:int}")]
    public Task<PeriodView> Get(int id, CancellationToken ct) => periods.GetAsync(User.GetUserId(), id, ct);

    [HttpPost("{id:int}/rebuild")]
    public Task<PeriodView> Rebuild(int id, RebuildRequest req, CancellationToken ct) =>
        periods.RebuildAsync(User.GetUserId(), id, req.FromDate, ct);

    [HttpPost("{id:int}/entries")]
    public Task<PeriodView> CreateEntry(int id, CreateEntryRequest req, CancellationToken ct) =>
        entries.CreateAsync(User.GetUserId(), id, req, ct);

    [HttpPost("{id:int}/entries/preview")]
    public Task<EntryPreview> PreviewEntry(int id, CreateEntryRequest req, CancellationToken ct) =>
        entries.PreviewAsync(User.GetUserId(), id, req, ct);

    [HttpDelete("{id:int}/entries/{entryId:int}")]
    public Task<PeriodView> DeleteEntry(int id, int entryId, CancellationToken ct) =>
        entries.DeleteAsync(User.GetUserId(), id, entryId, ct);

    [HttpPost("{id:int}/transfers")]
    public Task<PeriodView> AddTransfer(int id, CreatePoolTransferRequest req, CancellationToken ct) =>
        entries.AddTransferAsync(User.GetUserId(), id, req, ct);

    [HttpPost("{id:int}/income-adjustments")]
    public Task<PeriodView> AddIncomeAdjustment(int id, CreateIncomeAdjustmentRequest req, CancellationToken ct) =>
        entries.AddIncomeAdjustmentAsync(User.GetUserId(), id, req, ct);

    [HttpDelete("{id:int}/income-adjustments/{adjustmentId:int}")]
    public Task<PeriodView> DeleteIncomeAdjustment(int id, int adjustmentId, CancellationToken ct) =>
        entries.DeleteIncomeAdjustmentAsync(User.GetUserId(), id, adjustmentId, ct);

    [HttpPost("{id:int}/income-confirm")]
    public Task<PeriodView> ConfirmIncome(int id, CancellationToken ct) =>
        entries.ConfirmIncomeAsync(User.GetUserId(), id, ct);

    [HttpPost("{id:int}/allocate")]
    public Task<PeriodView> Allocate(int id, AllocateRequest req, CancellationToken ct) =>
        entries.AllocateAsync(User.GetUserId(), id, req, ct);

    [HttpDelete("{id:int}/transfers/{transferId:int}")]
    public Task<PeriodView> DeleteTransfer(int id, int transferId, CancellationToken ct) =>
        entries.DeleteTransferAsync(User.GetUserId(), id, transferId, ct);
}
