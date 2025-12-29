using System.Text.Json.Serialization;

namespace Swarm.Core.Models;

/// <summary>
/// Represents a time window during which sync is allowed or blocked.
/// </summary>
public class SyncSchedule
{
    /// <summary>
    /// Whether scheduling is enabled. If false, sync runs 24/7.
    /// </summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// The schedule mode: AllowDuring (whitelist) or BlockDuring (blacklist).
    /// </summary>
    public ScheduleMode Mode { get; set; } = ScheduleMode.AllowDuring;

    /// <summary>
    /// List of time windows when sync is allowed/blocked based on Mode.
    /// </summary>
    public List<SyncTimeWindow> TimeWindows { get; set; } = new();

    /// <summary>
    /// Determines if sync is currently allowed based on schedule.
    /// </summary>
    [JsonIgnore]
    public bool IsSyncAllowedNow
    {
        get
        {
            if (!IsEnabled || TimeWindows.Count == 0)
                return true; // No schedule = always allowed

            var now = DateTime.Now;
            var currentDay = now.DayOfWeek;
            var currentTime = now.TimeOfDay;

            var inWindow = TimeWindows.Any(w => w.IsActiveAt(currentDay, currentTime));

            return Mode == ScheduleMode.AllowDuring ? inWindow : !inWindow;
        }
    }

    /// <summary>
    /// Gets display text for the schedule status.
    /// </summary>
    [JsonIgnore]
    public string StatusDisplay
    {
        get
        {
            if (!IsEnabled)
                return "Always syncing";

            if (TimeWindows.Count == 0)
                return "No schedule configured";

            return IsSyncAllowedNow ? "Sync active (in schedule)" : "Sync paused (outside schedule)";
        }
    }

    /// <summary>
    /// Gets the next schedule change time.
    /// </summary>
    [JsonIgnore]
    public DateTime? NextChangeTime
    {
        get
        {
            if (!IsEnabled || TimeWindows.Count == 0)
                return null;

            var now = DateTime.Now;
            var currentDay = now.DayOfWeek;
            var currentTime = now.TimeOfDay;

            // Look ahead up to 7 days
            for (int dayOffset = 0; dayOffset < 7; dayOffset++)
            {
                var checkDay = (DayOfWeek)(((int)currentDay + dayOffset) % 7);
                
                foreach (var window in TimeWindows.Where(w => w.Days.Contains(checkDay)).OrderBy(w => w.StartTime))
                {
                    var startTime = window.StartTime;
                    var endTime = window.EndTime;

                    if (dayOffset == 0)
                    {
                        // Same day - check if we haven't passed start or end
                        if (currentTime < startTime)
                            return now.Date.AddDays(dayOffset).Add(startTime);
                        if (currentTime < endTime)
                            return now.Date.AddDays(dayOffset).Add(endTime);
                    }
                    else
                    {
                        // Future day - return first window start
                        return now.Date.AddDays(dayOffset).Add(startTime);
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Creates a default "business hours" schedule.
    /// </summary>
    public static SyncSchedule CreateBusinessHours()
    {
        return new SyncSchedule
        {
            IsEnabled = true,
            Mode = ScheduleMode.AllowDuring,
            TimeWindows = new List<SyncTimeWindow>
            {
                new SyncTimeWindow
                {
                    Name = "Business Hours",
                    StartTime = new TimeSpan(9, 0, 0),
                    EndTime = new TimeSpan(17, 0, 0),
                    Days = new List<DayOfWeek> 
                    { 
                        DayOfWeek.Monday, 
                        DayOfWeek.Tuesday, 
                        DayOfWeek.Wednesday, 
                        DayOfWeek.Thursday, 
                        DayOfWeek.Friday 
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates a "nights only" schedule for bandwidth-intensive syncing.
    /// </summary>
    public static SyncSchedule CreateNightsOnly()
    {
        return new SyncSchedule
        {
            IsEnabled = true,
            Mode = ScheduleMode.AllowDuring,
            TimeWindows = new List<SyncTimeWindow>
            {
                new SyncTimeWindow
                {
                    Name = "Night Sync",
                    StartTime = new TimeSpan(22, 0, 0),
                    EndTime = new TimeSpan(6, 0, 0),
                    Days = Enum.GetValues<DayOfWeek>().ToList()
                }
            }
        };
    }
}

/// <summary>
/// Schedule mode: whitelist (only during) or blacklist (except during).
/// </summary>
public enum ScheduleMode
{
    /// <summary>
    /// Sync only during the specified time windows.
    /// </summary>
    AllowDuring,

    /// <summary>
    /// Sync except during the specified time windows.
    /// </summary>
    BlockDuring
}

/// <summary>
/// Represents a recurring time window for sync scheduling.
/// </summary>
public class SyncTimeWindow
{
    /// <summary>
    /// Optional name for this time window (e.g., "Work Hours").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Start time of the window (inclusive).
    /// </summary>
    public TimeSpan StartTime { get; set; } = new TimeSpan(9, 0, 0);

    /// <summary>
    /// End time of the window (exclusive).
    /// </summary>
    public TimeSpan EndTime { get; set; } = new TimeSpan(17, 0, 0);

    /// <summary>
    /// Days of the week when this window is active.
    /// </summary>
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>
    /// Checks if this window is active at the given day and time.
    /// </summary>
    public bool IsActiveAt(DayOfWeek day, TimeSpan time)
    {
        if (!Days.Contains(day))
            return false;

        // Handle overnight windows (e.g., 22:00 - 06:00)
        if (EndTime <= StartTime)
        {
            // Window spans midnight
            return time >= StartTime || time < EndTime;
        }

        return time >= StartTime && time < EndTime;
    }

    /// <summary>
    /// Gets a display string for the days (e.g., "Mon-Fri" or "Weekends").
    /// </summary>
    [JsonIgnore]
    public string DaysDisplay
    {
        get
        {
            if (Days.Count == 7)
                return "Every day";
            if (Days.Count == 5 && !Days.Contains(DayOfWeek.Saturday) && !Days.Contains(DayOfWeek.Sunday))
                return "Weekdays";
            if (Days.Count == 2 && Days.Contains(DayOfWeek.Saturday) && Days.Contains(DayOfWeek.Sunday))
                return "Weekends";

            return string.Join(", ", Days.Select(d => d.ToString()[..3]));
        }
    }

    /// <summary>
    /// Gets a display string for the time range.
    /// </summary>
    [JsonIgnore]
    public string TimeDisplay => $"{StartTime:hh\\:mm} - {EndTime:hh\\:mm}";
}
