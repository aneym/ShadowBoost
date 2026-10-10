namespace ShadowBoost.Core;

public static class SunTimes
{
    public static (DateTimeOffset? Sunrise, DateTimeOffset? Sunset) For(
        DateOnly date, double latitude, double longitude, TimeZoneInfo tz)
    {
        var rise = UtcHour(date, latitude, longitude, true);
        var set = UtcHour(date, latitude, longitude, false);
        if (rise is null || set is null) return (null, null);
        return (LocalEvent(date, rise.Value, tz), LocalEvent(date, set.Value, tz));
    }

    // NOAA's day-of-year solar approximation, with atmospheric refraction at 90.833 degrees.
    private static double? UtcHour(DateOnly date, double latitude, double longitude, bool sunrise)
    {
        double longitudeHour = longitude / 15;
        double t = date.DayOfYear + ((sunrise ? 6 : 18) - longitudeHour) / 24;
        double meanAnomaly = 0.9856 * t - 3.289;
        double trueLongitude = Wrap(meanAnomaly + 1.916 * Sin(meanAnomaly) +
            0.020 * Sin(2 * meanAnomaly) + 282.634, 360);
        double rightAscension = Wrap(Degrees(Math.Atan(0.91764 * Math.Tan(Radians(trueLongitude)))), 360);
        rightAscension += Math.Floor(trueLongitude / 90) * 90 - Math.Floor(rightAscension / 90) * 90;
        rightAscension /= 15;
        double sinDeclination = 0.39782 * Sin(trueLongitude);
        double cosDeclination = Math.Cos(Math.Asin(sinDeclination));
        double cosHour = (Math.Cos(Radians(90.833)) - sinDeclination * Sin(latitude)) /
            (cosDeclination * Math.Cos(Radians(latitude)));
        if (!double.IsFinite(cosHour) || cosHour is < -1 or > 1) return null;
        double hourAngle = Degrees(Math.Acos(cosHour));
        if (sunrise) hourAngle = 360 - hourAngle;
        double localMeanTime = hourAngle / 15 + rightAscension - 0.06571 * t - 6.622;
        return Wrap(localMeanTime - longitudeHour, 24);
    }

    private static DateTimeOffset LocalEvent(DateOnly date, double hour, TimeZoneInfo tz)
    {
        var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        // UTC midnight can lie on the adjacent local date, especially for sunset in the Americas.
        for (int delta = -1; delta <= 1; delta++)
        {
            var local = TimeZoneInfo.ConvertTime(midnight.AddDays(delta).AddHours(hour), tz);
            if (DateOnly.FromDateTime(local.DateTime) == date) return local;
        }
        throw new ArgumentException("Time zone does not contain this solar event's local date.", nameof(tz));
    }

    private static double Wrap(double value, double modulus) => (value % modulus + modulus) % modulus;
    private static double Radians(double degrees) => degrees * Math.PI / 180;
    private static double Degrees(double radians) => radians * 180 / Math.PI;
    private static double Sin(double degrees) => Math.Sin(Radians(degrees));
}
