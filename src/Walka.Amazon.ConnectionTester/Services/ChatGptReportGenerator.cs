using System.Text;
using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class ChatGptReportGenerator(HistoricalDatabase database)
{
    public async Task<string> GenerateAsync(HistoryFilter filter, TimeZoneInfo marketplaceZone, CancellationToken ct = default)
    {
        var coverage = await database.GetCoverageAsync(filter.MarketplaceId, ct);
        var hours = await database.QueryHourSummaryAsync(filter, marketplaceZone, ct);
        var sku = await database.QuerySkuPerformanceAsync(filter, marketplaceZone, ct);
        var returns = await database.QueryReturnReasonsAsync(filter, marketplaceZone, ct);
        var prices = await database.QueryPricePerformanceAsync(filter, marketplaceZone, ct);
        var attribution = await new AdsSalesBridgeService(database.DatabasePath).QueryAsync(filter, marketplaceZone, ct);
        var traffic = await new SalesTrafficStore(database.DatabasePath).QueryAsync(filter.MarketplaceId, filter.FromDate, filter.ToDate, ct);

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WALKA.Analyzer", "Reports");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"WALKA-ChatGPT-Analysis-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        var sb = new StringBuilder();

        sb.AppendLine("# WALKA Amazon Analysis Pack for ChatGPT");
        sb.AppendLine();
        sb.AppendLine("> Act as an Amazon growth, pricing, advertising, inventory and customer-return analyst. Prioritize profitable incremental growth rather than revenue alone. Separate evidence from hypotheses, quantify confidence, identify confounders, and propose specific tests with guardrails. Always show recommended operating times in both marketplace time and Kuwait time (UTC+03:00).");
        sb.AppendLine();
        sb.AppendLine("## Scope and data coverage");
        sb.AppendLine($"- Marketplace: `{filter.MarketplaceId}`");
        sb.AppendLine($"- Marketplace timezone: `{marketplaceZone.DisplayName}`");
        sb.AppendLine("- Kuwait timezone: `UTC+03:00`");
        sb.AppendLine($"- Filter: `{filter.FromDate:yyyy-MM-dd}` → `{filter.ToDate:yyyy-MM-dd}`, marketplace hours `{filter.FromHour:00}:00` → `{filter.ToHour:00}:59`");
        if (!string.IsNullOrWhiteSpace(filter.SellerSku)) sb.AppendLine($"- SKU filter: `{Esc(filter.SellerSku)}`");
        if (!string.IsNullOrWhiteSpace(filter.Asin)) sb.AppendLine($"- ASIN filter: `{Esc(filter.Asin)}`");
        sb.AppendLine($"- Hourly Sales rows: {coverage.HourlyRows:N0} ({Fmt(coverage.FirstHourlyUtc)} → {Fmt(coverage.LastHourlyUtc)} UTC)");
        sb.AppendLine($"- Order rows: {coverage.OrderRows:N0} ({Fmt(coverage.FirstOrderUtc)} → {Fmt(coverage.LastOrderUtc)} UTC)");
        sb.AppendLine($"- Return rows: {coverage.ReturnRows:N0}; price snapshots: {coverage.PriceSnapshots:N0}; inventory snapshots: {coverage.InventorySnapshots:N0}; Ads rows: {coverage.AdRows:N0}");
        sb.AppendLine();

        sb.AppendLine("## Best sales hours — store-level Sales API");
        sb.AppendLine("| Marketplace hour | Kuwait hour(s) actually observed | Orders | Units | Sales | Orders / observed day | Avg order value |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var row in hours.Take(12))
            sb.AppendLine($"| {row.MarketplaceHourLabel} | {row.KuwaitHourLabel} | {row.Orders} | {row.Units} | {row.Sales:N2} | {row.OrdersPerObservedDay:N2} | {row.AverageOrderValue:N2} |");
        if (hours.Count == 0) sb.AppendLine("| No hourly rows for the selected period | | | | | | |");
        sb.AppendLine();

        sb.AppendLine("## Sessions, conversion, Buy Box and refund signals");
        sb.AppendLine("| Date | Sales | Units | Sessions | Page views | Unit session % | Buy Box % | Units refunded | Refund rate % | Avg selling price |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in traffic.TakeLast(60))
            sb.AppendLine($"| {row.Date:yyyy-MM-dd} | {row.Sales:N2} | {row.Units} | {row.Sessions} | {row.PageViews} | {row.ConversionPercent:N2}% | {row.BuyBoxPercent:N2}% | {row.UnitsRefunded} | {row.RefundRatePercent:N2}% | {row.AverageSellingPrice:N2} |");
        if (traffic.Count == 0) sb.AppendLine("| No Sales & Traffic rows available | | | | | | | | | |");
        sb.AppendLine();

        sb.AppendLine("## SKU performance");
        sb.AppendLine("| SKU | ASIN | Orders | Units | Revenue | Avg realized unit price | Promotion discounts | Returned units | Diagnostic return rate* |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in sku.Take(40))
            sb.AppendLine($"| {Esc(row.SellerSku)} | {Esc(row.Asin)} | {row.Orders} | {row.Units} | {row.Revenue:N2} | {row.AverageRealizedUnitPrice:N2} | {row.PromotionDiscount:N2} | {row.ReturnedUnits} | {row.ReturnRatePercent:N2}% |");
        sb.AppendLine();
        sb.AppendLine("*Diagnostic return rate compares returns occurring in the selected window with units ordered in the selected window; it is not yet a purchase-cohort return rate because returns lag purchases.");
        sb.AppendLine();

        sb.AppendLine("## Return reasons and likely corrective actions");
        sb.AppendLine("| SKU | ASIN | Reason | Disposition | Units | Share | Investigation / fix |");
        sb.AppendLine("|---|---|---|---|---:|---:|---|");
        foreach (var row in returns.Take(50))
            sb.AppendLine($"| {Esc(row.SellerSku)} | {Esc(row.Asin)} | {Esc(row.Reason)} | {Esc(row.Disposition)} | {row.ReturnedUnits} | {row.ShareOfReturnsPercent:N1}% | {Esc(SuggestReturnAction(row.Reason, row.Disposition))} |");
        if (returns.Count == 0) sb.AppendLine("| No returns in the selected period | | | | | | |");
        sb.AppendLine();

        sb.AppendLine("## Price-response evidence from actual order lines");
        sb.AppendLine("| SKU | ASIN | Realized unit price | Orders | Units | Revenue | Returned units | Diagnostic return rate |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in prices.Take(80))
            sb.AppendLine($"| {Esc(row.SellerSku)} | {Esc(row.Asin)} | {row.UnitPrice:N2} | {row.Orders} | {row.Units} | {row.Revenue:N2} | {row.ReturnedUnits} | {row.ReturnRatePercent:N2}% |");
        sb.AppendLine();

        sb.AppendLine("## Amazon Ads vs total order history");
        sb.AppendLine("Amazon Ads conversions are attribution-window metrics, so they do not necessarily occur on the same clock hour as the click or order. `Estimated non-ad` below is a directional bridge, not deterministic order-level attribution.");
        sb.AppendLine();
        sb.AppendLine("| Date | Total orders | Total item sales | Ad-attributed orders | Ad sales | Est. non-ad orders | Est. non-ad sales | Ad sales share | Spend | ACOS | TACOS |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in attribution.TakeLast(90))
            sb.AppendLine($"| {row.MarketplaceDate} | {row.TotalOrders} | {row.TotalSales:N2} | {row.AdAttributedOrders} | {row.AdAttributedSales:N2} | {row.EstimatedNonAdOrders} | {row.EstimatedNonAdSales:N2} | {row.AdSalesSharePercent:N1}% | {row.Spend:N2} | {row.AcosPercent:N1}% | {row.TacosPercent:N1}% |");
        if (coverage.AdRows == 0) sb.AppendLine("| Ads data not connected/imported yet. Import an Amazon Ads CSV now or connect Ads API later; the same SQLite schema is ready. | | | | | | | | | | |");
        sb.AppendLine();

        sb.AppendLine("## Analysis tasks");
        sb.AppendLine("1. Rank the strongest and weakest marketplace hours for sales velocity. Convert recommendations to Kuwait time and account for DST differences shown in the data.");
        sb.AppendLine("2. Identify weekday/hour combinations worth dayparting once hourly Ads data is available. Do not infer ad causality from store sales alone.");
        sb.AppendLine("3. Identify price points that appear to improve units, revenue, conversion and return quality. Propose controlled price tests rather than declaring correlation as causation.");
        sb.AppendLine("4. Identify SKU/ASINs with the largest return burden and classify root causes into product/QC, packaging/fulfillment, listing expectation, sizing/fit, and low-actionability customer choice.");
        sb.AppendLine("5. Use sessions, unit-session %, Buy Box %, average selling price and refunds to explain sales changes before recommending ad spend changes.");
        sb.AppendLine("6. When Ads rows exist, analyze spend, ACOS, TACOS, ad-sales share and estimated non-ad sales. Flag campaigns/time periods that may be harvesting organic demand rather than creating incremental demand.");
        sb.AppendLine("7. Propose a 14-day experiment plan for price, bid, budget and placement/dayparting with one major variable changed at a time and explicit stop-loss thresholds.");
        sb.AppendLine("8. Flag stock-outs, inbound constraints, promotions, reporting latency, attribution-window effects and return lag as possible confounders.");
        sb.AppendLine();

        sb.AppendLine("## Important limitations");
        sb.AppendLine("- Hourly Sales API history is limited by Amazon to recent data; the app preserves every future collection locally so the offline history grows over time.");
        sb.AppendLine("- Store-level hourly Sales API rows are marketplace-wide. If a SKU/ASIN filter is supplied, product/return/price/order-based sections use that filter, while store-level hourly rows remain marketplace totals.");
        sb.AppendLine("- Raw customer comments and all API credentials are intentionally excluded from this shareable report.");

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
        return path;
    }

    private static string Fmt(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd HH:mm") ?? "n/a";
    private static string Esc(string? value) => (value ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");

    private static string SuggestReturnAction(string reason, string disposition)
    {
        var text = (reason + " " + disposition).ToLowerInvariant();
        if (text.Contains("damage") || text.Contains("defect") || text.Contains("broken")) return "Audit QC and packaging; compare by FNSKU/fulfillment center and receiving batch; add pre-shipment checks.";
        if (text.Contains("not as described") || text.Contains("description") || text.Contains("different")) return "Audit title, bullets, dimensions, images and claims against the physical product; remove expectation gaps.";
        if (text.Contains("small") || text.Contains("large") || text.Contains("size")) return "Make dimensions and scale visually explicit in listing images/copy and test expectation-setting creative.";
        if (text.Contains("missing") || text.Contains("incomplete")) return "Add component packing checklist and final weight/QC validation; review bundle assembly.";
        if (text.Contains("leak") || text.Contains("spill") || text.Contains("seal")) return "Test seal/closure consistency and gasket/locking tolerances; align listing containment claims with real use.";
        if (text.Contains("quality") || text.Contains("poor")) return "Inspect manufacturing lot and finish/material consistency; correlate returns with supplier QC records.";
        if (text.Contains("no longer") || text.Contains("changed mind") || text.Contains("accidental")) return "Low-actionability customer-choice return; monitor trend without over-correcting the product.";
        return "Review reason trend by SKU, price, promotion, fulfillment center and time period before changing product or listing.";
    }
}
