using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;
using QafOnPrem.Api.Configuration;
using QafOnPrem.Api.Contracts;
using QafOnPrem.Api.Services.AppData;

namespace QafOnPrem.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class AppDataController(
    ISqlAppDataService appDataService,
    ITestSuiteEditSessionService testSuiteEditSessionService,
    IWebHostEnvironment environment,
    IOptions<UploadStorageSettings> uploadStorageOptions) : ControllerBase
{
    private readonly ISqlAppDataService _appDataService = appDataService;
    private readonly ITestSuiteEditSessionService _testSuiteEditSessionService = testSuiteEditSessionService;
    private readonly IWebHostEnvironment _environment = environment;
    private readonly UploadStorageSettings _uploadStorageSettings = uploadStorageOptions.Value;
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [HttpGet("roles")]
    public async Task<IActionResult> Roles([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetRolesAsync(User, q, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("Roles", data));
    }

    [HttpGet("roles/{id:long}")]
    public async Task<IActionResult> Role(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetRoleAsync(User, id, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Role not found", StatusCodes.Status404NotFound))
            : Ok(Success("Role Details", data));
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateRoleRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.CreateRoleAsync(User, request, cancellationToken);
        return result.Outcome switch
        {
            SaveRoleOutcome.Saved => Ok(Success("Role Saved Successfully", result.Role!)),
            SaveRoleOutcome.DuplicateName => RoleValidationFailure("name", result.ErrorMessage ?? "The role name has already been taken."),
            SaveRoleOutcome.InvalidPermissions => RoleValidationFailure("permissions", result.ErrorMessage ?? "One or more selected permissions are invalid."),
            _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to save role", StatusCodes.Status400BadRequest))
        };
    }

    [HttpPut("roles/{id:long}")]
    public async Task<IActionResult> UpdateRole(long id, [FromBody] SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateRoleRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.UpdateRoleAsync(User, id, request, cancellationToken);
        return result.Outcome switch
        {
            SaveRoleOutcome.Saved => Ok(Success("Role Saved Successfully", result.Role!)),
            SaveRoleOutcome.NotFound => StatusCode(StatusCodes.Status404NotFound, Failure("Role not found", StatusCodes.Status404NotFound)),
            SaveRoleOutcome.DuplicateName => RoleValidationFailure("name", result.ErrorMessage ?? "The role name has already been taken."),
            SaveRoleOutcome.InvalidPermissions => RoleValidationFailure("permissions", result.ErrorMessage ?? "One or more selected permissions are invalid."),
            _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to save role", StatusCodes.Status400BadRequest))
        };
    }

    [HttpDelete("roles/{id:long}")]
    public async Task<IActionResult> DeleteRole(long id, CancellationToken cancellationToken = default)
    {
        var result = await _appDataService.DeleteRoleAsync(User, id, cancellationToken);
        return result switch
        {
            RoleDeletionOutcome.Deleted => Ok(Success("Role Deleted Successfully", Array.Empty<object>())),
            RoleDeletionOutcome.HasAssignedUsers => StatusCode(StatusCodes.Status409Conflict, Failure("Role cannot be deleted because it is assigned to users.", StatusCodes.Status409Conflict)),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure("Role not found", StatusCodes.Status404NotFound))
        };
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users([FromQuery] string? q, [FromQuery] string? email, [FromQuery(Name = "role_id")] long? roleId, [FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetUsersAsync(User, q, email, roleId, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("User List", data));
    }

    [HttpGet("users/{id:long}")]
    public async Task<IActionResult> UserDetails(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetUserAsync(User, id, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("User not found", StatusCodes.Status404NotFound))
            : Ok(Success("User Details", data));
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] SaveUserRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateCreateUserRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.CreateUserAsync(User, request, cancellationToken);
        return result.Outcome switch
        {
            SaveUserOutcome.Saved => Ok(Success("User Created Successfully", result.User!)),
            SaveUserOutcome.InvalidRole => ValidationFailure(result.ErrorMessage ?? "The selected role is invalid."),
            SaveUserOutcome.DuplicateEmail => ValidationFailure(result.ErrorMessage ?? "The email has already been taken."),
            SaveUserOutcome.UserLimitReached => StatusCode(StatusCodes.Status409Conflict, Failure(result.ErrorMessage ?? "User limit reached for this client.", StatusCodes.Status409Conflict)),
            _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create user", StatusCodes.Status400BadRequest))
        };
    }

    [HttpPut("users/{id:long}")]
    public async Task<IActionResult> UpdateUser(long id, [FromBody] SaveUserRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateUpdateUserRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.UpdateUserAsync(User, id, request, cancellationToken);
        return result.Outcome switch
        {
            SaveUserOutcome.Saved => Ok(Success("User Update Successfully", result.User!)),
            SaveUserOutcome.NotFound => StatusCode(StatusCodes.Status404NotFound, Failure("User not found", StatusCodes.Status404NotFound)),
            SaveUserOutcome.InvalidRole => ValidationFailure(result.ErrorMessage ?? "The selected role is invalid."),
            SaveUserOutcome.DuplicateEmail => ValidationFailure(result.ErrorMessage ?? "The email has already been taken."),
            SaveUserOutcome.UserLimitReached => StatusCode(StatusCodes.Status409Conflict, Failure(result.ErrorMessage ?? "User limit reached for this client.", StatusCodes.Status409Conflict)),
            _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to update user", StatusCodes.Status400BadRequest))
        };
    }

    [HttpDelete("users/{id:long}")]
    public async Task<IActionResult> DeleteUser(long id, CancellationToken cancellationToken = default)
    {
        var result = await _appDataService.DeleteUserAsync(User, id, cancellationToken);
        return result.Outcome switch
        {
            UserDeletionOutcome.Deleted => Ok(Success("User Deleted Successfully", Array.Empty<object>())),
            UserDeletionOutcome.Blocked => StatusCode(StatusCodes.Status409Conflict, Failure(result.ErrorMessage ?? "User cannot be deleted.", StatusCodes.Status409Conflict)),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure("User not found", StatusCodes.Status404NotFound))
        };
    }

    [HttpPost("users/bulk-delete")]
    public async Task<IActionResult> BulkDeleteUsers([FromBody] BulkDeleteUsersRequest request, CancellationToken cancellationToken = default)
    {
        if (request.UserIds.Count == 0)
        {
            return ValidationFailure("The user_ids field is required.");
        }

        var result = await _appDataService.BulkDeleteUsersAsync(User, request.UserIds, cancellationToken);
        return result.Outcome switch
        {
            UserDeletionOutcome.Deleted => Ok(Success("User Deleted Successfully", Array.Empty<object>())),
            UserDeletionOutcome.Blocked => StatusCode(StatusCodes.Status409Conflict, Failure(result.ErrorMessage ?? "User cannot be deleted.", StatusCodes.Status409Conflict)),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure("User not found", StatusCodes.Status404NotFound))
        };
    }

    [HttpGet("assignable-users")]
    public async Task<IActionResult> AssignableUsers(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetAssignableUsersAsync(User, cancellationToken);
        return Ok(Success("Assignable Users", data));
    }

    [HttpGet("users/settings")]
    public async Task<IActionResult> UserSettings(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetUserSettingsAsync(User, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("User Settings Not Found Successfully", StatusCodes.Status404NotFound))
            : Ok(Success("User Settings", data));
    }

    [HttpPost("users/settings")]
    public async Task<IActionResult> SaveUserSettings([FromBody] UpdateUserSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.SaveUserSettingsAsync(User, request.Settings, cancellationToken);
        return Ok(Success("User Settings Saved Successfully", data));
    }

    [HttpGet("components")]
    public async Task<IActionResult> Components(
        [FromQuery] string? name,
        [FromQuery(Name = "page_name")] string? pageName,
        [FromQuery] string? feature,
        [FromQuery(Name = "project_id")] string? projectIds,
        [FromQuery(Name = "type_id")] string? typeIds,
        [FromQuery] bool? status,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetComponentsAsync(User, name, pageName, feature, projectIds, typeIds, status, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("Components List", data));
    }

    [HttpGet("components/{id:long}")]
    public async Task<IActionResult> Component(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetComponentAsync(User, id, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Component not found", StatusCodes.Status404NotFound))
            : Ok(Success("Component Details", data));
    }

    [HttpGet("components/exists")]
    public async Task<IActionResult> ComponentExists([FromQuery(Name = "project_id")] long? projectId, [FromQuery] string? page, [FromQuery] string? feature, [FromQuery(Name = "exclude_id")] long? excludeId, CancellationToken cancellationToken = default)
    {
        if (!projectId.HasValue || string.IsNullOrWhiteSpace(page) || string.IsNullOrWhiteSpace(feature))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("project_id, page and feature are required", StatusCodes.Status400BadRequest));
        }

        var exists = await _appDataService.ComponentExistsAsync(User, projectId.Value, page, feature, excludeId, cancellationToken);
        return Ok(Success("Component Exists", new ComponentExistsResponseDto { Exists = exists }));
    }

    [HttpGet("components/catalog")]
    public async Task<IActionResult> ComponentMetadataCatalog([FromQuery(Name = "project_id")] long? projectId, CancellationToken cancellationToken = default)
    {
        if (!projectId.HasValue)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("project_id is required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.GetComponentMetadataCatalogAsync(User, projectId.Value, cancellationToken);
        return Ok(Success("Component Metadata Catalog", data ?? new ComponentMetadataCatalogDto()));
    }

    [HttpPost("components")]
    public async Task<IActionResult> CreateComponent([FromBody] SaveComponentRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !request.ProjectId.HasValue || string.IsNullOrWhiteSpace(request.Page) || string.IsNullOrWhiteSpace(request.Feature) || request.Steps.Count == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name, project_id, page, feature and steps are required", StatusCodes.Status400BadRequest));
        }

        var helperValidationError = await _appDataService.ValidateComponentHelperSyntaxAsync(User, request.Steps, cancellationToken);
        if (!string.IsNullOrWhiteSpace(helperValidationError))
        {
            return ValidationFailure("steps", helperValidationError);
        }

        var exists = await _appDataService.ComponentExistsAsync(User, request.ProjectId.Value, request.Page, request.Feature, null, cancellationToken);
        if (exists)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure("Component with the same Project, Page, and Feature already exists.", StatusCodes.Status409Conflict));
        }

        var data = await _appDataService.CreateComponentAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create component", StatusCodes.Status400BadRequest))
            : Ok(Success("Component Created Successfully", data));
    }

    [HttpPatch("components/{id:long}")]
    public async Task<IActionResult> UpdateComponent(long id, [FromBody] SaveComponentRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !request.ProjectId.HasValue || string.IsNullOrWhiteSpace(request.Page) || string.IsNullOrWhiteSpace(request.Feature) || request.Steps.Count == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name, project_id, page, feature and steps are required", StatusCodes.Status400BadRequest));
        }

        var helperValidationError = await _appDataService.ValidateComponentHelperSyntaxAsync(User, request.Steps, cancellationToken);
        if (!string.IsNullOrWhiteSpace(helperValidationError))
        {
            return ValidationFailure("steps", helperValidationError);
        }

        var exists = await _appDataService.ComponentExistsAsync(User, request.ProjectId.Value, request.Page, request.Feature, id, cancellationToken);
        if (exists)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure("Component with the same Project, Page, and Feature already exists.", StatusCodes.Status409Conflict));
        }

        var data = await _appDataService.UpdateComponentAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Component not found", StatusCodes.Status404NotFound))
            : Ok(Success("Component Updated Successfully", data));
    }

    [HttpPost("components/sync-linked-tests")]
    public async Task<IActionResult> SyncLinkedComponentTests([FromQuery(Name = "component_id")] long? componentId, CancellationToken cancellationToken = default)
    {
        if (componentId.HasValue && componentId.Value <= 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("component_id must be greater than zero", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.SyncComponentDatasetsAsync(User, componentId, cancellationToken);
        var message = componentId.HasValue
            ? "Linked Component Tests Synced Successfully"
            : "Linked Component Tests Backfill Completed Successfully";
        return Ok(Success(message, data));
    }

    [HttpDelete("components/{id:long}")]
    public async Task<IActionResult> DeleteComponent(long id, CancellationToken cancellationToken = default)
    {
        var deleted = await _appDataService.DeleteComponentAsync(User, id, cancellationToken);
        return deleted
            ? Ok(Success("Component Deleted Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status409Conflict, Failure("You Can't Delete Component, as it is being associated with Test", StatusCodes.Status409Conflict));
    }

    [HttpPost("components/bulk-delete")]
    public async Task<IActionResult> BulkDeleteComponents([FromBody] BulkDeleteComponentsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ComponentIds.Count == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("component_ids are required", StatusCodes.Status400BadRequest));
        }

        var deleted = await _appDataService.BulkDeleteComponentsAsync(User, request.ComponentIds, cancellationToken);
        return deleted
            ? Ok(Success("Components Deleted Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status409Conflict, Failure("One or more selected components are associated with tests", StatusCodes.Status409Conflict));
    }

    [HttpPost("components/status")]
    public async Task<IActionResult> UpdateComponentStatus([FromBody] UpdateEntityStatusRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await _appDataService.UpdateComponentStatusAsync(User, request.Id, request.Status, cancellationToken);
        return updated
            ? Ok(Success("Component Status Changed", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status404NotFound, Failure("Component not found", StatusCodes.Status404NotFound));
    }

    [HttpPost("import/components")]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> ImportComponents([FromForm(Name = "component_file")] IFormFile? componentFile, CancellationToken cancellationToken = default)
    {
        if (componentFile is null || componentFile.Length == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("component_file is required", StatusCodes.Status400BadRequest));
        }

        await using var stream = componentFile.OpenReadStream();
        var result = await _appDataService.ImportComponentsAsync(User, stream, cancellationToken);
        return Ok(Success("Component Saved Successfully", result));
    }

    [HttpGet("export/components")]
    public async Task<IActionResult> ExportComponents(
        [FromQuery] string? name,
        [FromQuery(Name = "page_name")] string? pageName,
        [FromQuery] string? feature,
        [FromQuery(Name = "project_id")] string? projectIds,
        [FromQuery(Name = "type_id")] string? typeIds,
        [FromQuery] bool? status,
        CancellationToken cancellationToken = default)
    {
        var payload = await _appDataService.ExportComponentsAsync(User, name, pageName, feature, projectIds, typeIds, status, cancellationToken);
        return File(payload, "text/csv", "components_export.csv");
    }

    [HttpGet("projects")]
    public async Task<IActionResult> Projects([FromQuery] string? q, [FromQuery(Name = "is_active")] bool? isActive, [FromQuery] int page = 1, [FromQuery] int limit = 200, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetProjectsAsync(User, q, isActive, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("Project List", data));
    }

    [HttpGet("projects/{id:long}")]
    public async Task<IActionResult> Project(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetProjectAsync(User, id, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Project not found", StatusCodes.Status404NotFound))
            : Ok(Success("Project Details", data));
    }

    [HttpPost("projects")]
    public async Task<IActionResult> CreateProject([FromBody] SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName) || string.IsNullOrWhiteSpace(request.Description) || !request.TypeId.HasValue || request.TypeId.Value <= 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("project_name, description and type_id are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.CreateProjectAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create project", StatusCodes.Status400BadRequest))
            : Ok(Success("Project Added Successfully", data));
    }

    [HttpPatch("projects/{id:long}")]
    public async Task<IActionResult> UpdateProject(long id, [FromBody] SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName) || string.IsNullOrWhiteSpace(request.Description) || !request.TypeId.HasValue || request.TypeId.Value <= 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("project_name, description and type_id are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.UpdateProjectAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Project not found", StatusCodes.Status404NotFound))
            : Ok(Success("Project Updated Successfully", data));
    }

    [HttpDelete("projects/{id:long}")]
    public async Task<IActionResult> DeleteProject(long id, CancellationToken cancellationToken = default)
    {
        var result = await _appDataService.DeleteProjectAsync(User, id, cancellationToken);
        return result switch
        {
            ProjectDeletionOutcome.Deleted => Ok(Success("Project Deleted Successfully", Array.Empty<object>())),
            ProjectDeletionOutcome.HasAttachedComponents => StatusCode(StatusCodes.Status409Conflict, Failure("You Can't Delete Project, as it is being associated with Component", StatusCodes.Status409Conflict)),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure("Project not found", StatusCodes.Status404NotFound))
        };
    }

    [HttpPost("projects/bulk-delete")]
    public async Task<IActionResult> BulkDeleteProjects([FromBody] BulkDeleteProjectsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProjectIds.Count == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("project_ids are required", StatusCodes.Status400BadRequest));
        }

        var deleted = await _appDataService.BulkDeleteProjectsAsync(User, request.ProjectIds, cancellationToken);
        return deleted
            ? Ok(Success("Project Deleted Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status409Conflict, Failure("You Can't Delete Project, as it is being associated with Component", StatusCodes.Status409Conflict));
    }

    [HttpPost("projects/status")]
    public async Task<IActionResult> UpdateProjectStatus([FromBody] UpdateEntityStatusRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await _appDataService.UpdateProjectStatusAsync(User, request.Id, request.Status, cancellationToken);
        return updated
            ? Ok(Success("Project Status Changed", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status404NotFound, Failure("Project not found", StatusCodes.Status404NotFound));
    }

    [HttpGet("dashboard/summary")]
    public async Task<IActionResult> DashboardSummary([FromQuery(Name = "project_id")] long? projectId, [FromQuery(Name = "date_from")] DateOnly? dateFrom, [FromQuery(Name = "date_to")] DateOnly? dateTo, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetDashboardSummaryAsync(User, projectId, dateFrom, dateTo, cancellationToken);
        return Ok(Success("Dashboard Summary", data));
    }

    [HttpGet("defects")]
    public async Task<IActionResult> Defects([FromQuery] string? q, [FromQuery(Name = "assigned_to")] long? assignedTo, [FromQuery(Name = "status_id")] long? statusId, [FromQuery(Name = "created_by")] long? createdBy, [FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetDefectsAsync(User, q, assignedTo, statusId, createdBy, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("Defects List", data));
    }

    [HttpGet("defects/{id:long}")]
    public async Task<IActionResult> Defect(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetDefectAsync(User, id, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Defect not found", StatusCodes.Status404NotFound))
            : Ok(Success("Defect Details", data));
    }

    [HttpPost("defects")]
    public async Task<IActionResult> CreateDefect([FromBody] CreateDefectRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return ValidationFailure("title", "The title field is required.");
        }

        if (!request.AssignedTo.HasValue || request.AssignedTo.Value <= 0)
        {
            return ValidationFailure("assigned_to", "The assigned_to field is required.");
        }

        if (!request.TestRunnerItemId.HasValue || request.TestRunnerItemId.Value <= 0)
        {
            return ValidationFailure("test_runner_item_id", "The test_runner_item_id field is required.");
        }

        var data = await _appDataService.CreateDefectAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create defect", StatusCodes.Status400BadRequest))
            : Ok(Success("Defect Created Successfully", data));
    }

    [HttpPost("defects/manual")]
    public async Task<IActionResult> CreateManualDefect([FromBody] CreateManualDefectRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return ValidationFailure("title", "The title field is required.");
        }

        if (!request.AssignedTo.HasValue || request.AssignedTo.Value <= 0)
        {
            return ValidationFailure("assigned_to", "The assigned_to field is required.");
        }

        if (!request.TestRunnerItemId.HasValue && string.IsNullOrWhiteSpace(request.Description))
        {
            return ValidationFailure("description", "The description field is required for standalone defects.");
        }

        if (request.TestRunnerItemId.HasValue && request.TestRunnerItemId.Value <= 0)
        {
            return ValidationFailure("test_runner_item_id", "The test_runner_item_id field must be greater than 0.");
        }

        var data = await _appDataService.CreateManualDefectAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create defect", StatusCodes.Status400BadRequest))
            : Ok(Success("Defect Created Successfully", data));
    }

    [HttpPost("defects/{id:long}/attachments")]
    public async Task<IActionResult> AddDefectAttachments(long id, CancellationToken cancellationToken = default)
    {
        if (!Request.HasFormContentType)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("multipart/form-data is required", StatusCodes.Status400BadRequest));
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        var files = CollectFormFiles(form, "files");
        if (files.Count == 0)
        {
            return ValidationFailure("files", "At least one attachment file is required.");
        }

        var defect = await _appDataService.GetDefectAsync(User, id, cancellationToken);
        if (defect is null)
        {
            return StatusCode(StatusCodes.Status404NotFound, Failure("Defect not found", StatusCodes.Status404NotFound));
        }

        if (CountVideoAttachments(defect.Attachments) + CountVideoFiles(files) > 1)
        {
            return ValidationFailure("files", "Only one video attachment is allowed per defect.");
        }

        IReadOnlyList<string> fileUrls;
        try
        {
            fileUrls = await SaveUploadedFilesAsync(files, ["defects"], ".bin", cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Failure(ex.Message, StatusCodes.Status503ServiceUnavailable));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Failure("Upload storage is not writable for the current runtime identity.", StatusCodes.Status503ServiceUnavailable));
        }

        var attachments = files.Zip(fileUrls, (file, url) => new DefectAttachmentFileInput
        {
            FileName = Path.GetFileName(file.FileName),
            Url = url,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
            FileSize = file.Length
        }).ToList();

        var data = await _appDataService.AddDefectAttachmentsAsync(User, id, attachments, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Defect not found", StatusCodes.Status404NotFound))
            : Ok(Success("Defect attachments saved", data));
    }

    [HttpDelete("defects/{id:long}/attachments/{attachmentId:long}")]
    public async Task<IActionResult> DeleteDefectAttachment(long id, long attachmentId, CancellationToken cancellationToken = default)
    {
        var defect = await _appDataService.GetDefectAsync(User, id, cancellationToken);
        if (defect is null)
        {
            return StatusCode(StatusCodes.Status404NotFound, Failure("Defect not found", StatusCodes.Status404NotFound));
        }

        var attachment = defect.Attachments.FirstOrDefault(item => item.Id == attachmentId);
        if (attachment is null)
        {
            return StatusCode(StatusCodes.Status404NotFound, Failure("Attachment not found", StatusCodes.Status404NotFound));
        }

        var data = await _appDataService.DeleteDefectAttachmentAsync(User, id, attachmentId, cancellationToken);
        if (data is null)
        {
            return StatusCode(StatusCodes.Status404NotFound, Failure("Attachment not found", StatusCodes.Status404NotFound));
        }

        TryDeleteUploadedFile(attachment.Url);
        return Ok(Success("Defect attachment deleted", data));
    }

    [HttpGet("defects/statuses")]
    public async Task<IActionResult> DefectStatuses(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetDefectStatusesAsync(cancellationToken);
        return Ok(Success("Defect Statuses", data));
    }

    [HttpGet("project-types")]
    public async Task<IActionResult> ProjectTypes(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetProjectTypesAsync(cancellationToken);
        return Ok(Success("Project Types List", data));
    }

    [HttpGet("system-settings/health-poll")]
    public async Task<IActionResult> HealthPollConfig(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetHealthPollConfigAsync(cancellationToken);
        return Ok(Success("Health Poll Config", data));
    }

    [HttpPost("system-settings")]
    public async Task<IActionResult> SaveSystemSetting([FromBody] SaveSystemSettingRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsAdmin(User))
        {
            return StatusCode(StatusCodes.Status403Forbidden, Failure("Unauthorized", StatusCodes.Status403Forbidden));
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("key is required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.SaveSystemSettingAsync(request.Key.Trim(), request.Value, cancellationToken);
        return Ok(Success("System Setting Saved", data));
    }

    [HttpPut("defects/{id:long}")]
    public async Task<IActionResult> UpdateDefect(long id, [FromBody] UpdateDefectRequest request, CancellationToken cancellationToken = default)
    {
        if (request.AssignedTo.HasValue && request.AssignedTo.Value <= 0)
        {
            return ValidationFailure("assigned_to", "The assigned_to field must be greater than 0.");
        }

        if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
        {
            return ValidationFailure("title", "The title field is required.");
        }

        if (request.Title is null && request.Description is null && request.Expected is null && request.Actual is null && !request.AssignedTo.HasValue)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("At least one defect field must be provided.", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.UpdateDefectAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Defect not found", StatusCodes.Status404NotFound))
            : Ok(Success("Defect Updated Successfully", data));
    }

    [HttpPost("defects/{id:long}/status/update")]
    public async Task<IActionResult> UpdateDefectStatus(long id, [FromBody] UpdateDefectStatusRequest request, CancellationToken cancellationToken = default)
    {
        if (request.StatusId <= 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("status_id is required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.UpdateDefectStatusAsync(User, id, request.StatusId, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Defect not found", StatusCodes.Status404NotFound))
            : Ok(Success("Defect Status Changed Successfully", data));
    }

    [HttpPut("testrunner/logs/toggle/failed/status/{id:long}")]
    public async Task<IActionResult> ToggleFailedStatus(long id, CancellationToken cancellationToken = default)
    {
        var request = await DeserializeOptionalRequestBodyAsync<ToggleFailedStatusRequest>(cancellationToken);
        var updated = await _appDataService.ToggleFailedStatusAsync(User, id, request?.Comment, cancellationToken);
        return updated
            ? Ok(Success("Test runner status updated", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status404NotFound, Failure("Test runner item not found", StatusCodes.Status404NotFound));
    }

    [HttpPut("testrunner/logs/{id:long}/favorite")]
    public async Task<IActionResult> ToggleTestRunnerFavorite(long id, [FromBody] ToggleTestRunnerFavoriteRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await _appDataService.ToggleTestRunnerFavoriteAsync(User, id, request.IsFavorite, cancellationToken);
        return updated
            ? Ok(Success("Test runner favorite updated", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status404NotFound, Failure("Test runner item not found", StatusCodes.Status404NotFound));
    }

    [HttpGet("keywords")]
    public async Task<IActionResult> Keywords([FromQuery(Name = "custom_keywords")] bool customKeywords = false, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetKeywordsAsync(User, customKeywords, cancellationToken);
        return Ok(Success("Component Keywords List", data));
    }

    [HttpPost("keywords")]
    public async Task<IActionResult> CreateKeywordAlias([FromBody] SaveKeywordAliasRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationFailure("name", "The name field is required.");
        }

        var data = await _appDataService.CreateGlobalKeywordAsync(request.Name.Trim(), cancellationToken);
        return data is null
            ? ValidationFailure("name", "The name has already been taken.")
            : Ok(Success("Global keyword created", data));
    }

    [HttpDelete("keywords")]
    public async Task<IActionResult> DeleteKeywordAlias([FromBody] DeleteKeywordsRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var id in request.KeywordsIds.Distinct())
        {
            var result = await _appDataService.DeleteGlobalKeywordAsync(id, cancellationToken);
            if (!result.Found)
            {
                return StatusCode(StatusCodes.Status404NotFound, Failure("Global keyword not found", StatusCodes.Status404NotFound));
            }

            if (result.InUse)
            {
                return StatusCode(StatusCodes.Status422UnprocessableEntity, Failure("Keyword is in use by components/tests. Remove references first.", StatusCodes.Status422UnprocessableEntity));
            }
        }

        return Ok(Success("Global keywords deleted", Array.Empty<object>()));
    }

    [HttpGet("before-after-steps")]
    public async Task<IActionResult> BeforeAfterSteps(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetBeforeAfterStepsAsync(cancellationToken);
        return Ok(Success("Component Before After Steps", data));
    }

    [HttpGet("before-after-step-admin")]
    public async Task<IActionResult> BeforeAfterStepAdmin(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetBeforeAfterStepAdminAsync(cancellationToken);
        return Ok(Success("Before/After steps", data));
    }

    [HttpPost("before-after-step-admin")]
    public async Task<IActionResult> CreateBeforeAfterStepAdmin([FromBody] SaveBeforeAfterStepAdminRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationFailure("name", "The name field is required.");
        }

        var result = await _appDataService.CreateBeforeAfterStepAdminAsync(
            request.Name.Trim(),
            request.Field ?? false,
            string.IsNullOrWhiteSpace(request.Type) ? null : request.Type.Trim(),
            request.Rules,
            cancellationToken);

        return result.Duplicate
            ? ValidationFailure("name", "The name has already been taken.")
            : Ok(Success("Before/After step created", result.Step!));
    }

    [HttpPut("before-after-step-admin/{id:long}")]
    public async Task<IActionResult> UpdateBeforeAfterStepAdmin(long id, [FromBody] SaveBeforeAfterStepAdminRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationFailure("name", "The name field is required.");
        }

        var result = await _appDataService.UpdateBeforeAfterStepAdminAsync(
            id,
            request.Name.Trim(),
            request.Field ?? false,
            string.IsNullOrWhiteSpace(request.Type) ? null : request.Type.Trim(),
            request.Rules,
            cancellationToken);

        if (!result.Found)
        {
            return StatusCode(StatusCodes.Status404NotFound, Failure("Before/After step not found", StatusCodes.Status404NotFound));
        }

        return result.Duplicate
            ? ValidationFailure("name", "The name has already been taken.")
            : Ok(Success("Before/After step updated", result.Step!));
    }

    [HttpDelete("before-after-step-admin/{id:long}")]
    public async Task<IActionResult> DeleteBeforeAfterStepAdmin(long id, CancellationToken cancellationToken = default)
    {
        var result = await _appDataService.DeleteBeforeAfterStepAdminAsync(id, cancellationToken);
        if (!result.Found)
        {
            return StatusCode(StatusCodes.Status404NotFound, Failure("Before/After step not found", StatusCodes.Status404NotFound));
        }

        if (result.InUse)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, Failure("Helper is in use by components/tests. Remove references first.", StatusCodes.Status422UnprocessableEntity));
        }

        return Ok(Success("Before/After step deleted", Array.Empty<object>()));
    }

    [HttpGet("variables/types")]
    public async Task<IActionResult> VariableTypes(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetVariableTypesAsync(cancellationToken);
        return Ok(Success("Variable Types", data));
    }

    [HttpGet("custom/variables")]
    public async Task<IActionResult> CustomVariables([FromQuery] string? scope, [FromQuery(Name = "test_case_id")] long? testCaseId, [FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetCustomVariablesAsync(User, scope, testCaseId, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("All Custom Variables", data));
    }

    [HttpPost("custom/variables")]
    public async Task<IActionResult> CreateCustomVariable([FromBody] SaveCustomVariableRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.VariableId <= 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name and variable_id are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.CreateCustomVariableAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create custom variable", StatusCodes.Status400BadRequest))
            : Ok(Success("Custom Variable Added", data));
    }

    [HttpPut("custom/variables/{id:long}")]
    public async Task<IActionResult> UpdateCustomVariable(long id, [FromBody] SaveCustomVariableRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.VariableId <= 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name and variable_id are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.UpdateCustomVariableAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Custom Variable not found", StatusCodes.Status404NotFound))
            : Ok(Success("Custom Variable Updated", data));
    }

    [HttpDelete("custom/variables/{id:long}")]
    public async Task<IActionResult> DeleteCustomVariable(long id, CancellationToken cancellationToken = default)
    {
        var deleted = await _appDataService.DeleteCustomVariableAsync(User, id, cancellationToken);
        return deleted
            ? Ok(Success("Custom Variable Deleted", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status404NotFound, Failure("Custom Variable not found", StatusCodes.Status404NotFound));
    }

    [HttpGet("configuration-variables")]
    public async Task<IActionResult> ConfigurationVariables([FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetConfigurationVariablesAsync(User, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("All Configuration Variables", data));
    }

    [HttpPost("configuration-variables")]
    public async Task<IActionResult> CreateConfigurationVariable([FromBody] SaveConfigurationVariableRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name and description are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.CreateConfigurationVariableAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create configuration variable", StatusCodes.Status400BadRequest))
            : Ok(Success("Configuration Variable Created Successfully", data));
    }

    [HttpPut("configuration-variables/{id:long}")]
    public async Task<IActionResult> UpdateConfigurationVariable(long id, [FromBody] SaveConfigurationVariableRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name and description are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.UpdateConfigurationVariableAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Configuration Variable not found", StatusCodes.Status404NotFound))
            : Ok(Success("Configuration Variable Updated Successfully", data));
    }

    [HttpDelete("configuration-variables/{id:long}")]
    public async Task<IActionResult> DeleteConfigurationVariable(long id, CancellationToken cancellationToken = default)
    {
        var deleted = await _appDataService.DeleteConfigurationVariableAsync(User, id, cancellationToken);
        return deleted
            ? Ok(Success("Configuration Variable Deleted Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status404NotFound, Failure("Configuration Variable not found", StatusCodes.Status404NotFound));
    }

    [HttpGet("configurations")]
    public async Task<IActionResult> Configurations([FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetConfigurationsAsync(User, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("All Configuration Variables", data));
    }

    [HttpPost("configurations")]
    public async Task<IActionResult> CreateConfiguration([FromBody] SaveConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name and description are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.CreateConfigurationAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create configuration", StatusCodes.Status400BadRequest))
            : Ok(Success("Configuration Created Successfully", data));
    }

    [HttpPut("configurations/{id:long}")]
    public async Task<IActionResult> UpdateConfiguration(long id, [FromBody] SaveConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name and description are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.UpdateConfigurationAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Configuration not found", StatusCodes.Status404NotFound))
            : Ok(Success("Configuration Updated Successfully", data));
    }

    [HttpDelete("configurations/{id:long}")]
    public async Task<IActionResult> DeleteConfiguration(long id, CancellationToken cancellationToken = default)
    {
        var deleted = await _appDataService.DeleteConfigurationAsync(User, id, cancellationToken);
        return deleted
            ? Ok(Success("Configuration Deleted Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status404NotFound, Failure("Configuration not found", StatusCodes.Status404NotFound));
    }

    [HttpPost("configurations/add-to-test-suite")]
    public async Task<IActionResult> AssignConfigurationsToSuite([FromBody] AssignConfigurationsToSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.AssignConfigurationsToSuiteAsync(User, request, cancellationToken);
        return Ok(Success("Configurations assigned to suite", data));
    }

    [HttpGet("execution-device-pools")]
    public async Task<IActionResult> ExecutionDevicePools(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetExecutionDevicePoolsAsync(User, cancellationToken);
        return Ok(Success("Device Pools", data));
    }

    [HttpGet("execution-devices")]
    public async Task<IActionResult> ExecutionDevices([FromQuery(Name = "pool_id")] long? poolId, [FromQuery(Name = "poolId")] long? poolIdAlt, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetExecutionDevicesAsync(User, poolId ?? poolIdAlt, cancellationToken);
        return Ok(Success("Devices", data));
    }

    [HttpGet("execution-schedules")]
    public async Task<IActionResult> ExecutionSchedules(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetExecutionSchedulesAsync(User, cancellationToken);
        return Ok(Success("Execution Schedules", data));
    }

    [HttpGet("execution-queue")]
    public async Task<IActionResult> ExecutionQueues(
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] string? priority,
        [FromQuery(Name = "schedule_id")] long? scheduleId,
        [FromQuery(Name = "run_target")] string? runTarget,
        [FromQuery(Name = "test_plan_id")] long? testPlanId,
        [FromQuery(Name = "test_plan_item_id")] long? testPlanItemId,
        CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetExecutionQueuesAsync(User, status, source, priority, scheduleId, runTarget, testPlanId, testPlanItemId, cancellationToken);
        return Ok(Success("Execution Queue", data));
    }

    [HttpGet("execution-queue/{id:long}")]
    public async Task<IActionResult> ExecutionQueue(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetExecutionQueueAsync(User, id, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Execution Queue not found", StatusCodes.Status404NotFound))
            : Ok(Success("Execution Queue", data));
    }

    [HttpGet("integrations/connections")]
    public async Task<IActionResult> IntegrationConnections([FromQuery(Name = "project_id")] long? projectId, [FromQuery] string? provider, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetIntegrationConnectionsAsync(User, projectId, provider, cancellationToken);
        return Ok(Success("Integration connections", data));
    }

    [HttpPost("integrations/connections")]
    public async Task<IActionResult> CreateIntegrationConnection([FromBody] SaveIntegrationConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedIntegrationProvider(request.Provider) || string.IsNullOrWhiteSpace(request.Name))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("provider and name are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.CreateIntegrationConnectionAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create integration connection.", StatusCodes.Status400BadRequest))
            : StatusCode(StatusCodes.Status201Created, new ApiResponse<IntegrationConnectionDto>(true, StatusCodes.Status201Created, "Integration connection created.", data));
    }

    [HttpPut("integrations/connections/{id:long}")]
    public async Task<IActionResult> UpdateIntegrationConnection(long id, [FromBody] SaveIntegrationConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.UpdateIntegrationConnectionAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Integration connection not found.", StatusCodes.Status404NotFound))
            : Ok(Success("Integration connection updated.", data));
    }

    [HttpGet("integrations/jobs")]
    public async Task<IActionResult> IntegrationJobs([FromQuery(Name = "connection_id")] long? connectionId, [FromQuery] string? status, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var data = await _appDataService.GetIntegrationJobsAsync(User, connectionId, status, normalizedLimit, cancellationToken);
        return Ok(Success("Integration jobs", data));
    }

    [HttpPost("integrations/sync")]
    public async Task<IActionResult> QueueIntegrationSync([FromBody] QueueIntegrationSyncRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ConnectionId <= 0 || !IsSupportedIntegrationEntityType(request.EntityType) || request.InternalId <= 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("connection_id, entity_type and internal_id are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.QueueIntegrationSyncAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Integration connection not found.", StatusCodes.Status404NotFound))
            : StatusCode(StatusCodes.Status201Created, new ApiResponse<IntegrationJobDto>(true, StatusCodes.Status201Created, "Integration sync queued.", data));
    }

    [HttpPost("integrations/sync/bulk")]
    public async Task<IActionResult> QueueIntegrationBulkSync([FromBody] QueueIntegrationBulkSyncRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedIntegrationEntityType(request.EntityType) || request.InternalIds.Count == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("entity_type and internal_ids are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.QueueIntegrationBulkSyncAsync(User, request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<IntegrationBulkQueueResultDto>(true, StatusCodes.Status201Created, "Bulk integration sync queued.", data));
    }

    [HttpGet("integrations/connections/{connectionId:long}/mappings/{entityType}")]
    public async Task<IActionResult> IntegrationMapping(long connectionId, string entityType, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedIntegrationEntityType(entityType))
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, Failure("Unsupported entity type.", StatusCodes.Status422UnprocessableEntity));
        }

        var data = await _appDataService.GetIntegrationMappingAsync(User, connectionId, entityType, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Integration connection not found.", StatusCodes.Status404NotFound))
            : Ok(Success("Integration mapping", data));
    }

    [HttpPut("integrations/connections/{connectionId:long}/mappings/{entityType}")]
    public async Task<IActionResult> SaveIntegrationMapping(long connectionId, string entityType, [FromBody] SaveIntegrationMappingRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedIntegrationEntityType(entityType))
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, Failure("Unsupported entity type.", StatusCodes.Status422UnprocessableEntity));
        }

        var data = await _appDataService.SaveIntegrationMappingAsync(User, connectionId, entityType, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Integration connection not found.", StatusCodes.Status404NotFound))
            : Ok(Success("Integration mapping saved.", data));
    }

    [HttpPost("integrations/jobs/{id:long}/retry")]
    public async Task<IActionResult> RetryIntegrationJob(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.RetryIntegrationJobAsync(User, id, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Integration job not found.", StatusCodes.Status404NotFound))
            : Ok(Success("Integration job retried.", data));
    }

    [HttpPost("integrations/jobs/replay-failed")]
    public async Task<IActionResult> ReplayFailedIntegrationJobs([FromBody] ReplayFailedIntegrationJobsRequest request, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.ReplayFailedIntegrationJobsAsync(User, request.ConnectionId, Math.Clamp(request.Limit ?? 100, 1, 500), cancellationToken);
        return Ok(Success("Failed integration jobs replayed.", data));
    }

    [HttpGet("integrations/operations/summary")]
    public async Task<IActionResult> IntegrationOperationsSummary([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetIntegrationOperationsSummaryAsync(User, Math.Clamp(days, 1, 90), cancellationToken);
        return Ok(Success("Integration operations summary", data));
    }

    [HttpGet("integrations/operations/health")]
    public async Task<IActionResult> IntegrationHealth([FromQuery(Name = "pending_sla_minutes")] int pendingSlaMinutes = 2, [FromQuery(Name = "failure_rate_threshold")] double failureRateThreshold = 0.15d, [FromQuery(Name = "window_minutes")] int windowMinutes = 60, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetIntegrationHealthAsync(User, Math.Clamp(pendingSlaMinutes, 1, 1440), Math.Clamp(failureRateThreshold, 0d, 1d), Math.Clamp(windowMinutes, 5, 1440), cancellationToken);
        return Ok(Success("Integration health", data));
    }

    [HttpGet("test-plans")]
    public async Task<IActionResult> TestPlans([FromQuery] string? q, [FromQuery(Name = "plan_type")] string? planType, [FromQuery(Name = "plan_status")] string? planStatus, [FromQuery(Name = "project_id")] long? projectId, [FromQuery(Name = "is_active")] bool? isActive, [FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestPlansAsync(User, q, planType, planStatus, projectId, isActive, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("Test Plans", data));
    }

    [HttpGet("test-plans/{id:long}")]
    public async Task<IActionResult> TestPlan(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestPlanAsync(User, id, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status422UnprocessableEntity, Failure("Test Plan Not Found", StatusCodes.Status422UnprocessableEntity))
            : Ok(Success("Test Plan Details", data));
    }

    [HttpPost("test-plans")]
    public async Task<IActionResult> CreateTestPlan([FromBody] SaveTestPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !request.ProjectId.HasValue || !request.OwnerUserId.HasValue)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name, project_id and owner_user_id are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.CreateTestPlanAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create test plan", StatusCodes.Status400BadRequest))
            : Ok(Success("Test Plan Saved Successfully", data));
    }

    [HttpPut("test-plans/{id:long}")]
    public async Task<IActionResult> UpdateTestPlan(long id, [FromBody] SaveTestPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !request.ProjectId.HasValue || !request.OwnerUserId.HasValue)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name, project_id and owner_user_id are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.UpdateTestPlanAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Test Plan Not Found", StatusCodes.Status404NotFound))
            : Ok(Success("Test Plan Updated Successfully", data));
    }

    [HttpDelete("test-plans/{id:long}")]
    public async Task<IActionResult> DeleteTestPlan(long id, CancellationToken cancellationToken = default)
    {
        var deleted = await _appDataService.DeleteTestPlanAsync(User, id, cancellationToken);
        return deleted
            ? Ok(Success("Test Plan Deleted Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status403Forbidden, Failure("You Cannot Delete This Plan Because it has Test Suites attached", StatusCodes.Status403Forbidden));
    }

    [HttpPost("test-plans/status")]
    public async Task<IActionResult> UpdateTestPlanStatus([FromBody] UpdateEntityStatusRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await _appDataService.UpdateTestPlanStatusAsync(User, request.Id, request.Status, cancellationToken);
        return updated
            ? Ok(Success("Test Plan Status Changed", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status404NotFound, Failure("Test Plan Not Found", StatusCodes.Status404NotFound));
    }

    [HttpGet("test-plans-items")]
    public async Task<IActionResult> TestPlanItems([FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestPlanItemsAsync(User, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("Test Plan Items", data));
    }

    [HttpGet("test-plans-items/{id:long}")]
    public async Task<IActionResult> TestPlanItemsForPlan(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestPlanItemsForPlanAsync(User, id, cancellationToken);
        return Ok(Success("Test Plan Items", data));
    }

    [HttpPost("test-plans-items")]
    public async Task<IActionResult> CreateTestPlanItem([FromBody] SaveTestPlanItemRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !request.TestPlanId.HasValue)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name and test_plan_id are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.CreateTestPlanItemAsync(User, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to create test plan item", StatusCodes.Status400BadRequest))
            : Ok(Success("Test Plan Item Created Successfully", data));
    }

    [HttpPut("test-plans-items/{id:long}")]
    public async Task<IActionResult> UpdateTestPlanItem(long id, [FromBody] SaveTestPlanItemRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("name is required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.UpdateTestPlanItemAsync(User, id, request, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Test Plan Item Not Found", StatusCodes.Status404NotFound))
            : Ok(Success("Test Plan Item Updated Successfully", data));
    }

    [HttpDelete("test-plans-items/{id:long}")]
    public async Task<IActionResult> DeleteTestPlanItem(long id, CancellationToken cancellationToken = default)
    {
        var deleted = await _appDataService.DeleteTestPlanItemAsync(User, id, cancellationToken);
        return deleted
            ? Ok(Success("Test Plan Item Deleted Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status403Forbidden, Failure("You Cannot Delete This Item Because it's have Test Cases. Please Delete Test Cases First", StatusCodes.Status403Forbidden));
    }

    [HttpGet("get/testsuites/against/testplanitems/{id:long}")]
    public async Task<IActionResult> SuitesForPlanItem(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetSuitesForPlanItemAsync(User, id, cancellationToken);
        return Ok(Success("Test Suites Against Test Plan Item", data));
    }

    [HttpGet("get/testsuites/against/testplanitems/{id:long}/light")]
    public async Task<IActionResult> SuitesForPlanItemLight(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetSuitesForPlanItemLightAsync(User, id, cancellationToken);
        return Ok(Success("Test Suites Against Test Plan Item", data));
    }

    [HttpPost("add/testsuites/against/testplanitems")]
    public async Task<IActionResult> AddSuitesToPlanItem([FromBody] AddSuitesToPlanItemRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TestPlanItemId <= 0 || request.TestDesignIds.Count == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("test_plan_item_id and test_design_ids are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.AddSuitesToPlanItemAsync(User, request, cancellationToken);
        return Ok(Success("Test Suites Added Successfully", data));
    }

    [HttpDelete("remove/testsuites/against/testplanitems")]
    public async Task<IActionResult> RemoveSuitesFromPlanItem([FromBody] RemovePlanItemSuitesRequest request, CancellationToken cancellationToken = default)
    {
        var deleted = await _appDataService.RemoveSuitesFromPlanItemAsync(User, request.Ids, cancellationToken);
        return deleted
            ? Ok(Success("Test Suites Removed Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to remove test suites", StatusCodes.Status400BadRequest));
    }

    [HttpPost("sort/testsuites/against/testplanitems")]
    public async Task<IActionResult> SortSuitesForPlanItem([FromBody] RemovePlanItemSuitesRequest request, CancellationToken cancellationToken = default)
    {
        var sorted = await _appDataService.SortSuitesForPlanItemAsync(User, request.Ids, cancellationToken);
        return sorted
            ? Ok(Success("Test Suites Sorted Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to sort test suites", StatusCodes.Status400BadRequest));
    }

    [HttpPut("update/users/against/testplanitems/suite")]
    public async Task<IActionResult> UpdatePlanItemSuiteUsers([FromBody] UpdatePlanItemSuiteUsersRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await _appDataService.UpdatePlanItemSuiteUsersAsync(User, request, cancellationToken);
        return updated
            ? Ok(Success("Users Updated Successfully", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to update users", StatusCodes.Status400BadRequest));
    }

    [HttpPost("update/testsuites/status/not-started")]
    public async Task<IActionResult> ChangeSuiteToNotStarted([FromBody] ChangeSuiteToNotStartedRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await _appDataService.ChangeSuiteToNotStartedAsync(User, request, cancellationToken);
        return updated
            ? Ok(Success("Test Suite Status Updated", Array.Empty<object>()))
            : StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to update test suite status", StatusCodes.Status400BadRequest));
    }

    [HttpGet("testsuite/states")]
    public async Task<IActionResult> TestSuiteStates(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestSuiteStatesAsync(cancellationToken);
        return Ok(Success("Test Suites States", data));
    }

    [HttpGet("test-suite/tags")]
    public async Task<IActionResult> GetSharedTestSuiteTags(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetSharedTestSuiteTagsAsync(User, cancellationToken);
        return Ok(Success("Shared Test Suite Tags", data));
    }

    [HttpPatch("test-suite/tags/rename")]
    public async Task<IActionResult> RenameSharedTestSuiteTag([FromBody] RenameSharedTagRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OldTag) || string.IsNullOrWhiteSpace(request.NewTag))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("old_tag and new_tag are required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.RenameSharedTestSuiteTagAsync(User, request.OldTag, request.NewTag, cancellationToken);
        return Ok(Success("Shared Test Suite Tag Renamed", data));
    }

    [HttpDelete("test-suite/tags")]
    public async Task<IActionResult> DeleteSharedTestSuiteTag([FromQuery(Name = "tag")] string? tag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("tag is required", StatusCodes.Status400BadRequest));
        }

        var data = await _appDataService.DeleteSharedTestSuiteTagAsync(User, tag, cancellationToken);
        return Ok(Success("Shared Test Suite Tag Deleted", data));
    }

    [HttpGet("testsuite/project")]
    public async Task<IActionResult> TestSuitesForProject([FromQuery] string? q, [FromQuery] string? tags, [FromQuery(Name = "project_id")] long? projectId, [FromQuery(Name = "test_state_id")] long? testStateId, [FromQuery(Name = "test_suite_type")] int? testSuiteType, [FromQuery(Name = "test_plan_item_id")] long? testPlanItemId, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestSuitesAsync(User, q, tags, projectId, testStateId, testSuiteType, testPlanItemId, 1, 0, true, cancellationToken);
        return Ok(Success("Test Design Aginst Test", data));
    }

    [HttpGet("test-suite")]
    public async Task<IActionResult> TestSuites([FromQuery] string? q, [FromQuery] string? tags, [FromQuery(Name = "project_id")] long? projectId, [FromQuery(Name = "test_state_id")] long? testStateId, [FromQuery(Name = "test_suite_type")] int? testSuiteType, [FromQuery(Name = "test_plan_item_id")] long? testPlanItemId, [FromQuery] int page = 1, [FromQuery] int limit = 20, [FromQuery] int? light = 0, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestSuitesAsync(User, q, tags, projectId, testStateId, testSuiteType, testPlanItemId, NormalizePage(page), NormalizeLimit(limit), light == 1, cancellationToken);
        return Ok(Success("Test Design Aginst Test", data));
    }

    [HttpGet("test-suite/export")]
    public async Task<IActionResult> ExportTestSuites([FromQuery] string? q, [FromQuery] string? tags, [FromQuery(Name = "project_id")] long? projectId, [FromQuery(Name = "test_state_id")] long? testStateId, [FromQuery(Name = "test_suite_type")] int? testSuiteType, CancellationToken cancellationToken = default)
    {
        var payload = await _appDataService.ExportTestSuitesMatrixAsync(User, q, tags, projectId, testStateId, testSuiteType, cancellationToken);
        return File(payload, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "tests_export.xlsx");
    }

    [HttpPost("test-suite/validate-matrix")]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> ValidateTestSuitesMatrix([FromForm(Name = "test_file")] IFormFile? testFile, CancellationToken cancellationToken = default)
    {
        if (testFile is null || testFile.Length == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("test_file is required", StatusCodes.Status400BadRequest));
        }

        try
        {
            await using var stream = testFile.OpenReadStream();
            var result = await _appDataService.ValidateTestSuitesMatrixAsync(User, stream, cancellationToken);
            return Ok(Success("Validation passed", result));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure(ex.Message, StatusCodes.Status400BadRequest));
        }
    }

    [HttpPost("test-suite/import-matrix")]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> ImportTestSuitesMatrix([FromForm(Name = "test_file")] IFormFile? testFile, CancellationToken cancellationToken = default)
    {
        if (testFile is null || testFile.Length == 0)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("test_file is required", StatusCodes.Status400BadRequest));
        }

        try
        {
            await using var stream = testFile.OpenReadStream();
            var result = await _appDataService.ImportTestSuitesMatrixAsync(User, stream, cancellationToken);
            return Ok(Success("Test suites imported successfully", result));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure(ex.Message, StatusCodes.Status400BadRequest));
        }
    }

    [HttpGet("test-suite/{id:long}/full")]
    public async Task<IActionResult> TestSuiteFull(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestSuiteFullAsync(User, id, cancellationToken);
        return Ok(Success("Test Suite Full Details", data));
    }

    [HttpPost("test-suite/{id:long}/edit-access")]
    public IActionResult AcquireTestSuiteEditAccess(long id, [FromBody] TestSuiteEditSessionRequest request)
    {
        try
        {
            var data = _testSuiteEditSessionService.AcquireOrRefresh(User, id, request);
            return Ok(Success("Test suite edit access", data));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure(ex.Message, StatusCodes.Status400BadRequest));
        }
    }

    [HttpPost("test-suite/{id:long}/edit-access/release")]
    public IActionResult ReleaseTestSuiteEditAccess(long id, [FromBody] TestSuiteEditSessionRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return StatusCode(StatusCodes.Status400BadRequest, Failure("A session_id is required.", StatusCodes.Status400BadRequest));
            }

            _testSuiteEditSessionService.Release(User, id, request.SessionId);
            return Ok(Success("Test suite edit access released", Array.Empty<object>()));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure(ex.Message, StatusCodes.Status400BadRequest));
        }
    }

    [HttpGet("test-suite/{testDesignId:long}/components/{testComponentId:long}/datasets")]
    public async Task<IActionResult> TestSuiteComponentDatasets(long testDesignId, long testComponentId, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestComponentDatasetsAsync(User, testDesignId, testComponentId, cancellationToken);
        return data is null
            ? StatusCode(StatusCodes.Status404NotFound, Failure("Component datasets not found", StatusCodes.Status404NotFound))
            : Ok(Success("Component datasets", data));
    }

    [HttpPut("test-suite/{testDesignId:long}/components/{testComponentId:long}/datasets/{datasetId:long}")]
    public async Task<IActionResult> UpdateTestSuiteComponentDataset(long testDesignId, long testComponentId, long datasetId, [FromBody] SaveTestSuiteDatasetRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _appDataService.UpdateTestComponentDatasetAsync(User, testDesignId, testComponentId, datasetId, request, cancellationToken);
            return data is null
                ? StatusCode(StatusCodes.Status404NotFound, Failure("Component dataset not found", StatusCodes.Status404NotFound))
                : Ok(Success("Component dataset saved", data));
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    [HttpPut("test-suite/{testDesignId:long}/components/{testComponentId:long}/datasets")]
    public async Task<IActionResult> UpdateTestSuiteComponentDatasets(long testDesignId, long testComponentId, [FromBody] SaveTestSuiteComponentDatasetsRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _appDataService.UpdateTestComponentDatasetsAsync(User, testDesignId, testComponentId, request, cancellationToken);
            return data is null
                ? StatusCode(StatusCodes.Status404NotFound, Failure("Component datasets not found", StatusCodes.Status404NotFound))
                : Ok(Success("Component datasets saved", data));
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    [HttpPost("test-suite/{testDesignId:long}/components/{testComponentId:long}/datasets/ensure")]
    public async Task<IActionResult> EnsureTestSuiteComponentDataset(long testDesignId, long testComponentId, [FromBody] EnsureTestComponentDatasetRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _appDataService.EnsureTestComponentDatasetAsync(User, testDesignId, testComponentId, request, cancellationToken);
            return data is null
                ? StatusCode(StatusCodes.Status404NotFound, Failure("Component dataset could not be initialized", StatusCodes.Status404NotFound))
                : Ok(Success("Component dataset initialized", data));
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    [HttpPost("test-suite/{testDesignId:long}/components/datasets/ensure")]
    public async Task<IActionResult> EnsureTestSuiteDatasetForComponent(long testDesignId, [FromBody] EnsureTestComponentDatasetRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _appDataService.EnsureTestComponentDatasetForSuiteAsync(User, testDesignId, request, cancellationToken);
            return data is null
                ? StatusCode(StatusCodes.Status404NotFound, Failure("Component dataset could not be initialized", StatusCodes.Status404NotFound))
                : Ok(Success("Component dataset initialized", data));
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    [HttpPost("test-suite")]
    public async Task<IActionResult> CreateTestSuite([FromBody] SaveTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateTestSuiteRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.CreateTestSuiteAsync(User, request, cancellationToken);
        return result.Outcome switch
        {
            SaveTestSuiteOutcome.Saved => Ok(Success("Test Suite Created Successfully", result.TestSuite!)),
            SaveTestSuiteOutcome.InvalidReference => ValidationFailure(result.ErrorField ?? "details", result.ErrorMessage ?? "The given data was invalid."),
            _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to save test suite", StatusCodes.Status400BadRequest))
        };
    }

    [HttpPut("test-suite/{id:long}")]
    public async Task<IActionResult> UpdateTestSuite(long id, [FromBody] SaveTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateTestSuiteRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            var result = await _appDataService.UpdateTestSuiteAsync(User, id, request, cancellationToken);
            return result.Outcome switch
            {
                SaveTestSuiteOutcome.Saved => Ok(Success("Test Suite Created Successfully", result.TestSuite!)),
                SaveTestSuiteOutcome.NotFound => StatusCode(StatusCodes.Status404NotFound, Failure("Test Suite Design Not Found", StatusCodes.Status404NotFound)),
                SaveTestSuiteOutcome.InvalidReference => ValidationFailure(result.ErrorField ?? "details", result.ErrorMessage ?? "The given data was invalid."),
                _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to update test suite", StatusCodes.Status400BadRequest))
            };
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    [HttpPost("test-suite/{id:long}/clone")]
    public async Task<IActionResult> CloneTestSuite(long id, [FromBody] CloneTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateCloneTestSuiteRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.CloneTestSuiteAsync(User, id, request, cancellationToken);
        return result.Outcome switch
        {
            SaveTestSuiteOutcome.Saved => Ok(Success("Test Suite Cloned Successfully", result.TestSuite!)),
            SaveTestSuiteOutcome.NotFound => StatusCode(StatusCodes.Status404NotFound, Failure("Test Suite Design Not Found", StatusCodes.Status404NotFound)),
            SaveTestSuiteOutcome.InvalidReference => ValidationFailure(result.ErrorField ?? "details", result.ErrorMessage ?? "The given data was invalid."),
            _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to clone test suite", StatusCodes.Status400BadRequest))
        };
    }

    [HttpPatch("test-suite/{id:long}/details")]
    public async Task<IActionResult> UpdateTestSuiteDetails(long id, [FromBody] SaveTestSuiteDetailsRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateTestSuiteRequest(new SaveTestSuiteRequest
        {
            Details = request,
            DesignedComponents = []
        });
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            var result = await _appDataService.UpdateTestSuiteDetailsAsync(User, id, request, cancellationToken);
            return result.Outcome switch
            {
                SaveTestSuiteOutcome.Saved => Ok(Success("Test suite details saved", result.Details!)),
                SaveTestSuiteOutcome.NotFound => StatusCode(StatusCodes.Status404NotFound, Failure("Test Suite Design Not Found", StatusCodes.Status404NotFound)),
                SaveTestSuiteOutcome.InvalidReference => ValidationFailure(result.ErrorField ?? "details", result.ErrorMessage ?? "The given data was invalid."),
                _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to update test suite details", StatusCodes.Status400BadRequest))
            };
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    [HttpPut("test-suite/{id:long}/flow")]
    public async Task<IActionResult> UpdateTestSuiteFlow(long id, [FromBody] SaveTestSuiteFlowRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateTestSuiteFlowRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            var result = await _appDataService.UpdateTestSuiteFlowAsync(User, id, request, cancellationToken);
            return result.Outcome switch
            {
                SaveTestSuiteOutcome.Saved => Ok(Success("Test suite flow saved", result.Components)),
                SaveTestSuiteOutcome.NotFound => StatusCode(StatusCodes.Status404NotFound, Failure("Test Suite Design Not Found", StatusCodes.Status404NotFound)),
                SaveTestSuiteOutcome.InvalidReference => ValidationFailure(result.ErrorField ?? "components", result.ErrorMessage ?? "The given data was invalid."),
                _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to update test suite flow", StatusCodes.Status400BadRequest))
            };
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    [HttpDelete("test-suite/{id:long}")]
    public async Task<IActionResult> DeleteTestSuite(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _appDataService.DeleteTestSuiteAsync(User, id, cancellationToken);
            return result.Outcome switch
            {
                DeleteTestSuiteOutcome.Deleted => Ok(Success("TestSuite Deleted Successfully", Array.Empty<object>())),
                DeleteTestSuiteOutcome.ActivePlansBlocked => ValidationFailure("test_plans", result.ErrorMessage ?? "Can not delete Test Suite because active plans were found."),
                _ => StatusCode(StatusCodes.Status404NotFound, Failure("Test Suite Design Not Found", StatusCodes.Status404NotFound))
            };
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    [HttpPost("get/testsuites/steps")]
    public async Task<IActionResult> GetTestSuiteSteps([FromBody] GetTestSuiteStepsRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateGetTestSuiteStepsRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var data = await _appDataService.GetTestSuiteStepsAsync(User, request, false, cancellationToken);
        return Ok(Success("Test Runner", data));
    }

    [HttpPost("automation/get/testsuites/steps")]
    public async Task<IActionResult> GetAutomationTestSuiteSteps([FromBody] GetTestSuiteStepsRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateGetTestSuiteStepsRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var data = await _appDataService.GetTestSuiteStepsAsync(User, request, true, cancellationToken);
        return Ok(Success("Test Runner", data));
    }

    [HttpPost("save/testsuites/steps/status")]
    public async Task<IActionResult> SaveTestSuiteStepStatus(CancellationToken cancellationToken = default)
    {
        var parsed = await ReadStepStatusRequestAsync(cancellationToken);
        if (parsed.Request is null)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("Invalid request payload", StatusCodes.Status400BadRequest));
        }

        var validation = ValidateStepStatusRequest(parsed.Request);
        if (validation is not null)
        {
            return validation;
        }

        IReadOnlyList<string> imagePaths;
        try
        {
            imagePaths = parsed.Files.Count == 0
                ? Array.Empty<string>()
                : await SaveUploadedFilesAsync(parsed.Files, "images", cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Failure(ex.Message, StatusCodes.Status503ServiceUnavailable));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Failure("Upload storage is not writable for the current runtime identity.", StatusCodes.Status503ServiceUnavailable));
        }

        var result = await _appDataService.SaveTestRunnerStepStatusAsync(User, parsed.Request, imagePaths, cancellationToken);
        return result.Outcome switch
        {
            SaveTestRunnerStepStatusOutcome.Saved => Ok(Success("Test Runner Step Saved", result.Payload ?? new TestRunnerPayloadDto())),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure(result.ErrorMessage ?? "Test Runner Item Not Found", StatusCodes.Status404NotFound))
        };
    }

    [HttpPost("bulk/testsuites/steps/status")]
    public async Task<IActionResult> BulkTestSuiteStepsStatus([FromBody] SaveTestRunnerStepStatusRequest request, CancellationToken cancellationToken = default)
    {
        return await SaveBulkTestSuiteStepsStatusAsync(request, includeStatusV2Payload: false, cancellationToken);
    }

    [HttpPost("bulk/testsuites/steps/status-v2")]
    public async Task<IActionResult> BulkTestSuiteStepsStatusV2([FromBody] SaveTestRunnerStepStatusRequest request, CancellationToken cancellationToken = default)
    {
        return await SaveBulkTestSuiteStepsStatusAsync(request, includeStatusV2Payload: true, cancellationToken);
    }

    private async Task<IActionResult> SaveBulkTestSuiteStepsStatusAsync(SaveTestRunnerStepStatusRequest request, bool includeStatusV2Payload, CancellationToken cancellationToken)
    {
        var validation = ValidateStepStatusRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.SaveTestRunnerStepStatusAsync(User, request, Array.Empty<string>(), cancellationToken);
        object responsePayload = includeStatusV2Payload
            ? new
            {
                summary = result.Summary ?? new TestRunnerStepStatusSummaryDto(),
                active = result.Payload ?? new TestRunnerPayloadDto()
            }
            : result.Payload ?? new TestRunnerPayloadDto();

        return result.Outcome switch
        {
            SaveTestRunnerStepStatusOutcome.Saved => Ok(Success("Test Runner Step Saved", responsePayload)),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure(result.ErrorMessage ?? "Test Runner Item Not Found", StatusCodes.Status404NotFound))
        };
    }

    [HttpPost("save/close/testsuites")]
    public async Task<IActionResult> SaveAndCloseTestSuite([FromBody] SaveAndCloseTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateSaveAndCloseRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.SaveAndCloseTestSuiteAsync(User, request, cancellationToken);
        return result.Outcome switch
        {
            RunnerItemMutationOutcome.Saved => Ok(Success("Test Runner Item Saved", Array.Empty<object>())),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure(result.ErrorMessage ?? "Test Runner Item Not Found", StatusCodes.Status404NotFound))
        };
    }

    [HttpPost("pause/testsuites")]
    public async Task<IActionResult> PauseTestSuite([FromBody] PauseTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidatePauseTestSuiteRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var result = await _appDataService.PauseTestSuiteAsync(User, request, cancellationToken);
        return result.Outcome switch
        {
            RunnerItemMutationOutcome.Saved => Ok(Success("Test Runner Item Paused", Array.Empty<object>())),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure(result.ErrorMessage ?? "Test Runner Item Not Found", StatusCodes.Status404NotFound))
        };
    }

    [HttpPost("upload/testsuites/video")]
    public async Task<IActionResult> UploadTestSuiteVideo(CancellationToken cancellationToken = default)
    {
        if (!Request.HasFormContentType)
        {
            return StatusCode(StatusCodes.Status400BadRequest, Failure("multipart/form-data is required", StatusCodes.Status400BadRequest));
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        var testRunnerId = ParseInt64(form["test_runner_id"]);
        var testSuiteId = ParseInt64(form["test_suite_id"]);
        if (!testRunnerId.HasValue)
        {
            return ValidationFailure("test_runner_id", "The test_runner_id field is required.");
        }

        if (!testSuiteId.HasValue)
        {
            return ValidationFailure("test_suite_id", "The test_suite_id field is required.");
        }

        var files = CollectFormFiles(form, "videos");
        if (files.Count == 0)
        {
            return ValidationFailure("videos", "The videos field is required.");
        }

        IReadOnlyList<string> videoPaths;
        try
        {
            videoPaths = await SaveUploadedFilesAsync(files, "videos", cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Failure(ex.Message, StatusCodes.Status503ServiceUnavailable));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Failure("Upload storage is not writable for the current runtime identity.", StatusCodes.Status503ServiceUnavailable));
        }

        var result = await _appDataService.UploadTestSuiteVideoAsync(User, testRunnerId.Value, testSuiteId.Value, videoPaths, cancellationToken);
        return result.Outcome switch
        {
            RunnerItemMutationOutcome.Saved => Ok(Success("Test Suite Video Saved Successfully", Array.Empty<object>())),
            _ => StatusCode(StatusCodes.Status404NotFound, Failure(result.ErrorMessage ?? "Test Runner Item Not Found", StatusCodes.Status404NotFound))
        };
    }

    [HttpGet("test-suite/{id:long}/children")]
    public async Task<IActionResult> TestSuiteChildren(long id, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestSuiteChildrenAsync(User, id, cancellationToken);
        return Ok(Success("Child test suites", data));
    }

    [HttpGet("testrunner/statuses")]
    public async Task<IActionResult> TestRunnerStatuses(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestRunnerStatusesAsync(cancellationToken);
        return Ok(Success("Test Runner Statues List", data));
    }

    [HttpGet("testrunner/logs/items")]
    public async Task<IActionResult> TestRunnerLogItems([FromQuery(Name = "test_plan")] long? testPlanId, [FromQuery(Name = "test_plan_item")] long? testPlanItemId, [FromQuery(Name = "test_suite")] string? testSuite, [FromQuery(Name = "run_by")] long? runBy, [FromQuery] string? status, [FromQuery(Name = "created_at")] string? createdAt, [FromQuery(Name = "test_runner_ids")] string? testRunnerIds, [FromQuery(Name = "include_in_progress")] bool includeInProgress = false, [FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetTestRunnerLogItemsAsync(User, testPlanId, testPlanItemId, testSuite, runBy, status, createdAt, testRunnerIds, includeInProgress, NormalizePage(page), NormalizeLimit(limit), cancellationToken);
        return Ok(Success("Test Runner Log Items", data));
    }

    [HttpGet("global-keywords")]
    public async Task<IActionResult> GlobalKeywords(CancellationToken cancellationToken = default)
    {
        var data = await _appDataService.GetGlobalKeywordsAsync(cancellationToken);
        return Ok(Success("Global keywords", data));
    }

    [HttpPost("global-keywords")]
    public async Task<IActionResult> CreateGlobalKeyword([FromBody] SaveGlobalKeywordRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationFailure("name", "The name field is required.");
        }

        var data = await _appDataService.CreateGlobalKeywordAsync(request.Name.Trim(), cancellationToken);
        return data is null
            ? ValidationFailure("name", "The name has already been taken.")
            : Ok(Success("Global keyword created", data));
    }

    [HttpPut("global-keywords/{id:long}")]
    public async Task<IActionResult> UpdateGlobalKeyword(long id, [FromBody] SaveGlobalKeywordRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationFailure("name", "The name field is required.");
        }

        var data = await _appDataService.UpdateGlobalKeywordAsync(id, request.Name.Trim(), cancellationToken);
        if (data is not null)
        {
            return Ok(Success("Global keyword updated", data));
        }

        var exists = (await _appDataService.GetGlobalKeywordsAsync(cancellationToken)).Any(row => row.Id == id);
        return exists
            ? ValidationFailure("name", "The name has already been taken.")
            : StatusCode(StatusCodes.Status404NotFound, Failure("Global keyword not found", StatusCodes.Status404NotFound));
    }

    [HttpDelete("global-keywords/{id:long}")]
    public async Task<IActionResult> DeleteGlobalKeyword(long id, CancellationToken cancellationToken = default)
    {
        var result = await _appDataService.DeleteGlobalKeywordAsync(id, cancellationToken);
        if (!result.Found)
        {
            return StatusCode(StatusCodes.Status404NotFound, Failure("Global keyword not found", StatusCodes.Status404NotFound));
        }

        if (result.InUse)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, Failure("Keyword is in use by components/tests. Remove references first.", StatusCodes.Status422UnprocessableEntity));
        }

        return Ok(Success("Global keyword deleted", Array.Empty<object>()));
    }

    [HttpPost("save/override/value")]
    public async Task<IActionResult> SaveOverrideValue([FromBody] SaveOverrideValueRequest request, CancellationToken cancellationToken = default)
    {
        if (request.DatasetId <= 0)
        {
            return ValidationFailure("dataset_id", "The dataset_id field is required.");
        }

        if (request.StepId <= 0)
        {
            return ValidationFailure("step_id", "The step_id field is required.");
        }

        if (request.Reset != true && string.IsNullOrWhiteSpace(request.Value))
        {
            return ValidationFailure("value", "The value field is required when reset is not present.");
        }

        try
        {
            var result = await _appDataService.SaveOverrideValueAsync(User, request, cancellationToken);
            return result.Outcome switch
            {
                SaveOverrideValueOutcome.Saved => Ok(Success("Override String Validated", Array.Empty<object>())),
                SaveOverrideValueOutcome.NotFound => StatusCode(StatusCodes.Status422UnprocessableEntity, Failure(result.ErrorMessage ?? "DataSet Step No Found", StatusCodes.Status422UnprocessableEntity)),
                SaveOverrideValueOutcome.ValidationFailed => StatusCode(StatusCodes.Status422UnprocessableEntity, Failure(result.ErrorMessage ?? "Override validation failed", StatusCodes.Status422UnprocessableEntity)),
                _ => StatusCode(StatusCodes.Status400BadRequest, Failure("Unable to save override value", StatusCodes.Status400BadRequest))
            };
        }
        catch (TestSuiteEditLockException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, Failure(ex.Message, StatusCodes.Status409Conflict));
        }
    }

    private static ApiResponse<T> Success<T>(string message, T data)
    {
        return new ApiResponse<T>(true, StatusCodes.Status200OK, message, data);
    }

    private static ApiResponse<object> Failure(string message, int statusCode)
    {
        return new ApiResponse<object>(false, statusCode, message, null);
    }

    private IActionResult? ValidateRoleRequest(SaveRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return RoleValidationFailure("name", "The name field is required.");
        }

        if (request.Permissions.Count == 0)
        {
            return RoleValidationFailure("permissions", "The permissions field is required.");
        }

        return null;
    }

    private IActionResult RoleValidationFailure(string field, string message)
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

    private IActionResult? ValidateTestSuiteRequest(SaveTestSuiteRequest request)
    {
        if (request.Details is null)
        {
            return ValidationFailure("details", "The details field is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Details.Title))
        {
            return ValidationFailure("details.title", "The title field is required.");
        }

        if (!request.Details.TestStateId.HasValue || request.Details.TestStateId.Value <= 0)
        {
            return ValidationFailure("details.test_state_id", "The test_state_id field is required.");
        }

        if (!request.Details.TestSuiteType.HasValue || (request.Details.TestSuiteType.Value != 1 && request.Details.TestSuiteType.Value != 2))
        {
            return ValidationFailure("details.test_suite_type", "The selected test_suite_type is invalid.");
        }

        if (request.DesignedComponents is null)
        {
            return ValidationFailure("designed_components", "The designed_components field is required.");
        }

        return null;
    }

    private IActionResult? ValidateTestSuiteFlowRequest(SaveTestSuiteFlowRequest request)
    {
        if (request.Components is null)
        {
            return ValidationFailure("components", "The components field is required.");
        }

        for (var index = 0; index < request.Components.Count; index += 1)
        {
            var component = request.Components[index];
            if (!component.ComponentId.HasValue || component.ComponentId.Value <= 0)
            {
                return ValidationFailure($"components.{index}.component_id", "The component_id field is required.");
            }

            if (!component.ProjectId.HasValue || component.ProjectId.Value <= 0)
            {
                return ValidationFailure($"components.{index}.project_id", "The project_id field is required.");
            }
        }

        var persistedIds = request.Components
            .Where(component => component.TestComponentId.HasValue)
            .Select(component => component.TestComponentId!.Value)
            .ToArray();
        if (persistedIds.Length != persistedIds.Distinct().Count())
        {
            return ValidationFailure("components.test_component_id", "Duplicate test_component_id values are not allowed.");
        }

        return null;
    }

    private IActionResult? ValidateCloneTestSuiteRequest(CloneTestSuiteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return ValidationFailure("title", "The title field is required.");
        }

        return null;
    }

    private IActionResult? ValidateGetTestSuiteStepsRequest(GetTestSuiteStepsRequest request)
    {
        if (request.TestSuites.Count == 0)
        {
            return ValidationFailure("test_suites", "The test_suites field is required.");
        }

        if (!request.TestPlanItemId.HasValue && request.InvokedViaTests != true)
        {
            return ValidationFailure("invoked_via_tests", "The invoked_via_tests field is required when test_plan_item_id is not present.");
        }

        return null;
    }

    private IActionResult? ValidateStepStatusRequest(SaveTestRunnerStepStatusRequest request)
    {
        if (request.BulkUpdate == true)
        {
            if (!request.TestRunnerId.HasValue && !request.TestPlanItemId.HasValue)
            {
                return ValidationFailure("test_runner_id", "The test_runner_id field is required when test_plan_item_id is not present.");
            }

            if (request.TestSuiteId == 0)
            {
                return ValidationFailure("test_suite_id", "The test_suite_id field is required.");
            }

            if (!request.IsPassed.HasValue)
            {
                return ValidationFailure("is_passed", "The is_passed field is required.");
            }

            return null;
        }

        if (!request.TestRunnerId.HasValue)
        {
            return ValidationFailure("test_runner_id", "The test_runner_id field is required.");
        }

        if (request.TestSuiteId == 0)
        {
            return ValidationFailure("test_suite_id", "The test_suite_id field is required.");
        }

        if (request.Steps.Count == 0)
        {
            return ValidationFailure("steps", "The steps field is required.");
        }

        if (request.Steps.Any(step => step.ResolvedId <= 0))
        {
            return ValidationFailure("steps.id", "Each step id is required.");
        }

        if (request.Steps.Any(step => !step.IsPassed.HasValue))
        {
            return ValidationFailure("steps.is_passed", "Each step is_passed value is required.");
        }

        return null;
    }

    private IActionResult? ValidateSaveAndCloseRequest(SaveAndCloseTestSuiteRequest request)
    {
        if (request.TestRunnerId <= 0)
        {
            return ValidationFailure("test_runner_id", "The test_runner_id field is required.");
        }

        if (request.TestSuiteId == 0)
        {
            return ValidationFailure("test_suite_id", "The test_suite_id field is required.");
        }

        return null;
    }

    private IActionResult? ValidatePauseTestSuiteRequest(PauseTestSuiteRequest request)
    {
        if (request.TestRunnerId <= 0)
        {
            return ValidationFailure("test_runner_id", "The test_runner_id field is required.");
        }

        if (request.TestSuiteId == 0)
        {
            return ValidationFailure("test_suite_id", "The test_suite_id field is required.");
        }

        return null;
    }

    private IActionResult? ValidateCreateUserRequest(SaveUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email) || !request.RoleId.HasValue)
        {
            return ValidationFailure("Name, Email, and Role are required.");
        }

        if (string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.PasswordConfirmation))
        {
            return ValidationFailure("Password and confirmation are required.");
        }

        if (!string.Equals(request.Password, request.PasswordConfirmation, StringComparison.Ordinal))
        {
            return ValidationFailure("Passwords must match.");
        }

        return null;
    }

    private IActionResult? ValidateUpdateUserRequest(SaveUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email) || !request.RoleId.HasValue)
        {
            return ValidationFailure("Name, Email, and Role are required.");
        }

        return null;
    }

    private IActionResult ValidationFailure(string message)
    {
        return StatusCode(StatusCodes.Status422UnprocessableEntity, new
        {
            message
        });
    }

    private static int NormalizePage(int page) => page > 0 ? page : 1;

    private static int NormalizeLimit(int limit) => limit > 0 ? limit : 20;

    private static bool IsSupportedIntegrationEntityType(string? entityType)
    {
        return string.Equals(entityType, "test_case", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entityType, "test_plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entityType, "test_run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entityType, "defect", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedIntegrationProvider(string? provider)
    {
        return string.Equals(provider, "azure_devops", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "jira", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdmin(ClaimsPrincipal principal)
    {
        return principal.Claims
            .Where(claim => claim.Type == ClaimTypes.Role || claim.Type == "role")
            .Select(claim => claim.Value)
            .Any(value => value.Contains("admin", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(SaveTestRunnerStepStatusRequest? Request, IReadOnlyList<IFormFile> Files)> ReadStepStatusRequestAsync(CancellationToken cancellationToken)
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            return (
                new SaveTestRunnerStepStatusRequest
                {
                    TestRunnerId = ParseInt64(form["test_runner_id"]),
                    TestPlanItemId = ParseInt64(form["test_plan_item_id"]),
                    TestSuiteId = ParseInt64(form["test_suite_id"]) ?? 0,
                    Steps = DeserializeJson<IReadOnlyList<SaveTestRunnerStepRequest>>(form["steps"]) ?? [],
                    BulkUpdate = ParseBoolean(form["bulk_update"]),
                    IsPassed = ParseBoolean(form["is_passed"])
                },
                CollectFormFiles(form, "images"));
        }

        var request = await JsonSerializer.DeserializeAsync<SaveTestRunnerStepStatusRequest>(Request.Body, RequestJsonOptions, cancellationToken);
        return (request, Array.Empty<IFormFile>());
    }

    private Task<IReadOnlyList<string>> SaveUploadedFilesAsync(IReadOnlyList<IFormFile> files, string folderName, CancellationToken cancellationToken)
    {
        var fallbackExtension = folderName == "videos" ? ".mp4" : ".png";
        return SaveUploadedFilesAsync(files, ["test-runner", folderName], fallbackExtension, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> SaveUploadedFilesAsync(IReadOnlyList<IFormFile> files, IReadOnlyList<string> folderSegments, string fallbackExtension, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var uploadsRootPath = ResolveUploadsRootPath();
        if (!Directory.Exists(uploadsRootPath))
        {
            throw new InvalidOperationException($"Upload storage path is not provisioned: {uploadsRootPath}");
        }

        var clientSegment = User.FindFirst("client_id")?.Value;
        if (string.IsNullOrWhiteSpace(clientSegment))
        {
            clientSegment = "0";
        }

        var relativeSegments = folderSegments.Concat([clientSegment]).ToArray();
        var targetDirectory = Path.Combine(uploadsRootPath, Path.Combine(relativeSegments));
        Directory.CreateDirectory(targetDirectory);

        var results = new List<string>(files.Count);
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = fallbackExtension;
            }

            var fileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var fullPath = Path.Combine(targetDirectory, fileName);
            await using var stream = System.IO.File.Create(fullPath);
            await file.CopyToAsync(stream, cancellationToken);
            results.Add($"/uploads/{string.Join("/", relativeSegments)}/{fileName}");
        }

        return results;
    }

    private string ResolveUploadsRootPath()
    {
        if (!string.IsNullOrWhiteSpace(_uploadStorageSettings.RootPath))
        {
            return Path.GetFullPath(_uploadStorageSettings.RootPath);
        }

        throw new InvalidOperationException("Upload storage is not configured. Set Uploads:RootPath to a provisioned DFS path.");
    }

    private static int CountVideoFiles(IReadOnlyList<IFormFile> files)
    {
        return files.Count(file => !string.IsNullOrWhiteSpace(file.ContentType) && file.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
    }

    private static int CountVideoAttachments(IReadOnlyList<DefectAttachmentDto> attachments)
    {
        return attachments.Count(attachment => !string.IsNullOrWhiteSpace(attachment.ContentType) && attachment.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
    }

    private void TryDeleteUploadedFile(string? relativeUrl)
    {
        var path = ResolveUploadedFilePath(relativeUrl);
        if (path is null || !System.IO.File.Exists(path))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup after metadata removal.
        }
    }

    private string? ResolveUploadedFilePath(string? relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl) || !relativeUrl.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var uploadsRootPath = ResolveUploadsRootPath();
            var relativePath = relativeUrl["/uploads/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(uploadsRootPath, relativePath));
            return fullPath.StartsWith(uploadsRootPath, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<IFormFile> CollectFormFiles(IFormCollection form, string prefix)
    {
        return form.Files
            .Where(file => file.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || file.Name.StartsWith(prefix + "[", StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => $"{file.FileName}|{file.Length}|{file.ContentType}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static T? DeserializeJson<T>(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, RequestJsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private async Task<T?> DeserializeOptionalRequestBodyAsync<T>(CancellationToken cancellationToken)
    {
        if ((Request.ContentLength ?? 0) == 0)
        {
            return default;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(Request.Body, RequestJsonOptions, cancellationToken);
        }
        catch
        {
            return default;
        }
    }

    private static long? ParseInt64(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool? ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
    }
}
