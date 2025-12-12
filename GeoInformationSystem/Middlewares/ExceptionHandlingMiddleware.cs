using System.Text.Json;

namespace GeoAdminDemo.Middlewares;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IHostEnvironment env)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            // timeout interno (HttpClient, SQL, etc.)
            logger.LogWarning("Request timeout: {Method} {Path}", context.Request.Method, context.Request.Path);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                context.Response.ContentType = "application/problem+json";

                var pd = new
                {
                    type = "https://httpstatuses.com/504",
                    title = "Gateway Timeout",
                    status = 504,
                    detail = "La operación ha excedido el tiempo permitido.",
                    traceId = context.TraceIdentifier
                };

                await context.Response.WriteAsync(JsonSerializer.Serialize(pd, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception: {Method} {Path}", context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted) throw;

            context.Response.ContentType = "application/problem+json";
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var pd = new
            {
                type = "https://httpstatuses.com/500",
                title = "Internal Server Error",
                status = 500,
                detail = env.IsDevelopment() ? ex.Message : "Se produjo un error inesperado.",
                traceId = context.TraceIdentifier
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(pd, JsonOptions));
        }
    }
}
