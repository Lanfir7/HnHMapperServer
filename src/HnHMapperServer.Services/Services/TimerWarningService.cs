using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Implementation of timer warning tracking service.
/// Manages TimerWarnings table to track which pre-expiry warnings have been sent.
/// </summary>
public class TimerWarningService : ITimerWarningService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<TimerWarningService> _logger;

    public TimerWarningService(
        ApplicationDbContext db,
        ILogger<TimerWarningService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Check if a warning has already been sent for a timer at a specific interval.
    /// </summary>
    public async Task<bool> HasWarningSentAsync(int timerId, int warningMinutes)
    {
        return await _db.TimerWarnings
            .AnyAsync(tw => tw.TimerId == timerId && tw.WarningMinutes == warningMinutes);
    }

    /// <summary>
    /// Mark a warning as sent for a timer at a specific interval.
    /// </summary>
    public async Task MarkWarningSentAsync(int timerId, int warningMinutes)
    {
        // Check if already exists (unique constraint protection)
        var exists = await HasWarningSentAsync(timerId, warningMinutes);
        if (exists)
        {
            _logger.LogDebug(
                "Warning already recorded for timer {TimerId} at {WarningMinutes} minutes",
                timerId, warningMinutes);
            return;
        }

        var warning = new TimerWarningEntity
        {
            TimerId = timerId,
            WarningMinutes = warningMinutes,
            SentAt = DateTime.UtcNow
        };

        _db.TimerWarnings.Add(warning);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Marked warning sent for timer {TimerId} at {WarningMinutes} minutes",
            timerId, warningMinutes);
    }

    /// <summary>
    /// Get list of warning minutes that have been sent for a timer.
    /// </summary>
    public async Task<List<int>> GetSentWarningMinutesAsync(int timerId)
    {
        return await _db.TimerWarnings
            .Where(tw => tw.TimerId == timerId)
            .Select(tw => tw.WarningMinutes)
            .OrderByDescending(w => w) // Largest interval first (1440, 240, 60, 10)
            .ToListAsync();
    }

    /// <summary>
    /// Delete all warnings for a timer (e.g., when timer is reset/updated).
    /// </summary>
    public async Task ClearWarningsAsync(int timerId)
    {
        var count = await _db.TimerWarnings
            .Where(tw => tw.TimerId == timerId)
            .ExecuteDeleteAsync();

        if (count > 0)
        {
            _logger.LogInformation(
                "Cleared {Count} warnings for timer {TimerId}",
                count, timerId);
        }
    }
}
