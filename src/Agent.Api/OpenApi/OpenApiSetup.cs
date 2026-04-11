using System.Text.Json;
using Agent.Api.Security;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Agent.Api.OpenApi;

/// <summary>OpenAPI document (Swashbuckle) for the HTTP API, with the API key scheme and request examples.</summary>
public static class OpenApiSetup
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddAgentOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "DevOps agent API",
                Version = DocumentName,
                Description =
                    "Chat with a Semantic Kernel agent that lints Kubernetes manifests, Dockerfiles, Helm charts, Terraform " +
                    "and GitHub Actions workflows, analyzes logs and searches runbooks; or call the linters directly. " +
                    $"When API keys are configured, send one in the {ApiKeyOptions.HeaderName} header.",
                License = new OpenApiLicense { Name = "MIT" },
            });

            var scheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyOptions.HeaderName,
                Description = "An API key from Auth:ApiKeys. Not needed when the server has no keys configured.",
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyAuthenticationHandler.SchemeName },
            };
            options.AddSecurityDefinition(ApiKeyAuthenticationHandler.SchemeName, scheme);
            options.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = [] });
        });

        return services;
    }

    /// <summary>Serves the document at <c>/swagger/v1/swagger.json</c> and Swagger UI at <c>/swagger</c>.</summary>
    public static WebApplication MapAgentOpenApi(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options => options.DocumentTitle = "DevOps agent API");
        return app;
    }

    /// <summary>Adds a raw-text request body with an example, for endpoints that read the body themselves.</summary>
    public static RouteHandlerBuilder AcceptsText(this RouteHandlerBuilder builder, string description, string example) =>
        builder
            .Accepts<string>("text/plain", "application/yaml", "application/x-yaml")
            .WithOpenApi(operation =>
            {
                operation.RequestBody ??= new OpenApiRequestBody();
                operation.RequestBody.Required = true;
                operation.RequestBody.Description = description;
                foreach (var content in operation.RequestBody.Content.Values)
                {
                    content.Schema = new OpenApiSchema { Type = "string" };
                    content.Example = new OpenApiString(example);
                }

                return operation;
            });

    /// <summary>Sets the JSON request body example of an operation.</summary>
    public static RouteHandlerBuilder WithJsonExample(this RouteHandlerBuilder builder, object example) =>
        builder.WithOpenApi(operation =>
        {
            if (operation.RequestBody?.Content.TryGetValue("application/json", out var content) == true)
            {
                content.Example = ToOpenApi(JsonSerializer.SerializeToElement(example, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }

            return operation;
        });

    private static IOpenApiAny ToOpenApi(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToObject(element),
        JsonValueKind.Array => ToArray(element),
        JsonValueKind.String => new OpenApiString(element.GetString()),
        JsonValueKind.Number => new OpenApiDouble(element.GetDouble()),
        JsonValueKind.True => new OpenApiBoolean(true),
        JsonValueKind.False => new OpenApiBoolean(false),
        _ => new OpenApiNull(),
    };

    private static OpenApiObject ToObject(JsonElement element)
    {
        var result = new OpenApiObject();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ToOpenApi(property.Value);
        }

        return result;
    }

    private static OpenApiArray ToArray(JsonElement element)
    {
        var result = new OpenApiArray();
        result.AddRange(element.EnumerateArray().Select(ToOpenApi));
        return result;
    }
}
