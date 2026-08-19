using System.Globalization;
using Microsoft.Data.Sqlite;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class AdsSalesBridgeService(string databasePath)
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    public async Task<IReadOnlyList<SalesAttributionRow>> QueryAsync(HistoryFilter filter, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        var totalByDate = new Dictionary<DateTime, Totals>();
        var adByDate = new Dictionary<DateTime, AdTotals>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT amazon_order_id,purchase_date_utc,item_price,seller_sku,asin FROM orders WHERE marketplace_id=$m AND purchase_date_utc IS NOT NULL;";
            command.Parameters.AddWithValue("$m", filter.MarketplaceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var seenOrdersByDate = new Dictionary<DateTime, HashSet<string>>();
            while (await reader.ReadAsync(ct))
            {
                var utc = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var local = TimeZoneInfo.ConvertTime(utc, marketplaceZone);
                if (local.Date < filter.FromDate.Date || local.Date > filter.ToDate.Date || !TimeZoneHelper.HourMatches(local.Hour, filter.FromHour, filter.ToHour)) continue;
                var sku = reader.GetString(3); var asin = reader.GetString(4);
                if (!string.IsNullOrWhiteSpace(filter.SellerSku) && !sku.Contains(filter.SellerSku, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(filter.Asin) && !asin.Contains(filter.Asin, StringComparison.OrdinalIgnoreCase)) continue;
                if (!totalByDate.TryGetValue(local.Date, out var totals)) totalByDate[local.Date] = totals = new Totals();
                totals.Sales += Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                if (!seenOrdersByDate.TryGetValue(local.Date, out var ids)) seenOrdersByDate[local.Date] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ids.Add(reader.GetString(0));
                totals.Orders = ids.Count;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT period_start_utc,attributed_orders,attributed_sales,spend FROM ads_performance WHERE marketplace_id=$m;";
            command.Parameters.AddWithValue("$m", filter.MarketplaceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var utc = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var local = TimeZoneInfo.ConvertTime(utc, marketplaceZone);
                if (local.Date < filter.FromDate.Date || local.Date > filter.ToDate.Date || !TimeZoneHelper.HourMatches(local.Hour, filter.FromHour, filter.ToHour)) continue;
                if (!adByDate.TryGetValue(local.Date, out var totals)) adByDate[local.Date] = totals = new AdTotals();
                totals.Orders += reader.GetInt32(1);
                totals.Sales += Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                totals.Spend += Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
            }
        }

        return totalByDate.Keys.Union(adByDate.Keys).OrderBy(x => x).Select(date =>
        {
            totalByDate.TryGetValue(date, out var total); adByDate.TryGetValue(date, out var ad);
            var totalOrders = total?.Orders ?? 0; var totalSales = total?.Sales ?? 0m; var adOrders = ad?.Orders ?? 0; var adSales = ad?.Sales ?? 0m; var spend = ad?.Spend ?? 0m;
            return new SalesAttributionRow(date, date.ToString("yyyy-MM-dd"), totalOrders, totalSales, adOrders, adSales,
                Math.Max(0, totalOrders - adOrders), Math.Max(0m, totalSales - adSales), totalSales == 0 ? 0 : 100m * adSales / totalSales,
                spend, adSales == 0 ? 0 : 100m * spend / adSales, totalSales == 0 ? 0 : 100m * spend / totalSales);
        }).ToArray();
    }

    private sealed class Totals { public int Orders; public decimal Sales; }
    private sealed class AdTotals { public int Orders; public decimal Sales; public decimal Spend; }
}
