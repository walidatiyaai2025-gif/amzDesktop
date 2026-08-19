using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class SalesTrafficStore(string databasePath)
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
CREATE TABLE IF NOT EXISTS sales_traffic_daily (
    marketplace_id TEXT NOT NULL,
    date TEXT NOT NULL,
    ordered_product_sales REAL NOT NULL,
    units_ordered INTEGER NOT NULL,
    total_order_items INTEGER NOT NULL,
    sessions INTEGER NOT NULL,
    page_views INTEGER NOT NULL,
    buy_box_percentage REAL NOT NULL,
    unit_session_percentage REAL NOT NULL,
    units_refunded INTEGER NOT NULL,
    refund_rate REAL NOT NULL,
    average_selling_price REAL NOT NULL,
    currency TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (marketplace_id, date)
);
""";
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> SaveFromJsonAsync(string marketplaceId, string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        await InitializeAsync(ct);
        var rows = Parse(marketplaceId, json);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO sales_traffic_daily(marketplace_id,date,ordered_product_sales,units_ordered,total_order_items,sessions,page_views,buy_box_percentage,unit_session_percentage,units_refunded,refund_rate,average_selling_price,currency,updated_at_utc)
VALUES($m,$date,$sales,$units,$items,$sessions,$views,$bb,$usp,$refunds,$rr,$asp,$currency,$now)
ON CONFLICT(marketplace_id,date) DO UPDATE SET ordered_product_sales=excluded.ordered_product_sales,units_ordered=excluded.units_ordered,total_order_items=excluded.total_order_items,sessions=excluded.sessions,page_views=excluded.page_views,buy_box_percentage=excluded.buy_box_percentage,unit_session_percentage=excluded.unit_session_percentage,units_refunded=excluded.units_refunded,refund_rate=excluded.refund_rate,average_selling_price=excluded.average_selling_price,currency=excluded.currency,updated_at_utc=excluded.updated_at_utc;
""";
            command.Parameters.AddWithValue("$m", row.MarketplaceId); command.Parameters.AddWithValue("$date", row.Date.ToString("yyyy-MM-dd")); command.Parameters.AddWithValue("$sales", row.OrderedProductSales); command.Parameters.AddWithValue("$units", row.UnitsOrdered); command.Parameters.AddWithValue("$items", row.TotalOrderItems); command.Parameters.AddWithValue("$sessions", row.Sessions); command.Parameters.AddWithValue("$views", row.PageViews); command.Parameters.AddWithValue("$bb", row.BuyBoxPercentage); command.Parameters.AddWithValue("$usp", row.UnitSessionPercentage); command.Parameters.AddWithValue("$refunds", row.UnitsRefunded); command.Parameters.AddWithValue("$rr", row.RefundRate); command.Parameters.AddWithValue("$asp", row.AverageSellingPrice); command.Parameters.AddWithValue("$currency", row.Currency); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
        return rows.Count;
    }

    public async Task<IReadOnlyList<TrafficSummaryRow>> QueryAsync(string marketplaceId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = new List<TrafficSummaryRow>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT date,ordered_product_sales,units_ordered,sessions,page_views,unit_session_percentage,buy_box_percentage,units_refunded,refund_rate,average_selling_price FROM sales_traffic_daily WHERE marketplace_id=$m AND date >= $from AND date <= $to ORDER BY date;";
        command.Parameters.AddWithValue("$m", marketplaceId); command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd")); command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var date = DateTime.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            rows.Add(new TrafficSummaryRow(date, D(reader, 1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), D(reader, 5), D(reader, 6), reader.GetInt32(7), D(reader, 8), D(reader, 9)));
        }
        return rows;
    }

    public static IReadOnlyList<SalesTrafficDailyRow> Parse(string marketplaceId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("salesAndTrafficByDate", out var data) || data.ValueKind != JsonValueKind.Array) return Array.Empty<SalesTrafficDailyRow>();
        var result = new List<SalesTrafficDailyRow>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("date", out var dateValue) || !DateTime.TryParse(dateValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var sales = item.TryGetProperty("salesByDate", out var salesNode) ? salesNode : default;
            var traffic = item.TryGetProperty("trafficByDate", out var trafficNode) ? trafficNode : default;
            var currency = "";
            var orderedSales = Money(sales, "orderedProductSales", ref currency);
            var averageSellingPrice = Money(sales, "averageSellingPrice", ref currency);
            result.Add(new SalesTrafficDailyRow(
                marketplaceId, date.Date, "", "", "",
                orderedSales,
                Int(sales, "unitsOrdered"), Int(sales, "totalOrderItems"),
                Long(traffic, "sessions"), Long(traffic, "pageViews"),
                Decimal(traffic, "buyBoxPercentage"), Decimal(traffic, "unitSessionPercentage"),
                Int(sales, "unitsRefunded"), Decimal(sales, "refundRate"), averageSellingPrice, currency));
        }
        return result;
    }

    private static decimal Money(JsonElement parent, string name, ref string currency)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var money) || money.ValueKind != JsonValueKind.Object) return 0;
        if (money.TryGetProperty("currencyCode", out var c) && c.ValueKind == JsonValueKind.String) currency = c.GetString() ?? currency;
        return Decimal(money, "amount");
    }
    private static int Int(JsonElement parent, string name) => (int)Long(parent, name);
    private static long Long(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0;
    }
    private static decimal Decimal(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0;
    }
    private static decimal D(SqliteDataReader reader, int index) => Convert.ToDecimal(reader.GetValue(index), CultureInfo.InvariantCulture);
}
