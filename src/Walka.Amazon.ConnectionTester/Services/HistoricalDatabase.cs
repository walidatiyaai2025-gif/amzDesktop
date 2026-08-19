using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class HistoricalDatabase
{
    public string DatabasePath { get; }
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    public HistoricalDatabase()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WALKA.Analyzer", "Data");
        Directory.CreateDirectory(root);
        DatabasePath = Path.Combine(root, "walka-history.db");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS collections (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    marketplace_id TEXT NOT NULL,
    collected_at_utc TEXT NOT NULL,
    source TEXT NOT NULL,
    note TEXT NULL
);
CREATE TABLE IF NOT EXISTS hourly_sales (
    marketplace_id TEXT NOT NULL,
    interval_start_utc TEXT NOT NULL,
    interval_end_utc TEXT NOT NULL,
    orders INTEGER NOT NULL,
    units INTEGER NOT NULL,
    order_items INTEGER NOT NULL,
    sales REAL NOT NULL,
    average_unit_price REAL NOT NULL,
    currency TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (marketplace_id, interval_start_utc)
);
CREATE TABLE IF NOT EXISTS daily_sales (
    marketplace_id TEXT NOT NULL,
    interval_start_utc TEXT NOT NULL,
    interval_end_utc TEXT NOT NULL,
    orders INTEGER NOT NULL,
    units INTEGER NOT NULL,
    order_items INTEGER NOT NULL,
    sales REAL NOT NULL,
    average_unit_price REAL NOT NULL,
    currency TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (marketplace_id, interval_start_utc)
);
CREATE TABLE IF NOT EXISTS orders (
    row_key TEXT PRIMARY KEY,
    marketplace_id TEXT NOT NULL,
    amazon_order_id TEXT NOT NULL,
    purchase_date_utc TEXT NULL,
    order_status TEXT NOT NULL,
    fulfillment_channel TEXT NOT NULL,
    product_name TEXT NOT NULL,
    seller_sku TEXT NOT NULL,
    asin TEXT NOT NULL,
    quantity INTEGER NOT NULL,
    currency TEXT NOT NULL,
    item_price REAL NOT NULL,
    item_promotion_discount REAL NOT NULL,
    ship_promotion_discount REAL NOT NULL,
    promotion_ids TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_orders_market_purchase ON orders(marketplace_id, purchase_date_utc);
CREATE INDEX IF NOT EXISTS ix_orders_sku ON orders(marketplace_id, seller_sku);
CREATE TABLE IF NOT EXISTS returns (
    row_key TEXT PRIMARY KEY,
    marketplace_id TEXT NOT NULL,
    return_date_utc TEXT NULL,
    order_id TEXT NOT NULL,
    seller_sku TEXT NOT NULL,
    asin TEXT NOT NULL,
    fnsku TEXT NOT NULL,
    product_name TEXT NOT NULL,
    quantity INTEGER NOT NULL,
    fulfillment_center_id TEXT NOT NULL,
    detailed_disposition TEXT NOT NULL,
    reason TEXT NOT NULL,
    status TEXT NOT NULL,
    customer_comments TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_returns_market_date ON returns(marketplace_id, return_date_utc);
CREATE INDEX IF NOT EXISTS ix_returns_sku ON returns(marketplace_id, seller_sku);
CREATE TABLE IF NOT EXISTS price_snapshots (
    marketplace_id TEXT NOT NULL,
    captured_at_utc TEXT NOT NULL,
    seller_sku TEXT NOT NULL,
    asin TEXT NOT NULL,
    status TEXT NOT NULL,
    listing_price REAL NOT NULL,
    shipping REAL NOT NULL,
    landed_price REAL NOT NULL,
    currency TEXT NOT NULL,
    buy_box_winner INTEGER NULL,
    PRIMARY KEY (marketplace_id, captured_at_utc, seller_sku)
);
CREATE INDEX IF NOT EXISTS ix_prices_sku_time ON price_snapshots(marketplace_id, seller_sku, captured_at_utc);
CREATE TABLE IF NOT EXISTS inventory_snapshots (
    marketplace_id TEXT NOT NULL,
    captured_at_utc TEXT NOT NULL,
    seller_sku TEXT NOT NULL,
    asin TEXT NOT NULL,
    fnsku TEXT NOT NULL,
    product_name TEXT NOT NULL,
    fulfillable INTEGER NOT NULL,
    reserved INTEGER NOT NULL,
    inbound INTEGER NOT NULL,
    total INTEGER NOT NULL,
    PRIMARY KEY (marketplace_id, captured_at_utc, seller_sku)
);
CREATE TABLE IF NOT EXISTS raw_documents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    marketplace_id TEXT NOT NULL,
    document_type TEXT NOT NULL,
    period_start_utc TEXT NULL,
    period_end_utc TEXT NULL,
    captured_at_utc TEXT NOT NULL,
    content TEXT NOT NULL,
    content_hash TEXT NOT NULL UNIQUE
);
CREATE INDEX IF NOT EXISTS ix_raw_documents_type ON raw_documents(marketplace_id, document_type, captured_at_utc);
CREATE TABLE IF NOT EXISTS ads_performance (
    row_key TEXT PRIMARY KEY,
    marketplace_id TEXT NOT NULL,
    period_start_utc TEXT NOT NULL,
    period_end_utc TEXT NOT NULL,
    campaign_id TEXT NOT NULL,
    campaign_name TEXT NOT NULL,
    ad_group_id TEXT NOT NULL,
    ad_group_name TEXT NOT NULL,
    keyword_or_target TEXT NOT NULL,
    search_term TEXT NOT NULL,
    placement TEXT NOT NULL,
    impressions INTEGER NOT NULL,
    clicks INTEGER NOT NULL,
    spend REAL NOT NULL,
    attributed_orders INTEGER NOT NULL,
    attributed_units INTEGER NOT NULL,
    attributed_sales REAL NOT NULL,
    currency TEXT NOT NULL,
    source TEXT NOT NULL,
    imported_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_ads_market_time ON ads_performance(marketplace_id, period_start_utc);
""";
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAnalysisPackAsync(string marketplaceId, AnalysisPackResult pack, IReadOnlyList<InventoryRow> inventory, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await AddCollectionAsync(marketplaceId, "SP-API analysis pack", $"{pack.HourlySales.Count} hourly / {pack.Orders.Count} order / {pack.Returns.Count} return rows", ct);
        await SaveHourlySalesAsync(marketplaceId, pack.HourlySales, ct);
        await SaveOrdersAsync(marketplaceId, pack.Orders, ct);
        await SaveReturnsAsync(marketplaceId, pack.Returns, ct);
        await SavePricesAsync(marketplaceId, pack.Prices, ct);
        await SaveInventoryAsync(marketplaceId, inventory, now, ct);
        if (!string.IsNullOrWhiteSpace(pack.SalesTrafficRawJson))
            await SaveRawDocumentAsync(marketplaceId, "sales-traffic", null, null, pack.SalesTrafficRawJson, ct);
        foreach (var finance in pack.FinancePages)
            await SaveRawDocumentAsync(marketplaceId, "finance-transactions", null, null, finance, ct);
    }

    public async Task AddCollectionAsync(string marketplaceId, string source, string? note, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO collections(marketplace_id,collected_at_utc,source,note) VALUES($m,$t,$s,$n);";
        command.Parameters.AddWithValue("$m", marketplaceId);
        command.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$s", source);
        command.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task SaveHourlySalesAsync(string marketplaceId, IReadOnlyList<HourlySalesPoint> rows, CancellationToken ct = default) => SaveSalesRowsAsync("hourly_sales", marketplaceId, rows, ct);
    public Task SaveDailySalesAsync(string marketplaceId, IReadOnlyList<HourlySalesPoint> rows, CancellationToken ct = default) => SaveSalesRowsAsync("daily_sales", marketplaceId, rows, ct);

    private async Task SaveSalesRowsAsync(string table, string marketplaceId, IReadOnlyList<HourlySalesPoint> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows.Where(x => x.IntervalStartUtc != DateTimeOffset.MinValue))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
INSERT INTO {table}(marketplace_id,interval_start_utc,interval_end_utc,orders,units,order_items,sales,average_unit_price,currency,updated_at_utc)
VALUES($m,$s,$e,$o,$u,$i,$sales,$avg,$c,$now)
ON CONFLICT(marketplace_id,interval_start_utc) DO UPDATE SET interval_end_utc=excluded.interval_end_utc,orders=excluded.orders,units=excluded.units,order_items=excluded.order_items,sales=excluded.sales,average_unit_price=excluded.average_unit_price,currency=excluded.currency,updated_at_utc=excluded.updated_at_utc;
""";
            command.Parameters.AddWithValue("$m", marketplaceId);
            command.Parameters.AddWithValue("$s", Iso(row.IntervalStartUtc));
            command.Parameters.AddWithValue("$e", Iso(row.IntervalEndUtc));
            command.Parameters.AddWithValue("$o", row.Orders);
            command.Parameters.AddWithValue("$u", row.Units);
            command.Parameters.AddWithValue("$i", row.OrderItems);
            command.Parameters.AddWithValue("$sales", row.Sales);
            command.Parameters.AddWithValue("$avg", row.AverageUnitPrice);
            command.Parameters.AddWithValue("$c", row.Currency ?? "");
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    public async Task SaveOrdersAsync(string marketplaceId, IReadOnlyList<OrderLineRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            var key = Hash(string.Join("|", marketplaceId, row.AmazonOrderId, row.SellerSku, row.Asin, row.PurchaseDate?.UtcDateTime.ToString("O"), row.ItemPrice, row.Quantity, row.PromotionIds));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO orders(row_key,marketplace_id,amazon_order_id,purchase_date_utc,order_status,fulfillment_channel,product_name,seller_sku,asin,quantity,currency,item_price,item_promotion_discount,ship_promotion_discount,promotion_ids,updated_at_utc)
VALUES($k,$m,$oid,$d,$status,$fc,$name,$sku,$asin,$q,$currency,$price,$ipd,$spd,$promo,$now)
ON CONFLICT(row_key) DO UPDATE SET order_status=excluded.order_status,fulfillment_channel=excluded.fulfillment_channel,product_name=excluded.product_name,quantity=excluded.quantity,item_price=excluded.item_price,item_promotion_discount=excluded.item_promotion_discount,ship_promotion_discount=excluded.ship_promotion_discount,promotion_ids=excluded.promotion_ids,updated_at_utc=excluded.updated_at_utc;
""";
            command.Parameters.AddWithValue("$k", key); command.Parameters.AddWithValue("$m", marketplaceId); command.Parameters.AddWithValue("$oid", row.AmazonOrderId);
            command.Parameters.AddWithValue("$d", row.PurchaseDate is null ? DBNull.Value : Iso(row.PurchaseDate.Value));
            command.Parameters.AddWithValue("$status", row.OrderStatus); command.Parameters.AddWithValue("$fc", row.FulfillmentChannel); command.Parameters.AddWithValue("$name", row.ProductName);
            command.Parameters.AddWithValue("$sku", row.SellerSku); command.Parameters.AddWithValue("$asin", row.Asin); command.Parameters.AddWithValue("$q", row.Quantity); command.Parameters.AddWithValue("$currency", row.Currency);
            command.Parameters.AddWithValue("$price", row.ItemPrice); command.Parameters.AddWithValue("$ipd", row.ItemPromotionDiscount); command.Parameters.AddWithValue("$spd", row.ShipPromotionDiscount); command.Parameters.AddWithValue("$promo", row.PromotionIds);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    public async Task SaveReturnsAsync(string marketplaceId, IReadOnlyList<ReturnRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            var key = Hash(string.Join("|", marketplaceId, row.ReturnDate?.UtcDateTime.ToString("O"), row.OrderId, row.SellerSku, row.Asin, row.Quantity, row.Reason, row.DetailedDisposition));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO returns(row_key,marketplace_id,return_date_utc,order_id,seller_sku,asin,fnsku,product_name,quantity,fulfillment_center_id,detailed_disposition,reason,status,customer_comments,updated_at_utc)
VALUES($k,$m,$d,$oid,$sku,$asin,$fn,$name,$q,$fc,$disp,$reason,$status,$comments,$now)
ON CONFLICT(row_key) DO UPDATE SET status=excluded.status,customer_comments=excluded.customer_comments,detailed_disposition=excluded.detailed_disposition,updated_at_utc=excluded.updated_at_utc;
""";
            command.Parameters.AddWithValue("$k", key); command.Parameters.AddWithValue("$m", marketplaceId); command.Parameters.AddWithValue("$d", row.ReturnDate is null ? DBNull.Value : Iso(row.ReturnDate.Value));
            command.Parameters.AddWithValue("$oid", row.OrderId); command.Parameters.AddWithValue("$sku", row.SellerSku); command.Parameters.AddWithValue("$asin", row.Asin); command.Parameters.AddWithValue("$fn", row.FnSku); command.Parameters.AddWithValue("$name", row.ProductName);
            command.Parameters.AddWithValue("$q", row.Quantity); command.Parameters.AddWithValue("$fc", row.FulfillmentCenterId); command.Parameters.AddWithValue("$disp", row.DetailedDisposition); command.Parameters.AddWithValue("$reason", row.Reason); command.Parameters.AddWithValue("$status", row.Status); command.Parameters.AddWithValue("$comments", row.CustomerComments);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    public async Task SavePricesAsync(string marketplaceId, IReadOnlyList<PriceSnapshotRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT OR REPLACE INTO price_snapshots(marketplace_id,captured_at_utc,seller_sku,asin,status,listing_price,shipping,landed_price,currency,buy_box_winner)
VALUES($m,$t,$sku,$asin,$status,$list,$ship,$landed,$currency,$bb);
""";
            command.Parameters.AddWithValue("$m", marketplaceId); command.Parameters.AddWithValue("$t", Iso(row.CapturedAtUtc)); command.Parameters.AddWithValue("$sku", row.SellerSku); command.Parameters.AddWithValue("$asin", row.Asin); command.Parameters.AddWithValue("$status", row.Status);
            command.Parameters.AddWithValue("$list", row.ListingPrice); command.Parameters.AddWithValue("$ship", row.Shipping); command.Parameters.AddWithValue("$landed", row.LandedPrice); command.Parameters.AddWithValue("$currency", row.Currency); command.Parameters.AddWithValue("$bb", row.BuyBoxWinner is null ? DBNull.Value : row.BuyBoxWinner.Value ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    public async Task SaveInventoryAsync(string marketplaceId, IReadOnlyList<InventoryRow> rows, DateTimeOffset capturedAtUtc, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
INSERT OR REPLACE INTO inventory_snapshots(marketplace_id,captured_at_utc,seller_sku,asin,fnsku,product_name,fulfillable,reserved,inbound,total)
VALUES($m,$t,$sku,$asin,$fn,$name,$f,$r,$i,$total);
""";
            command.Parameters.AddWithValue("$m", marketplaceId); command.Parameters.AddWithValue("$t", Iso(capturedAtUtc)); command.Parameters.AddWithValue("$sku", row.SellerSku); command.Parameters.AddWithValue("$asin", row.Asin); command.Parameters.AddWithValue("$fn", row.FnSku); command.Parameters.AddWithValue("$name", row.ProductName);
            command.Parameters.AddWithValue("$f", row.Fulfillable); command.Parameters.AddWithValue("$r", row.Reserved); command.Parameters.AddWithValue("$i", row.Inbound); command.Parameters.AddWithValue("$total", row.Total);
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    public async Task SaveRawDocumentAsync(string marketplaceId, string type, DateTimeOffset? start, DateTimeOffset? end, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT OR IGNORE INTO raw_documents(marketplace_id,document_type,period_start_utc,period_end_utc,captured_at_utc,content,content_hash)
VALUES($m,$type,$start,$end,$now,$content,$hash);
""";
        command.Parameters.AddWithValue("$m", marketplaceId); command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$start", start is null ? DBNull.Value : Iso(start.Value)); command.Parameters.AddWithValue("$end", end is null ? DBNull.Value : Iso(end.Value));
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow)); command.Parameters.AddWithValue("$content", content); command.Parameters.AddWithValue("$hash", Hash(type + "|" + content));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAdsAsync(IReadOnlyList<AdsPerformanceRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            var key = Hash(string.Join("|", row.MarketplaceId, Iso(row.PeriodStartUtc), row.CampaignId, row.CampaignName, row.AdGroupId, row.KeywordOrTarget, row.SearchTerm, row.Placement, row.Source));
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
INSERT OR REPLACE INTO ads_performance(row_key,marketplace_id,period_start_utc,period_end_utc,campaign_id,campaign_name,ad_group_id,ad_group_name,keyword_or_target,search_term,placement,impressions,clicks,spend,attributed_orders,attributed_units,attributed_sales,currency,source,imported_at_utc)
VALUES($k,$m,$s,$e,$cid,$cn,$agid,$agn,$kt,$st,$p,$imp,$clicks,$spend,$orders,$units,$sales,$currency,$source,$now);
""";
            command.Parameters.AddWithValue("$k", key); command.Parameters.AddWithValue("$m", row.MarketplaceId); command.Parameters.AddWithValue("$s", Iso(row.PeriodStartUtc)); command.Parameters.AddWithValue("$e", Iso(row.PeriodEndUtc)); command.Parameters.AddWithValue("$cid", row.CampaignId); command.Parameters.AddWithValue("$cn", row.CampaignName);
            command.Parameters.AddWithValue("$agid", row.AdGroupId); command.Parameters.AddWithValue("$agn", row.AdGroupName); command.Parameters.AddWithValue("$kt", row.KeywordOrTarget); command.Parameters.AddWithValue("$st", row.SearchTerm); command.Parameters.AddWithValue("$p", row.Placement);
            command.Parameters.AddWithValue("$imp", row.Impressions); command.Parameters.AddWithValue("$clicks", row.Clicks); command.Parameters.AddWithValue("$spend", row.Spend); command.Parameters.AddWithValue("$orders", row.AttributedOrders); command.Parameters.AddWithValue("$units", row.AttributedUnits); command.Parameters.AddWithValue("$sales", row.AttributedSales); command.Parameters.AddWithValue("$currency", row.Currency); command.Parameters.AddWithValue("$source", row.Source); command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<HistoricalHourRow>> QueryHoursAsync(HistoryFilter filter, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var raw = new List<HourlySalesPoint>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT interval_start_utc,interval_end_utc,orders,units,order_items,sales,average_unit_price,currency FROM hourly_sales WHERE marketplace_id=$m AND interval_start_utc >= $from AND interval_start_utc < $to ORDER BY interval_start_utc;";
        command.Parameters.AddWithValue("$m", filter.MarketplaceId);
        command.Parameters.AddWithValue("$from", filter.FromDate.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", filter.ToDate.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            raw.Add(new HourlySalesPoint(ParseDate(reader.GetString(0)), ParseDate(reader.GetString(1)), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), ToDecimal(reader.GetValue(5)), ToDecimal(reader.GetValue(6)), reader.GetString(7)));
        }

        return raw.Select(x => new { Point = x, Local = TimeZoneInfo.ConvertTime(x.IntervalStartUtc, marketplaceZone), Kuwait = TimeZoneHelper.ToKuwait(x.IntervalStartUtc) })
            .Where(x => x.Local.Date >= filter.FromDate.Date && x.Local.Date <= filter.ToDate.Date && TimeZoneHelper.HourMatches(x.Local.Hour, filter.FromHour, filter.ToHour))
            .Select(x => new HistoricalHourRow(x.Point.IntervalStartUtc, x.Local.ToString("yyyy-MM-dd HH:mm"), x.Kuwait.ToString("yyyy-MM-dd HH:mm"), x.Local.DayOfWeek.ToString(), x.Point.Orders, x.Point.Units, x.Point.Sales, x.Point.AverageUnitPrice, x.Point.Currency))
            .ToArray();
    }

    public async Task<IReadOnlyList<HourAggregateRow>> QueryHourSummaryAsync(HistoryFilter filter, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        var rows = await QueryHoursAsync(filter, marketplaceZone, ct);
        var parsed = rows.Select(r => new
        {
            Row = r,
            Market = DateTime.ParseExact(r.MarketplaceTime, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Kuwait = DateTime.ParseExact(r.KuwaitTime, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        }).ToArray();

        return parsed.GroupBy(x => x.Market.Hour).Select(g =>
        {
            var observedDays = Math.Max(1, g.Select(x => x.Market.Date).Distinct().Count());
            var orders = g.Sum(x => x.Row.Orders);
            var sales = g.Sum(x => x.Row.Sales);
            var kuwaitHours = g.Select(x => x.Kuwait.Hour).Distinct().OrderBy(x => x).Select(x => $"{x:00}:00").ToArray();
            return new HourAggregateRow(g.Key, $"{g.Key:00}:00–{(g.Key + 1) % 24:00}:00", string.Join(" / ", kuwaitHours), orders, g.Sum(x => x.Row.Units), sales, orders == 0 ? 0 : sales / orders, (decimal)orders / observedDays, observedDays);
        }).OrderByDescending(x => x.OrdersPerObservedDay).ThenByDescending(x => x.Sales).ToArray();
    }

    public async Task<IReadOnlyList<SkuPerformanceRow>> QuerySkuPerformanceAsync(HistoryFilter filter, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        var orders = await LoadOrdersAsync(filter.MarketplaceId, ct);
        var returns = await LoadReturnsAsync(filter.MarketplaceId, ct);
        var selected = orders.Where(x => InFilter(x.PurchaseDate, filter, marketplaceZone) && MatchSku(x.SellerSku, x.Asin, filter)).ToArray();
        var selectedReturns = returns.Where(x => InDateFilter(x.ReturnDate, filter, marketplaceZone) && MatchSku(x.SellerSku, x.Asin, filter)).ToArray();
        return selected.GroupBy(x => new { x.SellerSku, x.Asin, x.ProductName }).Select(g =>
        {
            var units = g.Sum(x => x.Quantity);
            var revenue = g.Sum(x => x.ItemPrice);
            var returned = selectedReturns.Where(r => string.Equals(r.SellerSku, g.Key.SellerSku, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(g.Key.Asin) && string.Equals(r.Asin, g.Key.Asin, StringComparison.OrdinalIgnoreCase))).Sum(x => x.Quantity);
            return new SkuPerformanceRow(g.Key.SellerSku, g.Key.Asin, g.Key.ProductName, g.Select(x => x.AmazonOrderId).Distinct().Count(), units, revenue, units == 0 ? 0 : revenue / units, g.Sum(x => x.ItemPromotionDiscount + x.ShipPromotionDiscount), returned, units == 0 ? 0 : 100m * returned / units);
        }).OrderByDescending(x => x.Revenue).ToArray();
    }

    public async Task<IReadOnlyList<ReturnReasonSummaryRow>> QueryReturnReasonsAsync(HistoryFilter filter, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        var rows = (await LoadReturnsAsync(filter.MarketplaceId, ct)).Where(x => InDateFilter(x.ReturnDate, filter, marketplaceZone) && MatchSku(x.SellerSku, x.Asin, filter)).ToArray();
        var total = Math.Max(1, rows.Sum(x => x.Quantity));
        return rows.GroupBy(x => new { x.SellerSku, x.Asin, x.Reason, x.DetailedDisposition }).Select(g => new ReturnReasonSummaryRow(g.Key.SellerSku, g.Key.Asin, g.Key.Reason, g.Key.DetailedDisposition, g.Sum(x => x.Quantity), g.Count(), 100m * g.Sum(x => x.Quantity) / total)).OrderByDescending(x => x.ReturnedUnits).ToArray();
    }

    public async Task<IReadOnlyList<PricePerformanceRow>> QueryPricePerformanceAsync(HistoryFilter filter, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        var orders = (await LoadOrdersAsync(filter.MarketplaceId, ct)).Where(x => InFilter(x.PurchaseDate, filter, marketplaceZone) && MatchSku(x.SellerSku, x.Asin, filter) && x.Quantity > 0).ToArray();
        var returns = (await LoadReturnsAsync(filter.MarketplaceId, ct)).Where(x => InDateFilter(x.ReturnDate, filter, marketplaceZone) && MatchSku(x.SellerSku, x.Asin, filter)).ToArray();
        return orders.GroupBy(x => new { x.SellerSku, x.Asin, UnitPrice = Math.Round(x.ItemPrice / Math.Max(1, x.Quantity), 2) }).Select(g =>
        {
            var units = g.Sum(x => x.Quantity);
            var returned = returns.Where(r => string.Equals(r.SellerSku, g.Key.SellerSku, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(g.Key.Asin) && string.Equals(r.Asin, g.Key.Asin, StringComparison.OrdinalIgnoreCase))).Sum(x => x.Quantity);
            return new PricePerformanceRow(g.Key.SellerSku, g.Key.Asin, g.Key.UnitPrice, g.Select(x => x.AmazonOrderId).Distinct().Count(), units, g.Sum(x => x.ItemPrice), returned, units == 0 ? 0 : 100m * returned / units);
        }).OrderBy(x => x.SellerSku).ThenBy(x => x.UnitPrice).ToArray();
    }

    public async Task<IReadOnlyList<SalesAttributionRow>> QuerySalesAttributionAsync(HistoryFilter filter, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        var hours = await QueryHoursAsync(filter, marketplaceZone, ct);
        var salesByDate = hours.GroupBy(x => DateTime.ParseExact(x.MarketplaceTime, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture).Date)
            .ToDictionary(g => g.Key, g => new { Orders = g.Sum(x => x.Orders), Sales = g.Sum(x => x.Sales) });
        var ads = await LoadAdsAsync(filter.MarketplaceId, ct);
        var adByDate = ads.Select(x => new { Row = x, Local = TimeZoneInfo.ConvertTime(x.PeriodStartUtc, marketplaceZone) })
            .Where(x => x.Local.Date >= filter.FromDate.Date && x.Local.Date <= filter.ToDate.Date && TimeZoneHelper.HourMatches(x.Local.Hour, filter.FromHour, filter.ToHour))
            .GroupBy(x => x.Local.Date).ToDictionary(g => g.Key, g => new { Orders = g.Sum(x => x.Row.AttributedOrders), Sales = g.Sum(x => x.Row.AttributedSales), Spend = g.Sum(x => x.Row.Spend) });
        var dates = salesByDate.Keys.Union(adByDate.Keys).OrderBy(x => x).ToArray();
        return dates.Select(date =>
        {
            salesByDate.TryGetValue(date, out var total); adByDate.TryGetValue(date, out var ad);
            var totalOrders = total?.Orders ?? 0; var totalSales = total?.Sales ?? 0; var adOrders = ad?.Orders ?? 0; var adSales = ad?.Sales ?? 0; var spend = ad?.Spend ?? 0;
            return new SalesAttributionRow(date, date.ToString("yyyy-MM-dd"), totalOrders, totalSales, adOrders, adSales, Math.Max(0, totalOrders - adOrders), Math.Max(0, totalSales - adSales), totalSales == 0 ? 0 : 100m * adSales / totalSales, spend, adSales == 0 ? 0 : 100m * spend / adSales, totalSales == 0 ? 0 : 100m * spend / totalSales);
        }).ToArray();
    }

    public async Task<DatabaseCoverage> GetCoverageAsync(string marketplaceId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        async Task<(string? Min, string? Max, int Count)> Span(string table, string dateColumn)
        {
            await using var c = connection.CreateCommand(); c.CommandText = $"SELECT MIN({dateColumn}),MAX({dateColumn}),COUNT(*) FROM {table} WHERE marketplace_id=$m;"; c.Parameters.AddWithValue("$m", marketplaceId);
            await using var r = await c.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
            return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2));
        }
        var h = await Span("hourly_sales", "interval_start_utc"); var o = await Span("orders", "purchase_date_utc"); var ret = await Span("returns", "return_date_utc"); var p = await Span("price_snapshots", "captured_at_utc"); var inv = await Span("inventory_snapshots", "captured_at_utc"); var ads = await Span("ads_performance", "period_start_utc");
        return new DatabaseCoverage(ToNullableDate(h.Min), ToNullableDate(h.Max), h.Count, ToNullableDate(o.Min), ToNullableDate(o.Max), o.Count, ret.Count, p.Count, inv.Count, ads.Count, DatabasePath);
    }

    private async Task<IReadOnlyList<OrderLineRow>> LoadOrdersAsync(string marketplaceId, CancellationToken ct)
    {
        var rows = new List<OrderLineRow>(); await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct); await using var c = connection.CreateCommand();
        c.CommandText = "SELECT amazon_order_id,purchase_date_utc,order_status,fulfillment_channel,product_name,seller_sku,asin,quantity,currency,item_price,item_promotion_discount,ship_promotion_discount,promotion_ids FROM orders WHERE marketplace_id=$m;"; c.Parameters.AddWithValue("$m", marketplaceId);
        await using var r = await c.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) rows.Add(new OrderLineRow(r.GetString(0), r.IsDBNull(1) ? null : ParseDate(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetString(8), ToDecimal(r.GetValue(9)), ToDecimal(r.GetValue(10)), ToDecimal(r.GetValue(11)), r.GetString(12)));
        return rows;
    }

    private async Task<IReadOnlyList<ReturnRow>> LoadReturnsAsync(string marketplaceId, CancellationToken ct)
    {
        var rows = new List<ReturnRow>(); await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct); await using var c = connection.CreateCommand();
        c.CommandText = "SELECT return_date_utc,order_id,seller_sku,asin,fnsku,product_name,quantity,fulfillment_center_id,detailed_disposition,reason,status,customer_comments FROM returns WHERE marketplace_id=$m;"; c.Parameters.AddWithValue("$m", marketplaceId);
        await using var r = await c.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) rows.Add(new ReturnRow(r.IsDBNull(0) ? null : ParseDate(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11)));
        return rows;
    }

    private async Task<IReadOnlyList<AdsPerformanceRow>> LoadAdsAsync(string marketplaceId, CancellationToken ct)
    {
        var rows = new List<AdsPerformanceRow>(); await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct); await using var c = connection.CreateCommand();
        c.CommandText = "SELECT period_start_utc,period_end_utc,campaign_id,campaign_name,ad_group_id,ad_group_name,keyword_or_target,search_term,placement,impressions,clicks,spend,attributed_orders,attributed_units,attributed_sales,currency,source FROM ads_performance WHERE marketplace_id=$m;"; c.Parameters.AddWithValue("$m", marketplaceId);
        await using var r = await c.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) rows.Add(new AdsPerformanceRow(ParseDate(r.GetString(0)), ParseDate(r.GetString(1)), marketplaceId, r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt64(9), r.GetInt64(10), ToDecimal(r.GetValue(11)), r.GetInt32(12), r.GetInt32(13), ToDecimal(r.GetValue(14)), r.GetString(15), r.GetString(16)));
        return rows;
    }

    private static bool InFilter(DateTimeOffset? value, HistoryFilter filter, TimeZoneInfo zone)
    {
        if (value is null) return false; var local = TimeZoneInfo.ConvertTime(value.Value, zone);
        return local.Date >= filter.FromDate.Date && local.Date <= filter.ToDate.Date && TimeZoneHelper.HourMatches(local.Hour, filter.FromHour, filter.ToHour);
    }
    private static bool InDateFilter(DateTimeOffset? value, HistoryFilter filter, TimeZoneInfo zone)
    {
        if (value is null) return false; var local = TimeZoneInfo.ConvertTime(value.Value, zone); return local.Date >= filter.FromDate.Date && local.Date <= filter.ToDate.Date;
    }
    private static bool MatchSku(string sku, string asin, HistoryFilter filter) => (string.IsNullOrWhiteSpace(filter.SellerSku) || sku.Contains(filter.SellerSku, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrWhiteSpace(filter.Asin) || asin.Contains(filter.Asin, StringComparison.OrdinalIgnoreCase));

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ToNullableDate(string? value) => string.IsNullOrWhiteSpace(value) ? null : ParseDate(value);
    private static decimal ToDecimal(object value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
