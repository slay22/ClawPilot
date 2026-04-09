using System.Text.RegularExpressions;
using Cronos;

namespace ClawPilot.Worker.Services;

/// <summary>
/// Parses a schedule string into a <see cref="CronExpression"/> and computes the next occurrence.
/// Supports natural language intervals ("every 5m", "every 1h", "every day at 09:00")
/// and raw cron expressions ("0 * * * *").
/// </summary>
public static class CronScheduleParser
{
    private static readonly Regex EveryInterval =
        new(@"^every\s+(\d+)\s*(m(?:in(?:utes?)?)?|h(?:ours?)?|d(?:ays?)?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EveryDayAt =
        new(@"^every\s+day\s+at\s+(\d{1,2}):(\d{2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OnceAt =
        new(@"^once\s+at\s+(\d{1,2}):(\d{2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses <paramref name="input"/> and returns a Cronos <see cref="CronExpression"/>.
    /// Throws <see cref="ArgumentException"/> when the input cannot be parsed.
    /// </summary>
    public static CronExpression Parse(string input)
    {
        string trimmed = input.Trim();

        // "every day" shorthand
        if (trimmed.Equals("every day", StringComparison.OrdinalIgnoreCase))
            return CronExpression.Parse("0 0 * * *");

        // "every day at HH:mm"
        Match dayAtMatch = EveryDayAt.Match(trimmed);
        if (dayAtMatch.Success)
        {
            int hour = int.Parse(dayAtMatch.Groups[1].Value);
            int minute = int.Parse(dayAtMatch.Groups[2].Value);
            return CronExpression.Parse($"{minute} {hour} * * *");
        }

        // "once at HH:mm" — treated as a daily cron; RunOnce logic removes it after first fire
        Match onceAtMatch = OnceAt.Match(trimmed);
        if (onceAtMatch.Success)
        {
            int hour = int.Parse(onceAtMatch.Groups[1].Value);
            int minute = int.Parse(onceAtMatch.Groups[2].Value);
            return CronExpression.Parse($"{minute} {hour} * * *");
        }

        // "every N m/h/d"
        Match intervalMatch = EveryInterval.Match(trimmed);
        if (intervalMatch.Success)
        {
            int value = int.Parse(intervalMatch.Groups[1].Value);
            string unit = intervalMatch.Groups[2].Value.ToLowerInvariant();

            if (unit.StartsWith('m'))
            {
                if (value < 1 || value > 59)
                    throw new ArgumentException($"Minute interval must be 1–59, got {value}.");
                return CronExpression.Parse($"*/{value} * * * *");
            }

            if (unit.StartsWith('h'))
            {
                if (value < 1 || value > 23)
                    throw new ArgumentException($"Hour interval must be 1–23, got {value}.");
                return CronExpression.Parse($"0 */{value} * * *");
            }

            if (unit.StartsWith('d'))
            {
                if (value != 1)
                    throw new ArgumentException("Only 'every 1d' (every day) is supported for days. Use 'every day at HH:mm' for a specific time.");
                return CronExpression.Parse("0 0 * * *");
            }
        }

        // Fall back: try raw cron expression (5-part or 6-part with seconds)
        try
        {
            return CronExpression.Parse(trimmed, CronFormat.Standard);
        }
        catch
        {
            // ignored — try extended format next
        }

        try
        {
            return CronExpression.Parse(trimmed, CronFormat.IncludeSeconds);
        }
        catch
        {
            throw new ArgumentException(
                $"Cannot parse schedule '{trimmed}'. " +
                "Use natural language (e.g. 'every 1h', 'every day at 09:00') " +
                "or a standard cron expression (e.g. '0 * * * *').");
        }
    }

    /// <summary>Returns the next UTC occurrence after <paramref name="from"/>.</summary>
    public static DateTimeOffset NextOccurrence(CronExpression expression, DateTimeOffset from)
    {
        DateTimeOffset? next = expression.GetNextOccurrence(from, TimeZoneInfo.Utc);
        return next ?? from.AddHours(1); // fallback — should never happen with standard expressions
    }
}
