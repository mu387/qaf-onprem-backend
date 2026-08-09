using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QafOnPrem.Api.Contracts;
using QafOnPrem.Api.Services.AppData;

namespace QafOnPrem.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class ExecutionController(ISqlAppDataService appDataService) : ControllerBase
{
    private readonly ISqlAppDataService _appDataService = appDataService;

    [HttpPost("execution-device-pools")]
    public async Task<IActionResult> CreateDevicePool([FromBody] SaveExecutionDevicePoolRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.CreateExecutionDevicePoolAsync(User, request, cancellationToken), "Device Pool Created", "Device pool not found");
    }

    [HttpPut("execution-device-pools/{id:long}")]
    public async Task<IActionResult> UpdateDevicePool(long id, [FromBody] SaveExecutionDevicePoolRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.UpdateExecutionDevicePoolAsync(User, id, request, cancellationToken), "Device Pool Updated", "Device pool not found");
    }

    [HttpDelete("execution-device-pools/{id:long}")]
    public async Task<IActionResult> DeleteDevicePool(long id, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.DeleteExecutionDevicePoolAsync(User, id, cancellationToken), "Device Pool Deleted", "Device pool not found");
    }

    [HttpPost("execution-devices")]
    public async Task<IActionResult> CreateDevice([FromBody] SaveExecutionDeviceRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.CreateExecutionDeviceAsync(User, request, cancellationToken), "Device Created", "Device not found");
    }

    [HttpPut("execution-devices/{id:long}")]
    public async Task<IActionResult> UpdateDevice(long id, [FromBody] SaveExecutionDeviceRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.UpdateExecutionDeviceAsync(User, id, request, cancellationToken), "Device Updated", "Device not found");
    }

    [HttpDelete("execution-devices/{id:long}")]
    public async Task<IActionResult> DeleteDevice(long id, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.DeleteExecutionDeviceAsync(User, id, cancellationToken), "Device Deleted", "Device not found");
    }

    [HttpGet("execution-devices/{id:long}/health")]
    public async Task<IActionResult> DeviceHealth(long id, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.CheckExecutionDeviceHealthAsync(User, id, cancellationToken), "Device Health", "Device not found");
    }

    [HttpPost("execution-schedules")]
    public async Task<IActionResult> CreateSchedule([FromBody] SaveExecutionScheduleRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.CreateExecutionScheduleAsync(User, request, cancellationToken), "Schedule Created", "Schedule not found");
    }

    [HttpPut("execution-schedules/{id:long}")]
    public async Task<IActionResult> UpdateSchedule(long id, [FromBody] SaveExecutionScheduleRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.UpdateExecutionScheduleAsync(User, id, request, cancellationToken), "Schedule Updated", "Schedule not found");
    }

    [HttpDelete("execution-schedules/{id:long}")]
    public async Task<IActionResult> DeleteSchedule(long id, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.DeleteExecutionScheduleAsync(User, id, cancellationToken), "Schedule Deleted", "Schedule not found");
    }

    [HttpPost("execution-schedules/{id:long}/run-now")]
    public async Task<IActionResult> RunScheduleNow(long id, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.RunExecutionScheduleNowAsync(User, id, cancellationToken), "Schedule enqueued", "Schedule not found");
    }

    [HttpPost("execution-queue")]
    public async Task<IActionResult> CreateQueue([FromBody] CreateExecutionQueueRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.CreateExecutionQueueAsync(User, request, cancellationToken), "Queue Created", "Execution queue not found");
    }

    [HttpPost("execution-queue/{id:long}/cancel")]
    public async Task<IActionResult> CancelQueue(long id, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.CancelExecutionQueueAsync(User, id, cancellationToken), "Queue Canceled", "Execution queue not found");
    }

    [HttpPost("execution-queue/{id:long}/retry")]
    public async Task<IActionResult> RetryQueue(long id, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.RetryExecutionQueueAsync(User, id, cancellationToken), "Queue Retried", "Execution queue not found");
    }

    [HttpPost("execution-queue/bulk-delete")]
    public async Task<IActionResult> BulkDeleteQueues([FromBody] BulkDeleteExecutionQueuesRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.BulkDeleteExecutionQueuesAsync(User, request.Ids, cancellationToken), "Queue Entries Deleted", "Execution queue not found");
    }

    [HttpPost("execution-queue/{id:long}/items/status")]
    public async Task<IActionResult> UpdateQueueItemsStatus(long id, [FromBody] ExecutionQueueItemsStatusRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.UpdateExecutionQueueItemsStatusAsync(User, id, request, cancellationToken), "Queue Item Status Updated", "Execution queue not found");
    }

    [HttpPost("execution-queue/{id:long}/items/start")]
    public async Task<IActionResult> StartQueueItem(long id, [FromBody] ExecutionQueueItemLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.StartExecutionQueueItemAsync(User, id, request, cancellationToken), "Queue Item Start Acknowledged", "Execution queue not found");
    }

    [HttpPost("execution-queue/{id:long}/items/heartbeat")]
    public async Task<IActionResult> HeartbeatQueueItem(long id, [FromBody] ExecutionQueueItemLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.HeartbeatExecutionQueueItemAsync(User, id, request, cancellationToken), "Queue Item Heartbeat Recorded", "Execution queue not found");
    }

    [HttpPost("execution-queue/{id:long}/items/interrupted")]
    public async Task<IActionResult> InterruptQueueItem(long id, [FromBody] ExecutionQueueItemLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.InterruptExecutionQueueItemAsync(User, id, request, cancellationToken), "Queue Item Interrupted (waiting recovery)", "Execution queue not found");
    }

    [HttpPost("execution-queue/{id:long}/items/finish")]
    public async Task<IActionResult> FinishQueueItem(long id, [FromBody] ExecutionQueueItemFinishRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.FinishExecutionQueueItemAsync(User, id, request, cancellationToken), "Queue Item Finish Acknowledged", "Execution queue not found");
    }

    [HttpPost("execution-queue/claim-local")]
    public async Task<IActionResult> ClaimLocal([FromBody] ClaimExecutionQueueRequest request, CancellationToken cancellationToken = default)
    {
        return FromMutationResult(await _appDataService.ClaimLocalExecutionQueueAsync(User, request, cancellationToken), "Queue Item Claimed", "Execution queue not found");
    }

    [HttpPost("execution-queue/runner/bootstrap")]
    public IActionResult BootstrapRunner([FromBody] ExecutionRunnerBootstrapRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunnerId))
        {
            return ValidationFailure("runner_id", "The runner_id field is required.");
        }

        var token = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        return Ok(Success("Runner session issued", new RunnerSessionBootstrapDto
        {
            RunnerSessionToken = token,
            ExpiresInSeconds = 3600,
            RunnerId = request.RunnerId.Trim()
        }));
    }

    private IActionResult FromMutationResult<T>(ExecutionMutationResult<T> result, string successMessage, string notFoundMessage)
    {
        return result.Outcome switch
        {
            ExecutionMutationOutcome.Success => Ok(Success(successMessage, result.Data)),
            ExecutionMutationOutcome.NotFound => StatusCode(StatusCodes.Status404NotFound, Failure(result.ErrorMessage ?? notFoundMessage, StatusCodes.Status404NotFound)),
            ExecutionMutationOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, Failure(result.ErrorMessage ?? "Forbidden", StatusCodes.Status403Forbidden)),
            ExecutionMutationOutcome.Conflict => StatusCode(StatusCodes.Status409Conflict, Failure(result.ErrorMessage ?? "Conflict", StatusCodes.Status409Conflict)),
            ExecutionMutationOutcome.ValidationFailed when !string.IsNullOrWhiteSpace(result.ErrorField) => ValidationFailure(result.ErrorField!, result.ErrorMessage ?? "The given data was invalid."),
            ExecutionMutationOutcome.ValidationFailed => StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = result.ErrorMessage ?? "The given data was invalid." }),
            _ => StatusCode(StatusCodes.Status400BadRequest, Failure(result.ErrorMessage ?? "Unable to process request.", StatusCodes.Status400BadRequest))
        };
    }

    private static ApiResponse<T> Success<T>(string message, T data)
    {
        return new ApiResponse<T>(true, StatusCodes.Status200OK, message, data);
    }

    private static ApiResponse<object> Failure(string message, int statusCode)
    {
        return new ApiResponse<object>(false, statusCode, message, null);
    }

    private IActionResult ValidationFailure(string field, string message)
    {
        return StatusCode(StatusCodes.Status422UnprocessableEntity, new
        {
            message = "The given data was invalid.",
            errors = new Dictionary<string, string[]>
            {
                [field] = [message]
            }
        });
    }
}