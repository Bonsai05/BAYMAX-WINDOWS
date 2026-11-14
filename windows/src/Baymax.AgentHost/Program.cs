using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;
using Baymax.Contracts;
using Baymax.Core.Models;
using Baymax.Core.Planning;
using Baymax.Core.Persistence;
using Baymax.Core.Tasks;
using Baymax.AgentHost;
using Microsoft.AspNetCore.Authentication.Negotiate;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services
    .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("MobilePairingRegistration", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("MobilePairingStatus", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 40,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("MobileTasks", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var configuredPort = builder.Configuration["Baymax:Port"] ?? "5080";
if (!int.TryParse(configuredPort, out var port) || port is < 1 or > 65535)
{
    throw new InvalidOperationException(
        $"Baymax:Port must be a valid TCP port from 1 to 65535; received '{configuredPort}'.");
}

var mobilePairingOptions = MobilePairingOptions.FromConfiguration(builder.Configuration, port);
var mobileTaskExecutionOptions = new MobileTaskExecutionOptions(
    builder.Configuration.GetValue("Baymax:Mobile:AllowDirectTaskExecution", false));

var configuredSimulationDuration = builder.Configuration["Baymax:SimulationDurationSeconds"] ?? "15";
if (!int.TryParse(configuredSimulationDuration, out var simulationDurationSeconds)
    || simulationDurationSeconds is < 1 or > 3600)
{
    throw new InvalidOperationException(
        $"Baymax:SimulationDurationSeconds must be from 1 to 3600; received '{configuredSimulationDuration}'.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    if (mobilePairingOptions.Enabled)
    {
        options.Listen(mobilePairingOptions.BindAddress!, mobilePairingOptions.Port, listenOptions =>
            listenOptions.UseHttps(mobilePairingOptions.Certificate!));
    }
});

var configuredDatabasePath = builder.Configuration["Baymax:DatabasePath"];
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
if (string.IsNullOrWhiteSpace(localAppData))
{
    throw new InvalidOperationException("The Windows Local Application Data directory could not be resolved.");
}

var databasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
    ? Path.Combine(localAppData, "Baymax", "data", "baymax.db")
    : configuredDatabasePath;

var configuredWorkspacePath = builder.Configuration["Baymax:WorkspacePath"];
var workspaceRoot = string.IsNullOrWhiteSpace(configuredWorkspacePath)
    ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "WORKENIVORNMENT")
    : configuredWorkspacePath;
var permissionConfiguration = builder.Configuration.GetSection("Baymax:Permissions");
var executionPolicy = new ExecutionPolicy(
    workspaceRoot,
    permissionConfiguration.GetValue("Read", true),
    permissionConfiguration.GetValue("Write", true),
    permissionConfiguration.GetValue("Execute", true));

var llamaConfiguration = builder.Configuration.GetSection("Baymax:LlamaCpp");
var llamaOptions = new LlamaCppOptions(
    llamaConfiguration.GetValue("Enabled", false),
    llamaConfiguration["ExecutablePath"],
    llamaConfiguration["ModelPath"],
    llamaConfiguration["ExecutableSha256"],
    llamaConfiguration["ModelSha256"],
    ReadLlamaInteger("Port", 8081, 1, 65535),
    ReadLlamaInteger("ContextSize", 8192, 512, 131072),
    ReadOptionalLlamaInteger("GpuLayers", 0, 999),
    ReadLlamaInteger("MaxRestarts", 3, 0, 10),
    TimeSpan.FromSeconds(ReadLlamaInteger("StartupTimeoutSeconds", 300, 10, 1800)),
    llamaConfiguration.GetValue("ManageProcess", true));
if (llamaOptions.Enabled && llamaOptions.Port == port)
{
    throw new InvalidOperationException("Baymax and llama.cpp must use different TCP ports.");
}

if (llamaOptions.Enabled && !llamaOptions.ManageProcess
    && (llamaOptions.ExecutablePath is not null
        || llamaOptions.ModelPath is not null
        || llamaOptions.ExecutableSha256 is not null
        || llamaOptions.ModelSha256 is not null))
{
    throw new InvalidOperationException(
        "Baymax:LlamaCpp:ExecutablePath, ModelPath, and their SHA-256 values must be omitted when ManageProcess is false.");
}

var database = new BaymaxDatabase(databasePath);
await database.InitializeAsync();
builder.Services.AddSingleton(database);
var taskEngine = new TaskEngine(new TaskStore(database), TimeSpan.FromSeconds(simulationDurationSeconds));
await taskEngine.RecoverInterruptedTasksAsync();
builder.Services.AddSingleton(taskEngine);
builder.Services.AddSingleton(mobileTaskExecutionOptions);
builder.Services.AddHostedService<SimulatedTaskWorker>();
builder.Services.AddSingleton(llamaOptions);
builder.Services.AddSingleton(new LlamaCppClient(
    llamaOptions,
    new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromMinutes(5)
    }));
builder.Services.AddSingleton<LlamaCppSupervisor>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<LlamaCppSupervisor>());
builder.Services.AddSingleton<TaskPlanner>();
builder.Services.AddSingleton(executionPolicy);
builder.Services.AddSingleton(new FileSystemActionExecutor(executionPolicy.WorkspaceRoot));
builder.Services.AddSingleton(new PlanApprovalStore(database));
builder.Services.AddSingleton(mobilePairingOptions);
builder.Services.AddSingleton<DevicePairingStore>();
builder.Services.AddSingleton<MobilePairingService>();
builder.Services.AddSingleton<MobileDeviceAuthenticator>();

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var isMobileListener = mobilePairingOptions.Enabled
        && context.Connection.LocalPort == mobilePairingOptions.Port;
    var isMobilePairingRoute = context.Request.Path.StartsWithSegments("/mobile/pairing");
    var isMobileTaskRoute = context.Request.Path.Equals("/mobile/tasks", StringComparison.OrdinalIgnoreCase)
        || context.Request.Path.StartsWithSegments("/mobile/tasks");
    var isMobileRoute = isMobilePairingRoute || isMobileTaskRoute;
    if ((isMobileListener && !isMobileRoute) || (!isMobileListener && isMobileRoute))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (isMobileListener
        && context.Request.Path.Equals("/mobile/pairing/register", StringComparison.OrdinalIgnoreCase)
        && context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is
            { IsReadOnly: false } requestBodyLimit)
    {
        requestBodyLimit.MaxRequestBodySize = 4096;
    }
    else if (isMobileListener
        && context.Request.Path.Equals("/mobile/tasks", StringComparison.OrdinalIgnoreCase)
        && context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is
            { IsReadOnly: false } taskRequestBodyLimit)
    {
        taskRequestBodyLimit.MaxRequestBodySize = 8192;
    }

    await next();
});
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

app.MapGet("/health", async (CancellationToken cancellationToken) =>
{
    await database.CheckHealthAsync(cancellationToken);
    return Results.Ok(new HealthResponse("healthy", version, DateTimeOffset.UtcNow));
});

app.MapPost("/desktop/shutdown", (
    HttpRequest request,
    IConfiguration configuration,
    IHostApplicationLifetime applicationLifetime) =>
{
    var expectedToken = configuration["Baymax:DesktopControlToken"];
    var suppliedToken = request.Headers["X-Baymax-Desktop-Control"].ToString();
    if (string.IsNullOrEmpty(expectedToken)
        || string.IsNullOrEmpty(suppliedToken)
        || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expectedToken),
            System.Text.Encoding.UTF8.GetBytes(suppliedToken)))
    {
        return Results.Unauthorized();
    }

    request.HttpContext.Response.OnCompleted(() =>
    {
        applicationLifetime.StopApplication();
        return Task.CompletedTask;
    });
    return Results.Accepted();
}).RequireAuthorization();

app.MapPost("/tasks", async (SubmitTaskRequest? request, CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest();
    }

    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.UserInstruction) || request.UserInstruction.Length > 4000)
    {
        errors["userInstruction"] = ["Must contain 1 to 4000 non-whitespace characters."];
    }

    if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
    {
        errors["idempotencyKey"] = ["Must contain 1 to 200 non-whitespace characters."];
    }

    if (request.OriginDeviceId?.Length > 128)
    {
        errors["originDeviceId"] = ["Must not exceed 128 characters."];
    }

    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    try
    {
        var task = await taskEngine.SubmitAsync(request, cancellationToken);
        return Results.AcceptedAtRoute("GetTask", new { taskId = task.TaskId }, task);
    }
    catch (IdempotencyConflictException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Idempotency key conflict",
            detail: exception.Message);
    }
}).WithName("SubmitTask");

app.MapGet("/tasks/{taskId:guid}", async (Guid taskId, CancellationToken cancellationToken) =>
{
    var task = await taskEngine.GetAsync(taskId, cancellationToken);
    return task is null ? Results.NotFound() : Results.Ok(task);
}).WithName("GetTask");

app.MapGet("/desktop/mobile/tasks", async (
    TaskEngine engine,
    CancellationToken cancellationToken) =>
    Results.Ok(await engine.GetRecentMobileTasksAsync(50, cancellationToken)))
    .RequireAuthorization();

app.MapPost("/tasks/{taskId:guid}/cancel", async (Guid taskId, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await taskEngine.CancelAsync(taskId, cancellationToken));
    }
    catch (TaskNotFoundException)
    {
        return Results.NotFound();
    }
    catch (TaskStateConflictException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Task cannot be cancelled",
            detail: exception.Message);
    }
});

app.MapGet("/model/status", (LlamaCppSupervisor supervisor) => Results.Ok(supervisor.Status));

app.MapGet("/pairing/status", (MobilePairingService pairing) => Results.Ok(pairing.GetStatus()))
    .RequireAuthorization();

app.MapPost("/pairing/invitations", async (
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await pairing.CreateInvitationAsync(cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Mobile pairing is unavailable",
            detail: exception.Message);
    }
}).RequireAuthorization();

app.MapDelete("/pairing/invitations/{invitationId:guid}", async (
    Guid invitationId,
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
{
    var cancelled = await pairing.CancelInvitationAsync(invitationId, cancellationToken);
    return cancelled ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/pairing/requests", async (
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
    Results.Ok(await pairing.GetPendingRequestsAsync(cancellationToken)))
    .RequireAuthorization();

app.MapPost("/pairing/requests/{requestId:guid}/confirm", async (
    Guid requestId,
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
{
    try
    {
        var deviceId = await pairing.ConfirmRequestAsync(requestId, cancellationToken);
        return deviceId is null ? Results.NotFound() : Results.Ok(new { deviceId });
    }
    catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
    {
        return Results.Conflict(new { detail = "This Android device key is already paired." });
    }
}).RequireAuthorization();

app.MapDelete("/pairing/requests/{requestId:guid}", async (
    Guid requestId,
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
{
    var rejected = await pairing.RejectRequestAsync(requestId, cancellationToken);
    return rejected ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/pairing/devices", async (
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
    Results.Ok(await pairing.GetDevicesAsync(cancellationToken)))
    .RequireAuthorization();

app.MapDelete("/pairing/devices/{deviceId:guid}", async (
    Guid deviceId,
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
{
    var revoked = await pairing.RevokeDeviceAsync(deviceId, cancellationToken);
    return revoked ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/mobile/pairing/register", async (
    RegisterPairingDeviceRequest? request,
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest();
    }

    try
    {
        return Results.Ok(await pairing.RegisterDeviceAsync(request, cancellationToken));
    }
    catch (PairingRejectedException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Pairing request rejected",
            detail: exception.Message);
    }
    catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
    {
        return Results.Conflict(new { detail = "This Android device key is already paired." });
    }
}).RequireRateLimiting("MobilePairingRegistration");

app.MapGet("/mobile/pairing/requests/{requestId:guid}", async (
    Guid requestId,
    HttpRequest request,
    MobilePairingService pairing,
    CancellationToken cancellationToken) =>
{
    var pollingSecret = request.Headers["X-Baymax-Pairing-Poll-Secret"].ToString();
    if (pollingSecret.Length is < 40 or > 64)
    {
        return Results.Unauthorized();
    }

    var status = await pairing.GetRequestStatusAsync(requestId, pollingSecret, cancellationToken);
    return status is null ? Results.Unauthorized() : Results.Ok(status);
}).RequireRateLimiting("MobilePairingStatus");

app.MapPost("/mobile/tasks", async (
    HttpRequest request,
    MobileDeviceAuthenticator authenticator,
    TaskEngine taskEngine,
    MobileTaskExecutionOptions mobileOptions,
    ExecutionPolicy executionPolicy,
    CancellationToken cancellationToken) =>
{
    AuthenticatedMobileRequest? authenticated;
    try
    {
        authenticated = await authenticator.AuthenticateAsync(request, cancellationToken);
    }
    catch (MobileRequestTooLargeException)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    if (authenticated is null)
    {
        return Results.Unauthorized();
    }

    if (!mobileOptions.Enabled || !executionPolicy.ExecuteEnabled)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Direct Android execution is disabled",
            detail: "Enable direct Android task execution and Execute permission in Baymax Desktop Settings.");
    }

    if (!executionPolicy.ReadEnabled && !executionPolicy.WriteEnabled)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Workspace actions are disabled",
            detail: "Enable Read or Write permission in Baymax Desktop Settings.");
    }

    MobileSubmitTaskRequest? mobileRequest;
    try
    {
        mobileRequest = System.Text.Json.JsonSerializer.Deserialize<MobileSubmitTaskRequest>(
            authenticated.Body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest();
    }

    if (mobileRequest is null
        || string.IsNullOrWhiteSpace(mobileRequest.UserInstruction)
        || mobileRequest.UserInstruction.Length > 4000
        || string.IsNullOrWhiteSpace(mobileRequest.IdempotencyKey)
        || mobileRequest.IdempotencyKey.Length > 200)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = ["User instruction must contain 1 to 4000 non-whitespace characters and idempotency key must contain 1 to 200 non-whitespace characters."]
        });
    }

    try
    {
        var task = await taskEngine.SubmitAsync(
            new SubmitTaskRequest(
                mobileRequest.UserInstruction,
                mobileRequest.IdempotencyKey,
                authenticated.DeviceId.ToString("D")),
            cancellationToken);
        return Results.Accepted($"/mobile/tasks/{task.TaskId:D}", task);
    }
    catch (IdempotencyConflictException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Idempotency key conflict",
            detail: exception.Message);
    }
}).RequireRateLimiting("MobileTasks");

app.MapGet("/mobile/tasks/{taskId:guid}", async (
    Guid taskId,
    HttpRequest request,
    MobileDeviceAuthenticator authenticator,
    TaskEngine taskEngine,
    CancellationToken cancellationToken) =>
{
    request.HttpContext.Response.Headers["Cache-Control"] = "no-store";
    AuthenticatedMobileRequest? authenticated;
    try
    {
        authenticated = await authenticator.AuthenticateAsync(request, cancellationToken);
    }
    catch (MobileRequestTooLargeException)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    if (authenticated is null)
    {
        return Results.Unauthorized();
    }

    var task = await taskEngine.GetAsync(taskId, cancellationToken);
    return task is null || task.OriginDeviceId != authenticated.DeviceId.ToString("D")
        ? Results.NotFound()
        : Results.Ok(task);
}).RequireRateLimiting("MobileTasks");

app.MapPost("/model/chat/completions", async (
    ModelCompletionRequest? request,
    LlamaCppClient modelClient,
    LlamaCppSupervisor supervisor,
    CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest();
    }

    if (supervisor.Status.Status != "healthy")
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Local model is not ready",
            detail: supervisor.Status.Message);
    }

    try
    {
        return Results.Ok(await modelClient.CompleteAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid model request",
            detail: exception.Message);
    }
    catch (ModelUnavailableException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Local model is not configured",
            detail: exception.Message);
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Local model request failed",
            detail: exception.Message);
    }
    catch (InvalidDataException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Local model returned invalid output",
            detail: exception.Message);
    }
    catch (System.Text.Json.JsonException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Local model returned invalid JSON",
            detail: exception.Message);
    }
});

app.MapGet("/plans/actions", (TaskPlanner planner) =>
    Results.Ok(planner.GetActionDefinitions()));

app.MapPost("/plans", async (
    CreatePlanRequest? request,
    TaskPlanner planner,
    LlamaCppSupervisor supervisor,
    CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest();
    }

    if (supervisor.Status.Status != "healthy")
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Local model is not ready",
            detail: supervisor.Status.Message);
    }

    try
    {
        return Results.Ok(await planner.CreatePlanAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid planning request",
            detail: exception.Message);
    }
    catch (ModelUnavailableException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Local model is not configured",
            detail: exception.Message);
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Local model request failed",
            detail: exception.Message);
    }
    catch (InvalidDataException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Local model returned an invalid plan",
            detail: exception.Message);
    }
    catch (System.Text.Json.JsonException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Local model returned invalid JSON",
            detail: exception.Message);
    }
});

app.MapPost("/plans/approvals", async (
    CreatePlanApprovalRequest? request,
    TaskPlanner planner,
    ExecutionPolicy policy,
    PlanApprovalStore approvalStore,
    System.Security.Claims.ClaimsPrincipal user,
    CancellationToken cancellationToken) =>
{
    if (request?.Plan is null)
    {
        return Results.BadRequest();
    }

    if (!request.ConfirmApproval)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["confirmApproval"] = ["Set this to true only after reviewing the plan and its requested actions."]
        });
    }

    try
    {
        planner.ValidatePlanForExecution(request.Plan);
        policy.Validate(request.Plan);
        var approvedBy = AuthenticatedWindowsUser.GetName(user);
        return Results.Ok(await approvalStore.CreateAsync(request.Plan, approvedBy, cancellationToken));
    }
    catch (InvalidDataException exception)
    {
        await approvalStore.RecordDeniedAsync(null, "INVALID_PLAN", user.Identity?.Name ?? "unknown", cancellationToken);
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid plan",
            detail: exception.Message);
    }
    catch (UnauthorizedAccessException exception)
    {
        await approvalStore.RecordDeniedAsync(null, "CAPABILITY_DENIED", user.Identity?.Name ?? "unknown", cancellationToken);
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Plan approval denied by policy",
            detail: exception.Message);
    }
}).RequireAuthorization();

app.MapDelete("/plans/approvals/{approvalId:guid}", async (
    Guid approvalId,
    PlanApprovalStore approvalStore,
    System.Security.Claims.ClaimsPrincipal user,
    CancellationToken cancellationToken) =>
{
    var approvedBy = AuthenticatedWindowsUser.GetName(user);
    var revoked = await approvalStore.RevokeAsync(approvalId, approvedBy, cancellationToken);
    return revoked ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/plans/execute", async (
    ExecutePlanRequest? request,
    TaskPlanner planner,
    ExecutionPolicy policy,
    FileSystemActionExecutor executor,
    PlanApprovalStore approvalStore,
    System.Security.Claims.ClaimsPrincipal user,
    CancellationToken cancellationToken) =>
{
    if (request?.Plan is null || request.ApprovalId == Guid.Empty)
    {
        return Results.BadRequest();
    }

    string planHash;
    var approvedBy = AuthenticatedWindowsUser.GetName(user);
    try
    {
        planner.ValidatePlanForExecution(request.Plan);
        policy.Validate(request.Plan);
        planHash = PlanApprovalStore.GetPlanHash(request.Plan);
    }
    catch (InvalidDataException exception)
    {
        await approvalStore.RecordDeniedAsync(null, "INVALID_PLAN", approvedBy, cancellationToken);
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid plan",
            detail: exception.Message);
    }
    catch (UnauthorizedAccessException exception)
    {
        await approvalStore.RecordDeniedAsync(null, "CAPABILITY_DENIED", approvedBy, cancellationToken);
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Execution denied by policy",
            detail: exception.Message);
    }

    if (!await approvalStore.TryConsumeAsync(request.ApprovalId, planHash, approvedBy, cancellationToken))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Approval invalid",
            detail: "The approval may be expired, revoked, already used, or bound to a different plan.");
    }

    try
    {
        var response = await executor.ExecuteAsync(request.Plan, cancellationToken);
        await approvalStore.RecordExecutionAsync(planHash, response, approvedBy, cancellationToken);
        return Results.Ok(response);
    }
    catch (InvalidDataException exception)
    {
        await approvalStore.RecordDeniedAsync(planHash, "UNSUPPORTED_ACTION", approvedBy, cancellationToken);
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Unsupported action",
            detail: exception.Message);
    }
}).RequireAuthorization();

await app.RunAsync();

int ReadLlamaInteger(string key, int defaultValue, int minimum, int maximum)
{
    var value = llamaConfiguration[key];
    if (value is null)
    {
        return defaultValue;
    }

    if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
    {
        throw new InvalidOperationException(
            $"Baymax:LlamaCpp:{key} must be an integer from {minimum} to {maximum}.");
    }

    return parsed;
}

int? ReadOptionalLlamaInteger(string key, int minimum, int maximum)
{
    var value = llamaConfiguration[key];
    if (value is null)
    {
        return null;
    }

    if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
    {
        throw new InvalidOperationException(
            $"Baymax:LlamaCpp:{key} must be an integer from {minimum} to {maximum}.");
    }

    return parsed;
}
