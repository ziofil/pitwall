using Microsoft.Extensions.Logging;

namespace Pitwall.Data;

public class DateTimeProvider(ILogger<DateTimeProvider> logger) : IDateTimeProvider
{
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public DateTimeOffset Utc => PausedAt ?? DateTimeOffset.UtcNow - Delay;

    public DateTimeOffset? PausedAt { get; private set; }

    public bool IsPaused => PausedAt.HasValue;

    public void TogglePause()
    {
        if (PausedAt.HasValue)
        {
            var oldDelay = Delay;

            // To resume, we need to update the Delay to account for how long we've been paused for
            // When resuming, calculate what the delay should be based on the current time
            Delay = DateTimeOffset.UtcNow - PausedAt.Value;

            logger.LogInformation(
                "Resuming clock with previous delay: {PreviousDelay} and new delay: {NewDelay}",
                oldDelay,
                Delay
            );
            PausedAt = null;
        }
        else
        {
            PausedAt = Utc;
            logger.LogInformation("Paused clock at {PausedAt}", PausedAt.Value);
        }
    }
}
