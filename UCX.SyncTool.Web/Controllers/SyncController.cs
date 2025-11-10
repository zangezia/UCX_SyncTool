using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using UCX.SyncTool.Core;
using UCX.SyncTool.Core.Services;
using UCX.SyncTool.Core.Models;
using UCX.SyncTool.Web.Hubs;

namespace UCX.SyncTool.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SyncController : ControllerBase
{
    private readonly FileSyncService _syncService;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<SyncController> _logger;

    public SyncController(
        FileSyncService syncService,
        IHubContext<SyncHub> hubContext,
        ILogger<SyncController> logger)
    {
        _syncService = syncService;
        _hubContext = hubContext;
        _logger = logger;
    }

    [HttpPost("start")]
    public IActionResult Start([FromBody] StartSyncRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                return BadRequest("Project and destination path are required");
            }

            if (!Directory.Exists(request.DestinationPath))
            {
                return BadRequest("Destination path does not exist");
            }

            _syncService.Start(
                request.Project,
                request.DestinationPath,
                async (msg) => await _hubContext.Clients.All.SendAsync("ReceiveLog", msg),
                request.MaxParallelism ?? 8);

            return Ok(new { message = "Synchronization started" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start synchronization");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        try
        {
            _syncService.Stop();
            return Ok(new { message = "Synchronization stopped" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop synchronization");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("projects")]
    public async Task<IActionResult> GetProjects()
    {
        try
        {
            var projects = await Task.Run(() => _syncService.FindAvailableProjects(msg =>
            {
                _logger.LogInformation(msg);
            }));

            return Ok(projects);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get projects");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        try
        {
            var status = new
            {
                CompletedCaptures = _syncService.GetCompletedCapturesCount(),
                LastCaptureNumber = _syncService.GetLastCaptureNumber(),
                CompletedTestCaptures = _syncService.GetCompletedTestCapturesCount(),
                LastTestCaptureNumber = _syncService.GetLastTestCaptureNumber()
            };

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get status");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

public class StartSyncRequest
{
    public string Project { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public int? MaxParallelism { get; set; }
}
