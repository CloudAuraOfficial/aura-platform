using Aura.Core.DTOs;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/deployments/{deploymentId:guid}/runs/{runId:guid}/logs")]
public class LogStreamController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ILogStreamService _logStream;

    public LogStreamController(AuraDbContext db, ILogStreamService logStream)
    {
        _db = db;
        _logStream = logStream;
    }

    /// <summary>
    /// SSE endpoint — streams real-time logs for a deployment run.
    /// Each message is sent as: data: {message}\n\n
    /// The stream ends when the run completes or the client disconnects.
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream(Guid deploymentId, Guid runId, CancellationToken ct)
    {
        var run = await _db.DeploymentRuns
            .FirstOrDefaultAsync(r => r.Id == runId && r.DeploymentId == deploymentId, ct);

        if (run is null)
        {
            Response.StatusCode = 404;
            await Response.WriteAsJsonAsync(
                new ErrorResponse("not_found", "Run not found.", 404), ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        await Response.Body.FlushAsync(ct);

        try
        {
            await foreach (var message in _logStream.SubscribeAsync(runId, ct))
            {
                await WriteSseEventAsync(message, ct);
            }

            // Send a final event so clients know the stream is done
            await WriteSseEventAsync("[done]", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — normal
        }
    }

    private async Task WriteSseEventAsync(string data, CancellationToken ct)
    {
        await Response.WriteAsync($"data: {data}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
