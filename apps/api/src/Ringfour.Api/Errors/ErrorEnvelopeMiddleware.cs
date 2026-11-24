using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Ringfour.Api.Errors;

public sealed class ApiException(int statusCode, string code, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public bool Retryable { get; init; }
    public JsonObject? Details { get; init; }

    public static ApiException InvalidInput(string message) =>
        new(StatusCodes.Status400BadRequest, "invalid_input", message);

    public static ApiException Unauthenticated(string message) =>
        new(StatusCodes.Status401Unauthorized, "unauthenticated", message);

    public static ApiException Forbidden(string message) =>
        new(StatusCodes.Status403Forbidden, "forbidden", message);

    public static ApiException NotFound(string message) =>
        new(StatusCodes.Status404NotFound, "not_found", message);

    public static ApiException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    public static ApiException InvalidToken(string message) =>
        new(StatusCodes.Status401Unauthorized, "invalid_token", message);
}

public sealed record ErrorBody(string Code, string Message, string RequestId);

public sealed record ErrorEnvelope(ErrorBody Error);

// App endpoints opt into the v2 envelope without changing legacy consumers.
public sealed class AppErrorResponse { }

// Converts domain exceptions and bare 401/403/404 responses into the shared
// error envelope; request_id correlates with server logs via TraceIdentifier.
public sealed class ErrorEnvelopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException exception)
        {
            await WriteAsync(context, exception.StatusCode, exception.Code, exception.Message, exception.Retryable, exception.Details);
            return;
        }
        catch (BadHttpRequestException exception)
        {
            await WriteAsync(
                context, StatusCodes.Status400BadRequest, "invalid_input", exception.Message);
            return;
        }
        catch (JsonException exception)
        {
            await WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_input",
                $"Request body is not valid JSON: {exception.Message}");
            return;
        }

        if (context.Response.HasStarted || context.Response.StatusCode < 400)
        {
            return;
        }

        var (code, message) = context.Response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized => ("unauthenticated", "A valid access token is required."),
            StatusCodes.Status403Forbidden => ("forbidden", "This operation requires a higher role."),
            StatusCodes.Status404NotFound => ("not_found", "The requested resource does not exist in this organization."),
            _ => (null, null),
        };
        if (code is not null)
        {
            await WriteAsync(context, context.Response.StatusCode, code, message!);
        }
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, string code, string message, bool retryable = false, JsonObject? details = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        if (context.GetEndpoint()?.Metadata.GetMetadata<AppErrorResponse>() is not null)
        {
            await context.Response.WriteAsJsonAsync(new { error = new {
                code = code == "invalid_token" ? "unauthenticated" : code,
                message = message.Length > 1000 ? message[..1000] : message,
                details = details ?? new JsonObject { ["retryable"] = retryable }, request_id = context.TraceIdentifier,
            } }, RingfourJson.Options);
            return;
        }
        var envelope = new ErrorEnvelope(new ErrorBody(code, message, context.TraceIdentifier));
        await context.Response.WriteAsJsonAsync(envelope, RingfourJson.Options);
    }
}
