using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Agent.Api;

/// <summary>Request limits for the HTTP API. Bound from the <c>Api</c> configuration section.</summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Largest chat request body (the JSON with the message), in bytes.</summary>
    public long MaxChatRequestBytes { get; set; } = 64 * 1024;
}

public static class RequestLimits
{
    /// <summary>
    /// Declares <paramref name="maxBytes"/> as the endpoint's body limit. Kestrel enforces it while reading a body of
    /// any length; <see cref="UseDeclaredRequestSizeLimits"/> refuses a declared Content-Length over it up front.
    /// </summary>
    public static TBuilder WithRequestSizeLimit<TBuilder>(this TBuilder builder, long maxBytes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequestSizeLimitAttribute(maxBytes));

    /// <summary>Answers 413 problem details when a request declares a body larger than its endpoint allows.</summary>
    public static IApplicationBuilder UseDeclaredRequestSizeLimits(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
            if (limit is { } max && context.Request.ContentLength > max)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status413PayloadTooLarge,
                        Title = "Payload Too Large",
                        Detail = $"The body is larger than the {max}-byte limit for this endpoint.",
                    },
                });
                return;
            }

            await next(context);
        });

    /// <summary>
    /// Turns <see cref="BadHttpRequestException"/> (malformed JSON, a body over the server limit) into problem
    /// details with its own status code. Minimal APIs throw it in Development instead of answering 400, and the
    /// default exception handler would report it as a 500.
    /// </summary>
    public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not BadHttpRequestException badRequest)
            {
                return false;
            }

            httpContext.Response.StatusCode = badRequest.StatusCode;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = { Status = badRequest.StatusCode, Detail = badRequest.Message },
                Exception = exception,
            });
        }
    }

    /// <summary>Every problem response carries the trace id, so a client report can be matched to logs and traces.</summary>
    public static void AddTraceId(ProblemDetailsContext context)
    {
        context.ProblemDetails.Instance ??= $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    }
}
