using System.Globalization;
using System.Text;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public static class AdsCsvImporter
{
    public static async Task<IReadOnlyList<AdsPerformanceRow>> ReadAsync(string path, string marketplaceId, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(path, ct);
        var records = ParseCsv(text);
        if (records.Count < 2) return Array.Empty<AdsPerformanceRow>();

        var headers = records[0].Select(Normalize).ToArray();
        var result = new List<AdsPerformanceRow>();
        foreach (var values in records.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            string Get(params string[] aliases)
            {
                foreach (var alias in aliases)
                {
                    var wanted = Normalize(alias);
                    var index = Array.FindIndex(headers, h => h == wanted);
                    if (index >= 0 && index < values.Count) return values[index].Trim();
                }
                return "";
            }

            var dateText = Get("Date", "Start Date", "StartDate", "Report Date");
            if (!TryParseDate(dateText, out var date)) continue;
            var hour = ParseHour(Get("Hour", "Start Time", "Time"));
            var local = DateTime.SpecifyKind(date.Date.AddHours(hour), DateTimeKind.Unspecified);
            var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, marketplaceZone), TimeSpan.Zero);
            var endUtc = startUtc.AddHours(string.IsNullOrWhiteSpace(Get("Hour", "Start Time", "Time")) ? 24 : 1);

            result.Add(new AdsPerformanceRow(
                startUtc,
                endUtc,
                marketplaceId,
                Get("Campaign ID", "CampaignId"),
                Get("Campaign Name", "Campaign"),
                Get("Ad Group ID", "AdGroupId"),
                Get("Ad Group Name", "Ad Group"),
                Get("Targeting", "Keyword", "Keyword Text", "Target"),
                Get("Customer Search Term", "Search Term", "Search term"),
                Get("Placement", "Placement Classification"),
                Long(Get("Impressions")),
                Long(Get("Clicks")),
                Decimal(Get("Spend", "Cost")),
                Int(Get("Purchases", "7 Day Total Orders (#)", "14 Day Total Orders (#)", "Orders")),
                Int(Get("Units sold", "7 Day Total Units (#)", "14 Day Total Units (#)", "Units")),
                Decimal(Get("Sales", "7 Day Total Sales", "14 Day Total Sales", "Attributed Sales")),
                Get("Currency", "Currency Code"),
                "Amazon Ads CSV: " + Path.GetFileName(path)));
        }
        return result;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted) { row.Add(field.ToString()); field.Clear(); }
            else if ((ch == '\n' || ch == '\r') && !quoted)
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); if (row.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(row); }
        return rows;
    }

    private static string Normalize(string value) => new(value.Trim().TrimStart('\uFEFF').ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static bool TryParseDate(string value, out DateTime date) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    private static int ParseHour(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)) return Math.Clamp(hour, 0, 23);
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var time)) return time.Hour;
        return 0;
    }
    private static long Long(string value) => long.TryParse(CleanNumber(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static int Int(string value) => int.TryParse(CleanNumber(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static decimal Decimal(string value) => decimal.TryParse(CleanNumber(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0m;
    private static string CleanNumber(string value) => value.Replace("$", "").Replace("%", "").Replace(",", "").Trim();
}
