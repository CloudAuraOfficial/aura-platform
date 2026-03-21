using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Member,Operator")]
[Route("api/v1/experiments")]
public class ExperimentsController : ControllerBase
{
    private readonly IExperimentService _experiments;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public ExperimentsController(IExperimentService experiments, ITenantContext tenant, IAuditService audit)
    {
        _experiments = experiments;
        _tenant = tenant;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? project,
        [FromQuery] Core.Enums.ExperimentStatus? status,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25)
    {
        (offset, limit) = PaginationDefaults.Clamp(offset, limit);
        var (items, total) = await _experiments.ListAsync(project, status, offset, limit);
        var dtos = items.Select(ToDto).ToList();
        return Ok(new PaginatedResponse<ExperimentResponse>(dtos, total, offset, limit));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var experiment = await _experiments.GetByIdAsync(id);
        if (experiment is null)
            return NotFound(new ErrorResponse("not_found", "Experiment not found.", 404));

        return Ok(ToDto(experiment));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Member")]
    public async Task<IActionResult> Create([FromBody] CreateExperimentRequest request)
    {
        try
        {
            var experiment = await _experiments.CreateAsync(
                request.Project, request.Name, request.Hypothesis,
                request.Variants, request.MetricName);

            await _audit.LogAsync(_tenant.TenantId, GetCurrentUserId(),
                "create", "Experiment", experiment.Id);

            AuraMetrics.ExperimentAssignmentsTotal.WithLabels(experiment.Name, "created").Inc(0);

            return CreatedAtAction(nameof(Get), new { id = experiment.Id }, ToDto(experiment));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse("bad_request", ex.Message, 400));
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Member")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateExperimentRequest request)
    {
        try
        {
            var experiment = await _experiments.UpdateAsync(
                id, request.Name, request.Hypothesis, request.Status, request.Conclusion);

            await _audit.LogAsync(_tenant.TenantId, GetCurrentUserId(),
                "update", "Experiment", experiment.Id, $"status={experiment.Status}");

            return Ok(ToDto(experiment));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new ErrorResponse("not_found", "Experiment not found.", 404));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Invalid status"))
        {
            return BadRequest(new ErrorResponse("bad_request", ex.Message, 400));
        }
    }

    [HttpGet("{id:guid}/results")]
    public async Task<IActionResult> GetResults(Guid id)
    {
        try
        {
            var results = await _experiments.GetResultsAsync(id);

            var variantDtos = results.Variants.ToDictionary(
                kvp => kvp.Key,
                kvp => new VariantResultResponse(
                    kvp.Value.SampleSize, kvp.Value.Mean, kvp.Value.StdDev,
                    kvp.Value.Min, kvp.Value.Max));

            StatisticalSignificanceResponse? sigDto = null;
            if (results.Significance is not null)
            {
                var s = results.Significance;
                sigDto = new StatisticalSignificanceResponse(
                    s.TStatistic, s.PValue, s.DegreesOfFreedom, s.IsSignificant, s.ConfidenceLevel);
            }

            return Ok(new ExperimentResultsResponse(
                results.ExperimentId, results.Name, results.MetricName, variantDtos, sigDto));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new ErrorResponse("not_found", "Experiment not found.", 404));
        }
    }

    [HttpPost("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignVariantRequest request)
    {
        try
        {
            var variantId = await _experiments.AssignVariantAsync(id, request.SubjectKey);
            var subjectHash = Infrastructure.Services.ExperimentService.ComputeHash($"{id}:{request.SubjectKey}");

            AuraMetrics.ExperimentAssignmentsTotal.WithLabels(id.ToString(), variantId).Inc();

            return Ok(new AssignVariantResponse(id, variantId, subjectHash));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new ErrorResponse("not_found", "Experiment not found.", 404));
        }
    }

    [HttpPost("{id:guid}/track")]
    public async Task<IActionResult> Track(Guid id, [FromBody] TrackEventRequest request)
    {
        try
        {
            await _experiments.TrackEventAsync(
                id, request.VariantId, request.SubjectHash,
                request.MetricName, request.MetricValue, request.Metadata);

            AuraMetrics.ExperimentEventsTotal.WithLabels(id.ToString(), request.MetricName).Inc();

            return Ok(new { tracked = true });
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponse("bad_request", ex.Message, 400));
        }
    }

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)
            ?? User.FindFirst("sub");
        return sub is not null ? Guid.Parse(sub.Value) : Guid.Empty;
    }

    private static ExperimentResponse ToDto(Experiment e) =>
        new(e.Id, e.Project, e.Name, e.Hypothesis, e.Status.ToString(),
            e.Variants, e.MetricName, e.StartedAt, e.ConcludedAt,
            e.Conclusion, e.CreatedAt);
}
