using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class HistoricalSpApiClient(HttpClient httpClient)
{
    private const string Endpoint = "https://sellingpartnerapi-na.amazon.com";

    public async Task<IReadOnlyList<HourlySalesPoint>> GetDailySalesAsync(string marketplaceId, string accessToken, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 729);
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-days);
        var interval = $"{start:yyyy-MM-dd'T'HH:mm:ss'Z'}--{end:yyyy-MM-dd'T'HH:mm:ss'Z'}";
        var path = $"/sales/v1/orderMetrics?marketplaceIds={Uri.EscapeDataString(marketplaceId)}&interval={Uri.EscapeDataString(interval)}&granularity=Day&granularityTimeZone=UTC&buyerType=All";
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + path);
        request.Headers.TryAddWithoutValidation("x-amz-access-token", accessToken);
        request.Headers.TryAddWithoutValidation("x-amz-date", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("user-agent", "WALKA-Amazon-Analyzer/0.4 (Language=CSharp; Platform=Windows)");
        using var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Historical Sales API failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{Trim(json)}");

        using var doc = JsonDocument.Parse(json);
        var rows = new List<HourlySalesPoint>();
        if (!doc.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Array) return rows;
        foreach (var row in payload.EnumerateArray())
        {
            var parts = ReadString(row, "interval").Split("--", StringSplitOptions.RemoveEmptyEntries);
            var startUtc = parts.Length > 0 && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s) ? s : DateTimeOffset.MinValue;
            var endUtc = parts.Length > 1 && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : startUtc;
            var currency = ""; decimal sales = 0, avg = 0;
            if (row.TryGetProperty("totalSales", out var total) && total.ValueKind == JsonValueKind.Object) { currency = ReadString(total, "currencyCode"); sales = ReadDecimal(total, "amount"); }
            if (row.TryGetProperty("averageUnitPrice", out var average) && average.ValueKind == JsonValueKind.Object) { if (string.IsNullOrWhiteSpace(currency)) currency = ReadString(average, "currencyCode"); avg = ReadDecimal(average, "amount"); }
            rows.Add(new HourlySalesPoint(startUtc, endUtc, ReadInt(row, "orderCount"), ReadInt(row, "unitCount"), ReadInt(row, "orderItemCount"), sales, avg, currency));
        }
        return rows;
    }

    private static string ReadString(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), _ => "" } : "";
    private static int ReadInt(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.Number when value.TryGetInt32(out var n) => n, JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n, _ => 0 } : 0;
    private static decimal ReadDecimal(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.Number when value.TryGetDecimal(out var n) => n, JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => n, _ => 0m } : 0m;
    private static string Trim(string text) => text.Length <= 1000 ? text : text[..1000] + "…";
}
