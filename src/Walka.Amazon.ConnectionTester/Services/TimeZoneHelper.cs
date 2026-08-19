namespace Walka.Amazon.ConnectionTester.Services;

public static class TimeZoneHelper
{
    public static readonly TimeZoneInfo Kuwait = TimeZoneInfo.CreateCustomTimeZone(
        "Kuwait Standard Time",
        TimeSpan.FromHours(3),
        "Kuwait Standard Time",
        "Kuwait Standard Time");

    public static DateTimeOffset ToKuwait(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Kuwait);

    public static string Format(DateTimeOffset value, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("yyyy-MM-dd HH:mm");

    public static string FormatKuwait(DateTimeOffset value) => ToKuwait(value).ToString("yyyy-MM-dd HH:mm");

    public static bool HourMatches(int hour, int fromHour, int toHour)
    {
        fromHour = Math.Clamp(fromHour, 0, 23);
        toHour = Math.Clamp(toHour, 0, 23);
        return fromHour <= toHour
            ? hour >= fromHour && hour <= toHour
            : hour >= fromHour || hour <= toHour;
    }
}
