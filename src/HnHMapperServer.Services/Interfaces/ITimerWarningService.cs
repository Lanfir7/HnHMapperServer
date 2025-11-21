namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for managing timer warning tracking.
/// Tracks which pre-expiry warnings have been sent for each timer.
/// </summary>
public interface ITimerWarningService
{
    /// <summary>
    /// Check if a warning has already been sent for a timer at a specific interval.
    /// </summary>
    /// <param name="timerId">Timer ID</param>
    /// <param name="warningMinutes">Warning interval in minutes (e.g., 1440, 240, 60, 10)</param>
    /// <returns>True if warning was already sent, false otherwise</returns>
    Task<bool> HasWarningSentAsync(int timerId, int warningMinutes);

    /// <summary>
    /// Mark a warning as sent for a timer at a specific interval.
    /// </summary>
    /// <param name="timerId">Timer ID</param>
    /// <param name="warningMinutes">Warning interval in minutes</param>
    Task MarkWarningSentAsync(int timerId, int warningMinutes);

    /// <summary>
    /// Get list of warning minutes that have been sent for a timer.
    /// </summary>
    /// <param name="timerId">Timer ID</param>
    /// <returns>List of warning intervals in minutes</returns>
    Task<List<int>> GetSentWarningMinutesAsync(int timerId);

    /// <summary>
    /// Delete all warnings for a timer (e.g., when timer is reset/updated).
    /// </summary>
    /// <param name="timerId">Timer ID</param>
    Task ClearWarningsAsync(int timerId);
}
