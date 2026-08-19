using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class AmazonSpApiClient(HttpClient httpClient)
{
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
                ReadString(m, "id"),
                ReadString(m, "name"),
                ReadString(m, "countryCode"),
                ReadString(m, "defaultCurrencyCode"),
                p.TryGetProperty("isParticipating", out var ip) && ip.ValueKind is JsonValueKind.True or JsonValueKind.False && ip.GetBoolean(),
                p.TryGetProperty("hasSuspendedListings", out var hs) && hs.ValueKind is JsonValueKind.True or JsonValueKind.False && hs.GetBoolean()));
        }
        return rows;
    }

    public async Task<SalesSummary> GetLast7DaysSalesAsync(string marketplaceId, string accessToken, CancellationToken ct = default)
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-7);
        var interval = $"{start:yyyy-MM-dd'T'HH:mm:ss'Z'}--{end:yyyy-MM-dd'T'HH:mm:ss'Z'}";
        var path = $"/sales/v1/orderMetrics?marketplaceIds={Uri.EscapeDataString(marketplaceId)}&interval={Uri.EscapeDataString(interval)}&granularity=Day&granularityTimeZone=UTC&buyerType=All";
        var json = await GetAsync(path, accessToken, ct);
        using var doc = JsonDocument.Parse(json);
        var orders = 0;
        var units = 0;
        var items = 0;
        decimal sales = 0;
        var currency = "";

        foreach (var row in doc.RootElement.GetProperty("payload").EnumerateArray())
        {
            orders += ReadInt32(row, "orderCount");
            units += ReadInt32(row, "unitCount");
            items += ReadInt32(row, "orderItemCount");

            if (row.TryGetProperty("totalSales", out var total) && total.ValueKind == JsonValueKind.Object)
            {
                var code = ReadString(total, "currencyCode");
                if (!string.IsNullOrWhiteSpace(code)) currency = code;
                sales += ReadDecimal(total, "amount");
            }
        }

        return new SalesSummary(orders, units, items, sales, currency);
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

            rows.Add(new InventoryRow(
                ReadString(item, "sellerSku"),
                ReadString(item, "asin"),
                ReadString(item, "fnSku"),
                ReadString(item, "productName"),
                Q("fulfillableQuantity"),
                Q("reservedQuantity"),
                Q("inboundWorkingQuantity") + Q("inboundShippedQuantity") + Q("inboundReceivingQuantity"),
                ReadInt32(item, "totalQuantity")));
        }
        return rows;
    }

    private async Task<string> GetAsync(string path, string accessToken, CancellationToken ct)
    {
        var url = "https://sellingpartnerapi-na.amazon.com" + path;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-amz-access-token", accessToken);
        req.Headers.TryAddWithoutValidation("x-amz-date", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("user-agent", "WALKA-Amazon-ConnectionTester/0.2 (Language=CSharp; Platform=Windows)");
        using var response = await httpClient.SendAsync(req, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, json, path);
        return json;
    }

    private static string ReadString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static int ReadInt32(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var integer)) return integer;
            if (value.TryGetDecimal(out var number)) return decimal.ToInt32(decimal.Truncate(number));
            return 0;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static decimal ReadDecimal(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value)) return 0m;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var safeBody = body.Length > 1200 ? body[..1200] + "…" : body;
        throw new HttpRequestException($"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{safeBody}");
    }
}
