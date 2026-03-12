namespace Aura.Core.Services;

/// <summary>
/// Parses and evaluates standard 5-field cron expressions.
/// Fields: minute (0-59) hour (0-23) day-of-month (1-31) month (1-12) day-of-week (0-6, 0=Sunday).
/// Supports: * (any), values, lists (1,3,5), ranges (1-5), steps (*/5, 1-10/2).
/// </summary>
public sealed class CronExpression
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;

    private CronExpression(
        HashSet<int> minutes,
        HashSet<int> hours,
        HashSet<int> daysOfMonth,
        HashSet<int> months,
        HashSet<int> daysOfWeek)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
    }

    /// <summary>
    /// Parse a 5-field cron expression string.
    /// </summary>
    public static CronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            throw new FormatException($"Cron expression must have exactly 5 fields, got {parts.Length}: \"{expression}\"");

        return new CronExpression(
            minutes: ParseField(parts[0], 0, 59),
            hours: ParseField(parts[1], 0, 23),
            daysOfMonth: ParseField(parts[2], 1, 31),
            months: ParseField(parts[3], 1, 12),
            daysOfWeek: ParseField(parts[4], 0, 6)
        );
    }

    /// <summary>
    /// Try to parse a cron expression, returning null on failure.
    /// </summary>
    public static CronExpression? TryParse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        try
        {
            return Parse(expression);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true if the given UTC time matches this cron expression (evaluated to the minute).
    /// </summary>
    public bool Matches(DateTime utcTime)
    {
        return _minutes.Contains(utcTime.Minute)
            && _hours.Contains(utcTime.Hour)
            && _daysOfMonth.Contains(utcTime.Day)
            && _months.Contains(utcTime.Month)
            && _daysOfWeek.Contains((int)utcTime.DayOfWeek);
    }

    internal static HashSet<int> ParseField(string field, int min, int max)
    {
        var result = new HashSet<int>();

        foreach (var part in field.Split(','))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                throw new FormatException($"Empty element in cron field: \"{field}\"");

            // Check for step: */2, 1-10/3, etc.
            int step = 1;
            var slashIndex = trimmed.IndexOf('/');
            string rangePart;

            if (slashIndex >= 0)
            {
                var stepStr = trimmed[(slashIndex + 1)..];
                if (!int.TryParse(stepStr, out step) || step < 1)
                    throw new FormatException($"Invalid step value in cron field: \"{trimmed}\"");
                rangePart = trimmed[..slashIndex];
            }
            else
            {
                rangePart = trimmed;
            }

            if (rangePart == "*")
            {
                for (var i = min; i <= max; i += step)
                    result.Add(i);
            }
            else if (rangePart.Contains('-'))
            {
                var dashParts = rangePart.Split('-');
                if (dashParts.Length != 2)
                    throw new FormatException($"Invalid range in cron field: \"{rangePart}\"");

                if (!int.TryParse(dashParts[0], out var rangeStart) ||
                    !int.TryParse(dashParts[1], out var rangeEnd))
                    throw new FormatException($"Non-numeric range in cron field: \"{rangePart}\"");

                ValidateBounds(rangeStart, min, max, rangePart);
                ValidateBounds(rangeEnd, min, max, rangePart);

                if (rangeStart > rangeEnd)
                    throw new FormatException($"Range start exceeds end in cron field: \"{rangePart}\"");

                for (var i = rangeStart; i <= rangeEnd; i += step)
                    result.Add(i);
            }
            else
            {
                // Single value (step only applies with * or range)
                if (!int.TryParse(rangePart, out var value))
                    throw new FormatException($"Non-numeric value in cron field: \"{rangePart}\"");

                ValidateBounds(value, min, max, rangePart);
                result.Add(value);
            }
        }

        return result;
    }

    private static void ValidateBounds(int value, int min, int max, string context)
    {
        if (value < min || value > max)
            throw new FormatException($"Value {value} out of range [{min}-{max}] in cron field: \"{context}\"");
    }
}
