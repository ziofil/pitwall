using Microsoft.AspNetCore.Mvc;
using Pitwall.Data;

namespace Pitwall.Console.Api;

public enum ControlOperation
{
    PauseClock,
    ResumeClock,
    ToggleClock,
}

public sealed record ControlRequest(ControlOperation operation);

public sealed record ControlResponse(bool ClockPaused, bool SessionRunning, string? SessionName);

public enum ControlErrorCode
{
    NoRunningSession,
    UnknownOperation,
};

public sealed record ControlError(ControlErrorCode errorCode)
{
    public string errorMessage =>
        errorCode switch
        {
            ControlErrorCode.NoRunningSession => "No session is currently running",
            ControlErrorCode.UnknownOperation => "Unknown operation requested",
            _ => "Unknwon error",
        };
}

public static class ControlEndpoints
{
    public static WebApplication MapControlEndpoints(this WebApplication app)
    {
        app.MapGet("/control", GetControlState)
            .WithDescription("Fetches the current state of the timing client")
            .WithTags("Control")
            .Produces<ControlResponse>(StatusCodes.Status200OK);

        app.MapPost("/control", ControlApiEndpoint)
            .WithDescription(
                "Issues control commands to the currently running session. These commands allow you to, for example, start or stop the session clock to synchronize the pit wall with another service"
            )
            .WithTags("Control")
            .Produces<ControlResponse>(StatusCodes.Status200OK)
            .Produces<ControlError>(StatusCodes.Status400BadRequest);

        return app;
    }

    public static IResult GetControlState(
        SessionInfoProcessor sessionInfo,
        IDateTimeProvider dateTimeProvider
    )
    {
        var res = new ControlResponse(
            dateTimeProvider.IsPaused,
            sessionInfo.Latest.Name is not null,
            sessionInfo.Latest.Name
        );

        return Results.Ok(res);
    }

    public static IResult ControlApiEndpoint(
        [FromBody] ControlRequest request,
        SessionInfoProcessor sessionInfo,
        IDateTimeProvider dateTimeProvider
    )
    {
        if (sessionInfo.Latest.Name is null)
        {
            return Results.BadRequest(new ControlError(ControlErrorCode.NoRunningSession));
        }

        if (!Enum.IsDefined(request.operation))
        {
            return Results.BadRequest(new ControlError(ControlErrorCode.UnknownOperation));
        }

        switch (request.operation)
        {
            case ControlOperation.PauseClock when !dateTimeProvider.IsPaused:
                dateTimeProvider.TogglePause();
                break;
            case ControlOperation.ResumeClock when dateTimeProvider.IsPaused:
                dateTimeProvider.TogglePause();
                break;
            case ControlOperation.ToggleClock:
                dateTimeProvider.TogglePause();
                break;
        }

        return GetControlState(sessionInfo, dateTimeProvider);
    }
}
