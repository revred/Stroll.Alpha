namespace Stroll.Alpha.Dataset.Integrity;

public sealed class IntegrityCalculator
{
    /// <summary>
    /// Calculate expected minute bars per session (excluding holidays)
    /// Based on standard market hours: 9:30 AM - 4:00 PM ET = 390 minutes
    /// </summary>
    public static int ExpectedMinuteBarsPerSession(DateOnly sessionDate)
    {
        // Standard trading day: 9:30 AM - 4:00 PM ET = 6.5 hours = 390 minutes
        const int standardTradingMinutes = 390;
        
        // Check for holidays and early closes
        if (IsHoliday(sessionDate))
            return 0;
        
        if (IsEarlyClose(sessionDate))
            return 210; // Early close at 1:00 PM ET = 3.5 hours = 210 minutes
        
        return standardTradingMinutes;
    }

    /// <summary>
    /// Calculate expected records for options chain based on DTE and moneyness constraints
    /// </summary>
    public static int ExpectedOptionsRecords(int activeDteBuckets, decimal moneynessWindow)
    {
        // Rough estimate: ~6 strikes per side per DTE bucket within 15% moneyness
        const int strikesPerSidePerDte = 6;
        return activeDteBuckets * strikesPerSidePerDte * 2; // 2 sides (put/call)
    }

    /// <summary>
    /// Validate session window integrity for minute-aligned data
    /// </summary>
    public static (int Expected, int Actual, double IntegrityRatio) ValidateSessionWindow(
        DateOnly sessionDate,
        IEnumerable<DateTime> timestamps)
    {
        var expected = ExpectedMinuteBarsPerSession(sessionDate);
        var actual = timestamps.Count();
        var ratio = expected == 0 ? 0 : (double)actual / expected;
        
        return (expected, actual, ratio);
    }

    /// <summary>
    /// Check if date is a market holiday
    /// </summary>
    private static bool IsHoliday(DateOnly date)
    {
        // Major US holidays when markets are closed
        var year = date.Year;
        
        // Fixed holidays
        var fixedHolidays = new[]
        {
            new DateOnly(year, 1, 1),   // New Year's Day
            new DateOnly(year, 7, 4),   // Independence Day  
            new DateOnly(year, 12, 25)  // Christmas Day
        };
        
        if (fixedHolidays.Contains(date))
            return true;
        
        // Martin Luther King Jr. Day (3rd Monday in January)
        if (IsNthWeekdayOfMonth(date, DayOfWeek.Monday, 3, 1))
            return true;
        
        // Presidents' Day (3rd Monday in February)
        if (IsNthWeekdayOfMonth(date, DayOfWeek.Monday, 3, 2))
            return true;
        
        // Good Friday (Friday before Easter) - simplified approximation
        var easter = CalculateEaster(year);
        if (date == easter.AddDays(-2))
            return true;
        
        // Memorial Day (last Monday in May)
        if (IsLastWeekdayOfMonth(date, DayOfWeek.Monday, 5))
            return true;
        
        // Labor Day (1st Monday in September)  
        if (IsNthWeekdayOfMonth(date, DayOfWeek.Monday, 1, 9))
            return true;
        
        // Thanksgiving (4th Thursday in November)
        if (IsNthWeekdayOfMonth(date, DayOfWeek.Thursday, 4, 11))
            return true;
        
        return false;
    }

    /// <summary>
    /// Check if date is an early close day (typically day after Thanksgiving and Christmas Eve)
    /// </summary>
    private static bool IsEarlyClose(DateOnly date)
    {
        var year = date.Year;
        
        // Day after Thanksgiving (4th Friday in November)
        if (IsNthWeekdayOfMonth(date, DayOfWeek.Friday, 4, 11))
            return true;
        
        // Christmas Eve (if on weekday)
        var christmasEve = new DateOnly(year, 12, 24);
        if (date == christmasEve && christmasEve.DayOfWeek != DayOfWeek.Saturday && christmasEve.DayOfWeek != DayOfWeek.Sunday)
            return true;
        
        return false;
    }

    private static bool IsNthWeekdayOfMonth(DateOnly date, DayOfWeek targetDayOfWeek, int occurrence, int month)
    {
        if (date.Month != month) return false;
        if (date.DayOfWeek != targetDayOfWeek) return false;
        
        var firstDayOfMonth = new DateOnly(date.Year, month, 1);
        var firstTargetDayOfMonth = firstDayOfMonth;
        
        while (firstTargetDayOfMonth.DayOfWeek != targetDayOfWeek)
        {
            firstTargetDayOfMonth = firstTargetDayOfMonth.AddDays(1);
        }
        
        var targetDate = firstTargetDayOfMonth.AddDays((occurrence - 1) * 7);
        return date == targetDate;
    }
    
    private static bool IsLastWeekdayOfMonth(DateOnly date, DayOfWeek targetDayOfWeek, int month)
    {
        if (date.Month != month) return false;
        if (date.DayOfWeek != targetDayOfWeek) return false;
        
        var lastDayOfMonth = new DateOnly(date.Year, month, DateTime.DaysInMonth(date.Year, month));
        var lastTargetDayOfMonth = lastDayOfMonth;
        
        while (lastTargetDayOfMonth.DayOfWeek != targetDayOfWeek)
        {
            lastTargetDayOfMonth = lastTargetDayOfMonth.AddDays(-1);
        }
        
        return date == lastTargetDayOfMonth;
    }
    
    private static DateOnly CalculateEaster(int year)
    {
        // Simplified Easter calculation (Gregorian calendar)
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int n = (h + l - 7 * m + 114) / 31;
        int p = (h + l - 7 * m + 114) % 31;
        
        return new DateOnly(year, n, p + 1);
    }
}