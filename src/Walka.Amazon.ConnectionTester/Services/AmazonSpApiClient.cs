using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class AmazonSpApiClient(HttpClient httpClient)
{
    private const string Endpoint = "https://sellingpartnerapi-na.amazon.com";

    public async Task<string> GetAccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        });
        using var response = await httpClient.PostAsync("https://api.amazon.com/auth/o2/token", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, json, "LWA token exchange");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Amazon did not return an access token.");
    }

    public async Task<IReadOnlyList<MarketplaceRow>> GetMarketplacesAsync(string accessToken, CancellationToken ct = default)
    {
        var json = await GetAsync("/sellers/v1/marketplaceParticipations", accessToken, ct);
        using var doc = JsonDocument.Parse(json);
        var rows = new List<MarketplaceRow>();
        foreach (var item in doc.RootElement.GetProperty("payload").EnumerateArray())
        {
            var m = item.GetProperty("marketplace");
            var p = item.GetProperty("participation");
            rows.Add(new MarketplaceRow(
                ReadString(m, "id"), ReadString(m, "name"), ReadString(m, "countryCode"), ReadString(m, "defaultCurrencyCode"),
                p.TryGetProperty("isParticipating", out var ip) && ip.ValueKind is JsonValueKind.True or JsonValueKind.False && ip.GetBoolean(),
                p.TryGetProperty("hasSuspendedListings", out var hs) && hs.ValueKind is JsonValueKind.True or JsonValueKind.False && hs.GetBoolean()));
        }
        return rows;
    }

    public async Task<SalesSummary> GetLast7DaysSalesAsync(string marketplaceId, string accessToken, CancellationToken ct = default)
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-7);
        var path = BuildSalesPath(marketplaceId, start, end, "Day", includeTimezone: true);
        var rows = ParseSalesPoints(await GetAsync(path, accessToken, ct));
        return new SalesSummary(rows.Sum(x => x.Orders), rows.Sum(x => x.Units), rows.Sum(x => x.OrderItems), rows.Sum(x => x.Sales), rows.Select(x => x.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "");
    }

    public async Task<IReadOnlyList<HourlySalesPoint>> GetHourlySalesAsync(string marketplaceId, string accessToken, int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-days);
        var json = await GetAsync(BuildSalesPath(marketplaceId, start, end, "Hour", includeTimezone: false), accessToken, ct);
        return ParseSalesPoints(json);
    }

    public async Task<IReadOnlyList<InventoryRow>> GetInventoryAsync(string marketplaceId, string accessToken, CancellationToken ct = default)
    {
        var path = $"/fba/inventory/v1/summaries?details=true&granularityType=Marketplace&granularityId={Uri.EscapeDataString(marketplaceId)}&marketplaceIds={Uri.EscapeDataString(marketplaceId)}";
        var json = await GetAsync(path, accessToken, ct);
        using var doc = JsonDocument.Parse(json);
        var rows = new List<InventoryRow>();
        foreach (var item in doc.RootElement.GetProperty("payload").GetProperty("inventorySummaries").EnumerateArray())
        {
            var d = item.TryGetProperty("inventoryDetails", out var details) ? details : default;
            int Q(string name) => d.ValueKind == JsonValueKind.Object ? ReadInt32(d, name) : 0;
            rows.Add(new InventoryRow(ReadString(item, "sellerSku"), ReadString(item, "asin"), ReadString(item, "fnSku"), ReadString(item, "productName"), Q("fulfillableQuantity"), Q("reservedQuantity"), Q("inboundWorkingQuantity") + Q("inboundShippedQuantity") + Q("inboundReceivingQuantity"), ReadInt32(item, "totalQuantity")));
        }
        return rows;
    }

    public async Task<IReadOnlyList<PriceSnapshotRow>> GetPriceSnapshotsAsync(string marketplaceId, IReadOnlyList<InventoryRow> inventory, string accessToken, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new List<PriceSnapshotRow>();
        var skus = inventory.Select(x => x.SellerSku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var batch in skus.Chunk(20))
        {
            var query = string.Join("&", batch.Select(s => "Skus=" + Uri.EscapeDataString(s)));
            var path = $"/products/pricing/v0/price?MarketplaceId={Uri.EscapeDataString(marketplaceId)}&ItemType=Sku&{query}";
            var json = await GetAsync(path, accessToken, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in payload.EnumerateArray())
            {
                var sku = ReadStringIgnoreCase(item, "SellerSKU");
                var asin = ReadStringIgnoreCase(item, "ASIN");
                var status = ReadStringIgnoreCase(item, "status");
                decimal listing = 0, shipping = 0, landed = 0;
                var currency = "";
                if (TryGetPropertyIgnoreCase(item, "Product", out var product) && TryGetPropertyIgnoreCase(product, "Offers", out var offers) && offers.ValueKind == JsonValueKind.Array && offers.GetArrayLength() > 0)
                {
                    var offer = offers[0];
                    if (TryGetPropertyIgnoreCase(offer, "BuyingPrice", out var buying))
                    {
                        ReadMoney(buying, "ListingPrice", ref listing, ref currency);
                        ReadMoney(buying, "Shipping", ref shipping, ref currency);
                        ReadMoney(buying, "LandedPrice", ref landed, ref currency);
                    }
                }
                rows.Add(new PriceSnapshotRow(now, sku, asin, status, listing, shipping, landed, currency, null));
            }
            if (skus.Length > 20) await Task.Delay(2100, ct);
        }
        return rows;
    }

    public Task<string> GetAllOrdersReportAsync(string marketplaceId, string accessToken, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        RequestAndDownloadReportAsync("GET_FLAT_FILE_ALL_ORDERS_DATA_BY_ORDER_DATE_GENERAL", marketplaceId, start, end, accessToken, null, ct);

    public Task<string> GetFbaReturnsReportAsync(string marketplaceId, string accessToken, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        RequestAndDownloadReportAsync("GET_FBA_FULFILLMENT_CUSTOMER_RETURNS_DATA", marketplaceId, start, end, accessToken, null, ct);

    public Task<string> GetSalesAndTrafficReportAsync(string marketplaceId, string accessToken, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        RequestAndDownloadReportAsync("GET_SALES_AND_TRAFFIC_REPORT", marketplaceId, start, end, accessToken, new Dictionary<string, string> { ["dateGranularity"] = "DAY", ["asinGranularity"] = "SKU" }, ct);

    public async Task<IReadOnlyList<string>> GetFinanceTransactionPagesAsync(string marketplaceId, string accessToken, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        var pages = new List<string>();
        string? nextToken = null;
        do
        {
            var path = $"/finances/2024-06-19/transactions?postedAfter={Uri.EscapeDataString(start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}&postedBefore={Uri.EscapeDataString(end.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}&marketplaceId={Uri.EscapeDataString(marketplaceId)}";
            if (!string.IsNullOrWhiteSpace(nextToken)) path += "&nextToken=" + Uri.EscapeDataString(nextToken);
            var json = await GetAsync(path, accessToken, ct);
            pages.Add(json);
            using var doc = JsonDocument.Parse(json);
            nextToken = TryFindString(doc.RootElement, "nextToken");
            if (pages.Count >= 100) break;
        } while (!string.IsNullOrWhiteSpace(nextToken));
        return pages;
    }

    private async Task<string> RequestAndDownloadReportAsync(string reportType, string marketplaceId, DateTimeOffset start, DateTimeOffset end, string accessToken, Dictionary<string, string>? reportOptions, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["reportType"] = reportType,
            ["marketplaceIds"] = new[] { marketplaceId },
            ["dataStartTime"] = start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["dataEndTime"] = end.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };
        if (reportOptions is not null) body["reportOptions"] = reportOptions;
        var createJson = await SendSpApiAsync(HttpMethod.Post, "/reports/2021-06-30/reports", accessToken, JsonSerializer.Serialize(body), ct);
        using var createDoc = JsonDocument.Parse(createJson);
        var reportId = ReadString(createDoc.RootElement, "reportId");
        if (string.IsNullOrWhiteSpace(reportId)) throw new InvalidOperationException($"Amazon did not return a reportId for {reportType}.");

        string? documentId = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(attempt == 0 ? 1500 : 3000, ct);
            var statusJson = await GetAsync("/reports/2021-06-30/reports/" + Uri.EscapeDataString(reportId), accessToken, ct);
            using var statusDoc = JsonDocument.Parse(statusJson);
            var status = ReadString(statusDoc.RootElement, "processingStatus");
            if (string.Equals(status, "DONE", StringComparison.OrdinalIgnoreCase))
            {
                documentId = ReadString(statusDoc.RootElement, "reportDocumentId");
                break;
            }
            if (string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (string.Equals(status, "FATAL", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Amazon report {reportType} failed with FATAL status.");
        }
        if (string.IsNullOrWhiteSpace(documentId)) throw new TimeoutException($"Timed out waiting for Amazon report {reportType}.");

        var documentJson = await GetAsync("/reports/2021-06-30/documents/" + Uri.EscapeDataString(documentId), accessToken, ct);
        using var documentDoc = JsonDocument.Parse(documentJson);
        var url = ReadString(documentDoc.RootElement, "url");
        var compression = ReadString(documentDoc.RootElement, "compressionAlgorithm");
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException($"Amazon did not return a download URL for {reportType}.");
        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (string.Equals(compression, "GZIP", StringComparison.OrdinalIgnoreCase))
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            await gzip.CopyToAsync(output, ct);
            bytes = output.ToArray();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static IReadOnlyList<HourlySalesPoint> ParseSalesPoints(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<HourlySalesPoint>();
        foreach (var row in doc.RootElement.GetProperty("payload").EnumerateArray())
        {
            var interval = ReadString(row, "interval").Split("--", StringSplitOptions.RemoveEmptyEntries);
            var start = interval.Length > 0 && DateTimeOffset.TryParse(interval[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s) ? s : DateTimeOffset.MinValue;
            var end = interval.Length > 1 && DateTimeOffset.TryParse(interval[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : start;
            var currency = ""; decimal sales = 0, avg = 0;
            if (row.TryGetProperty("totalSales", out var total) && total.ValueKind == JsonValueKind.Object) { currency = ReadString(total, "currencyCode"); sales = ReadDecimal(total, "amount"); }
            if (row.TryGetProperty("averageUnitPrice", out var average) && average.ValueKind == JsonValueKind.Object) { if (string.IsNullOrWhiteSpace(currency)) currency = ReadString(average, "currencyCode"); avg = ReadDecimal(average, "amount"); }
            rows.Add(new HourlySalesPoint(start, end, ReadInt32(row, "orderCount"), ReadInt32(row, "unitCount"), ReadInt32(row, "orderItemCount"), sales, avg, currency));
        }
        return rows;
    }

    private static string BuildSalesPath(string marketplaceId, DateTimeOffset start, DateTimeOffset end, string granularity, bool includeTimezone)
    {
        var interval = $"{start:yyyy-MM-dd'T'HH:mm:ss'Z'}--{end:yyyy-MM-dd'T'HH:mm:ss'Z'}";
        var path = $"/sales/v1/orderMetrics?marketplaceIds={Uri.EscapeDataString(marketplaceId)}&interval={Uri.EscapeDataString(interval)}&granularity={granularity}&buyerType=All";
        if (includeTimezone) path += "&granularityTimeZone=UTC";
        return path;
    }

    private Task<string> GetAsync(string path, string accessToken, CancellationToken ct) => SendSpApiAsync(HttpMethod.Get, path, accessToken, null, ct);

    private async Task<string> SendSpApiAsync(HttpMethod method, string path, string accessToken, string? jsonBody, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Endpoint + path);
        req.Headers.TryAddWithoutValidation("x-amz-access-token", accessToken);
        req.Headers.TryAddWithoutValidation("x-amz-date", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("user-agent", "WALKA-Amazon-Analyzer/0.3 (Language=CSharp; Platform=Windows)");
        if (jsonBody is not null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(req, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, json, path);
        return json;
    }

    private static void ReadMoney(JsonElement parent, string propertyName, ref decimal amount, ref string currency)
    {
        if (!TryGetPropertyIgnoreCase(parent, propertyName, out var money) || money.ValueKind != JsonValueKind.Object) return;
        amount = ReadDecimalIgnoreCase(money, "Amount");
        var code = ReadStringIgnoreCase(money, "CurrencyCode");
        if (!string.IsNullOrWhiteSpace(code)) currency = code;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject()) if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default; return false;
    }

    private static string ReadStringIgnoreCase(JsonElement obj, string propertyName) => TryGetPropertyIgnoreCase(obj, propertyName, out var value) ? ValueAsString(value) : "";
    private static decimal ReadDecimalIgnoreCase(JsonElement obj, string propertyName) => TryGetPropertyIgnoreCase(obj, propertyName, out var value) ? ValueAsDecimal(value) : 0m;
    private static string ReadString(JsonElement obj, string propertyName) => obj.TryGetProperty(propertyName, out var value) ? ValueAsString(value) : "";
    private static decimal ReadDecimal(JsonElement obj, string propertyName) => obj.TryGetProperty(propertyName, out var value) ? ValueAsDecimal(value) : 0m;

    private static string ValueAsString(JsonElement value) => value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => "" };
    private static decimal ValueAsDecimal(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) ? n : value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0m;

    private static int ReadInt32(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number) { if (value.TryGetInt32(out var i)) return i; if (value.TryGetDecimal(out var n)) return decimal.ToInt32(decimal.Truncate(n)); }
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    private static string? TryFindString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                var nested = TryFindString(p.Value, propertyName); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var x in element.EnumerateArray()) { var nested = TryFindString(x, propertyName); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var safeBody = body.Length > 1600 ? body[..1600] + "…" : body;
        throw new HttpRequestException($"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{safeBody}");
    }
}
