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
                m.GetProperty("id").GetString() ?? "",
                m.GetProperty("name").GetString() ?? "",
                m.GetProperty("countryCode").GetString() ?? "",
                m.GetProperty("defaultCurrencyCode").GetString() ?? "",
                p.TryGetProperty("isParticipating", out var ip) && ip.GetBoolean(),
                p.TryGetProperty("hasSuspendedListings", out var hs) && hs.GetBoolean()));
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
        var orders = 0; var units = 0; var items = 0; decimal sales = 0; var currency = "";
        foreach (var row in doc.RootElement.GetProperty("payload").EnumerateArray())
        {
            orders += row.GetProperty("orderCount").GetInt32();
            units += row.GetProperty("unitCount").GetInt32();
            items += row.GetProperty("orderItemCount").GetInt32();
            if (row.TryGetProperty("totalSales", out var total))
            {
                currency = total.TryGetProperty("currencyCode", out var c) ? c.GetString() ?? currency : currency;
                if (total.TryGetProperty("amount", out var a) && decimal.TryParse(a.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) sales += n;
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
            int Q(string name) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
            rows.Add(new InventoryRow(
                item.TryGetProperty("sellerSku", out var sku) ? sku.GetString() ?? "" : "",
                item.TryGetProperty("asin", out var asin) ? asin.GetString() ?? "" : "",
                item.TryGetProperty("fnSku", out var fn) ? fn.GetString() ?? "" : "",
                item.TryGetProperty("productName", out var pn) ? pn.GetString() ?? "" : "",
                Q("fulfillableQuantity"), Q("reservedQuantity"), Q("inboundWorkingQuantity") + Q("inboundShippedQuantity") + Q("inboundReceivingQuantity"),
                item.TryGetProperty("totalQuantity", out var tq) ? tq.GetInt32() : 0));
        }
        return rows;
    }

    private async Task<string> GetAsync(string path, string accessToken, CancellationToken ct)
    {
        var url = "https://sellingpartnerapi-na.amazon.com" + path;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-amz-access-token", accessToken);
        req.Headers.TryAddWithoutValidation("x-amz-date", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("user-agent", "WALKA-Amazon-ConnectionTester/0.1 (Language=CSharp; Platform=Windows)");
        using var response = await httpClient.SendAsync(req, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, json, path);
        return json;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var safeBody = body.Length > 1200 ? body[..1200] + "…" : body;
        throw new HttpRequestException($"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{safeBody}");
    }
}
