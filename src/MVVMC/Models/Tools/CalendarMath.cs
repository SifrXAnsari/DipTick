using System.Globalization;

namespace DipTick.Models.Tools;

/// <summary>
/// The calendar's arithmetic (<c>calendar_days</c>, <c>tools cal</c>, the
/// Tools sheet): days between two dates, a date plus days, a date's weekday and ISO week, from .NET's
/// <see cref="ISOWeek"/> (ISO 8601: weeks begin on Monday; week 1 holds the year's first Thursday).
/// Dates are plain calendar days, yyyy-MM-dd, with no time and no zone.
/// </summary>
internal static class CalendarMath
{
    /// <summary>A date as the tools read it: yyyy-MM-dd, or today, yesterday or tomorrow; null when it is none of these.</summary>
    public static DateOnly? Read(string text, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(text);
        var word = text.Trim();
        if (word.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return today;
        }

        if (word.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            return today.AddDays(-1);
        }

        if (word.Equals("tomorrow", StringComparison.OrdinalIgnoreCase))
        {
            return today.AddDays(1);
        }

        return DateOnly.TryParseExact(word, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>A date with its weekday and ISO week: "2026-09-27 (Sunday, ISO week 2026-W39)".</summary>
    public static string Describe(DateOnly date)
    {
        var day = date.ToDateTime(TimeOnly.MinValue);
        return string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd} ({date.DayOfWeek}, ISO week {ISOWeek.GetYear(day)}-W{ISOWeek.GetWeekOfYear(day):00})");
    }

    /// <summary>Days from one date to another: positive when <paramref name="to"/> is later.</summary>
    public static int Between(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber;

    /// <summary>A date plus (or minus) days, or null past the calendar's ends.</summary>
    public static DateOnly? Add(DateOnly date, long days)
    {
        var number = date.DayNumber + days;
        return number < DateOnly.MinValue.DayNumber || number > DateOnly.MaxValue.DayNumber ? null : DateOnly.FromDayNumber((int)number);
    }

    /// <summary>The weeks of a month, Monday first, each with its ISO week number and its seven days (null outside the month).</summary>
    public static IReadOnlyList<(int Week, DateOnly?[] Days)> Month(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);

        // The Monday on or before the 1st: DayOfWeek counts from Sunday, ISO weeks from Monday.
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var weeks = new List<(int, DateOnly?[])>();
        for (var monday = start; monday <= last; monday = monday.AddDays(7))
        {
            var days = new DateOnly?[7];
            for (var i = 0; i < 7; i++)
            {
                var day = monday.AddDays(i);
                days[i] = day.Month == month ? day : null;
            }

            weeks.Add((ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue)), days));
        }

        return weeks;
    }
}

/// <summary>The time now, here or in another zone: what the clock, <c>tools time</c> and <c>time_now</c> all say.</summary>
internal sealed record ClockReading(DateTimeOffset Now, TimeZoneInfo Zone)
{
    /// <summary>The zone's name as Windows gives it, summer or standard as it is now.</summary>
    public string ZoneName => Zone.IsDaylightSavingTime(Now) ? Zone.DaylightName : Zone.StandardName;

    /// <summary>The offset from UTC, as +hh:mm.</summary>
    public string Offset => (Now.Offset < TimeSpan.Zero ? "-" : "+") + Now.Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>For a model: "2026-09-27, Sunday, 21:04:13, Pacific Daylight Time (UTC-07:00)".</summary>
    public string Words => string.Create(CultureInfo.InvariantCulture, $"{Now:yyyy-MM-dd}, {Now.DayOfWeek}, {Now:HH:mm:ss}, {ZoneName} (UTC{Offset}), zone id {Zone.Id}");

    /// <summary>The time now in <paramref name="zoneId"/> (Windows' ids and IANA ids both), or here with none; null for a zone this PC does not know.</summary>
    public static ClockReading? Read(string? zoneId, DateTimeOffset utcNow)
    {
        var zone = TimeZoneInfo.Local;
        if (zoneId is { Length: > 0 } && !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out zone))
        {
            return null;
        }

        return new ClockReading(TimeZoneInfo.ConvertTime(utcNow, zone!), zone!);
    }
}
