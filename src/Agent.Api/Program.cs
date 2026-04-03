using Agent.Api;
using Agent.Api.Health;
using Agent.Api.Security;
using Agent.Core;
using Agent.Core.Workspace;
using Agent.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDevOpsAgent(builder.Configuration);
builder.Services.AddChatSessionStorage(builder.Configuration);
builder.Services.AddApiSecurity(builder.Configuration);
builder.Services.AddAgentHealthChecks(builder.Configuration);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = RequestLimits.AddTraceId);
builder.Services.AddExceptionHandler<RequestLimits.BadRequestExceptionHandler>();
builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
    .Validate(o => o.MaxChatRequestBytes > 0, "Api:MaxChatRequestBytes must be positive.")
    .ValidateOnStart();

var apiOptions = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
var workspaceOptions = builder.Configuration.GetSection(WorkspaceOptions.SectionName).Get<WorkspaceOptions>() ?? new WorkspaceOptions();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseDeclaredRequestSizeLimits();

app.MapAgentHealthChecks();
app.MapChatEndpoints()
    .RequireAuthorization(ApiSecurityExtensions.ClientPolicy)
    .RequireRateLimiting(ApiSecurityExtensions.RateLimitPolicy)
    .WithRequestSizeLimit(apiOptions.MaxChatRequestBytes);
app.MapLintEndpoints()
    .RequireAuthorization(ApiSecurityExtensions.ClientPolicy)
    .RequireRateLimiting(ApiSecurityExtensions.RateLimitPolicy)
    .WithRequestSizeLimit(workspaceOptions.MaxFileBytes);

app.WarnIfUnauthenticated();
app.Run();

/// <summary>Entry point, visible to the integration tests.</summary>
public partial class Program;
