using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>旅遊（§17）與收入中斷（§18）。</summary>
[ApiController]
[Authorize]
[Route("api")]
public class TripsController(ITripService trips, IRunwayService runway) : ControllerBase
{
    [HttpGet("trips")]
    public Task<List<TripView>> List(CancellationToken ct) => trips.ListAsync(User.GetUserId(), ct);

    [HttpPost("trips")]
    public Task<List<TripView>> Create(CreateTripRequest req, CancellationToken ct) => trips.CreateAsync(User.GetUserId(), req, ct);

    [HttpPost("trips/{id:int}/end")]
    public Task<List<TripView>> End(int id, CancellationToken ct) => trips.EndAsync(User.GetUserId(), id, ct);

    [HttpGet("runway")]
    public Task<RunwayView> Runway(CancellationToken ct) => runway.GetAsync(User.GetUserId(), ct);
}
