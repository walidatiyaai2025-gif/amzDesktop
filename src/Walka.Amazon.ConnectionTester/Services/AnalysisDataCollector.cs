using System.Globalization;
using System.Text;
using System.Text.Json;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class AnalysisDataCollector(AmazonSpApiClient api)
{
    public async Task<AnalysisPackResult> CollectAsync(
        string marketplaceId,
        string accessToken,
        IReadOnlyList<InventoryRow> inventory,
        TimeZoneInfo analysisTimeZone,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var start30 = now.AddDays(-30);
        var start90 = now.AddDays(-90);
        var outputFolder = CreateOutputFolder(marketplaceId, now);

        progress?.Invoke("Collecting hourly Sales API metrics for the last 30 days…");
        var hourly = await api.GetHourlySalesAsync(marketplaceId, accessToken, 30, ct);
        var hourOfDay = BuildHourOfDay(hourly, analysisTimeZone);
        await WriteJsonAsync(Path.Combine(outputFolder, "hourly-sales-30d.json"), hourly, ct);
        await WriteJsonAsync(Path.Combine(outputFolder, "hour-of-day-summary.json"), hourOfDay, ct);

        progress?.Invoke("Saving current FBA inventory snapshot…");
        await WriteJsonAsync(Path.Combine(outputFolder, "inventory-snapshot.json"), inventory, ct);

        progress?.Invoke("Collecting current seller price snapshots…");
        var prices = await api.GetPriceSnapshotsAsync(marketplaceId, inventory, accessToken, ct);
        await WriteJsonAsync(Path.Combine(outputFolder, "price-snapshot.json"), prices, ct);

        progress?.Invoke("Requesting 30-day All Orders report (item-level purchase time, SKU, ASIN, price, promotions)…");
        var ordersRaw = await api.GetAllOrdersReportAsync(marketplaceId, accessToken, start30, now, ct);
        var orders = ParseOrders(ordersRaw);
        await File.WriteAllTextAsync(Path.Combine(outputFolder, "orders-30d.tsv"), ordersRaw, new UTF8Encoding(false), ct);
        await WriteJsonAsync(Path.Combine(outputFolder, "orders-30d.json"), orders, ct);

        progress?.Invoke("Requesting 30-day FBA customer returns report (reason, disposition, comments)…");
        var returnsRaw = await api.GetFbaReturnsReportAsync(marketplaceId, accessToken, start30, now, ct);
        var returns = ParseReturns(returnsRaw);
        await File.WriteAllTextAsync(Path.Combine(outputFolder, "returns-30d.tsv"), returnsRaw, new UTF8Encoding(false), ct);
        await WriteJsonAsync(Path.Combine(outputFolder, "returns-30d.json"), returns, ct);

        progress?.Invoke("Requesting Sales & Traffic report (sessions/page views/Buy Box/conversion inputs)…");
        var trafficRaw = await api.GetSalesAndTrafficReportAsync(marketplaceId, accessToken, start30, now, ct);
        await File.WriteAllTextAsync(Path.Combine(outputFolder, "sales-traffic-30d.json"), trafficRaw, new UTF8Encoding(false), ct);

        progress?.Invoke("Collecting Finance transactions for the last 90 days (fees/refunds/settlement inputs)…");
        var financePages = await api.GetFinanceTransactionPagesAsync(marketplaceId, accessToken, start90, now.AddMinutes(-3), ct);
        for (var i = 0; i < financePages.Count; i++)
            await File.WriteAllTextAsync(Path.Combine(outputFolder, $"finance-90d-page-{i + 1:000}.json"), financePages[i], new UTF8Encoding(false), ct);

        var manifest = new
        {
            createdAtUtc = now,
            marketplaceId,
            analysisTimeZone = analysisTimeZone.Id,
            hourlySalesPoints = hourly.Count,
            orderLines = orders.Count,
            returnLines = returns.Count,
            priceSnapshots = prices.Count,
            financePages = financePages.Count,
            files = Directory.GetFiles(outputFolder).Select(Path.GetFileName).OrderBy(x => x).ToArray()
        };
        await WriteJsonAsync(Path.Combine(outputFolder, "manifest.json"), manifest, ct);

        return new AnalysisPackResult(outputFolder, hourly, hourOfDay, orders, returns, prices, trafficRaw, financePages);
    }

    private static IReadOnlyList<HourOfDaySummary> BuildHourOfDay(IReadOnlyList<HourlySalesPoint> hourly, TimeZoneInfo zone)
    {
        var converted = hourly
            .Where(x => x.IntervalStartUtc != DateTimeOffset.MinValue)
            .Select(x => new { Point = x, Local = TimeZoneInfo.ConvertTime(x.IntervalStartUtc, zone) })
            .ToArray();

        return Enumerable.Range(0, 24).Select(hour =>
        {
            var rows = converted.Where(x => x.Local.Hour == hour).ToArray();
            var orders = rows.Sum(x => x.Point.Orders);
            var units = rows.Sum(x => x.Point.Units);
            var sales = rows.Sum(x => x.Point.Sales);
            var observedDays = Math.Max(1, rows.Select(x => x.Local.Date).Distinct().Count());
            var nextHour = (hour + 1) % 24;
            return new HourOfDaySummary(
                hour,
                $"{hour:00}:00–{nextHour:00}:00",
                orders,
                units,
                sales,
                orders == 0 ? 0 : sales / orders,
                (decimal)orders / observedDays);
        }).OrderByDescending(x => x.OrdersPerObservedDay).ThenByDescending(x => x.Sales).ToArray();
    }

    public static IReadOnlyList<OrderLineRow> ParseOrders(string tsv)
    {
        var rows = ParseTsv(tsv);
        return rows.Select(r => new OrderLineRow(
            Get(r, "amazon-order-id"),
            Date(Get(r, "purchase-date")),
            Get(r, "order-status"),
            Get(r, "fulfillment-channel"),
            Get(r, "product-name"),
            Get(r, "sku"),
            Get(r, "asin"),
            Int(Get(r, "quantity")),
            Get(r, "currency"),
            Decimal(Get(r, "item-price")),
            Decimal(Get(r, "item-promotion-discount")),
            Decimal(Get(r, "ship-promotion-discount")),
            Get(r, "promotion-ids"))).ToArray();
    }

    public static IReadOnlyList<ReturnRow> ParseReturns(string tsv)
    {
        var rows = ParseTsv(tsv);
        return rows.Select(r => new ReturnRow(
            Date(Get(r, "return-date")),
            Get(r, "order-id"),
            Get(r, "sku"),
            Get(r, "asin"),
            Get(r, "fnsku"),
            Get(r, "product-name"),
            Int(Get(r, "quantity")),
            Get(r, "fulfillment-center-id"),
            Get(r, "detailed-disposition"),
            Get(r, "reason"),
            Get(r, "status"),
            Get(r, "customer-comments"))).ToArray();
    }

    private static List<Dictionary<string, string>> ParseTsv(string tsv)
    {
        var lines = tsv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return [];
        var headers = lines[0].TrimStart('\uFEFF').Split('\t').Select(x => x.Trim()).ToArray();
        var result = new List<Dictionary<string, string>>(Math.Max(0, lines.Length - 1));
        foreach (var line in lines.Skip(1))
        {
            var values = line.Split('\t');
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++) row[headers[i]] = i < values.Length ? values[i] : "";
            result.Add(row);
        }
        return result;
    }

    private static string Get(Dictionary<string, string> row, string key) => row.TryGetValue(key, out var value) ? value : "";
    private static int Int(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static decimal Decimal(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0m;
    private static DateTimeOffset? Date(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string CreateOutputFolder(string marketplaceId, DateTimeOffset timestamp)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WALKA.Analyzer", "Data", marketplaceId);
        var folder = Path.Combine(root, timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);
}
