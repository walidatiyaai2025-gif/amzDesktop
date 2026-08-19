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
        var attribution = await database.QuerySalesAttributionAsync(filter, marketplaceZone, ct);

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WALKA.Analyzer", "Reports");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"WALKA-ChatGPT-Analysis-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        var sb = new StringBuilder();

        sb.AppendLine("# WALKA Amazon Analysis Pack for ChatGPT");
        sb.AppendLine();
        sb.AppendLine("> Analyze this report as an Amazon growth, pricing, advertising, inventory, and customer-return analyst. Prioritize profitable sales growth, not revenue alone. Distinguish evidence from hypotheses. Give specific next actions, tests, expected impact, and risks. Do not assume ad-attributed sales are identical to purchase-time sales because Amazon Ads attribution windows can shift conversions after the click.");
        sb.AppendLine();
        sb.AppendLine("## Analysis scope");
        sb.AppendLine($"- Marketplace: `{filter.MarketplaceId}`");
        sb.AppendLine($"- Marketplace timezone used for analysis: `{marketplaceZone.DisplayName}`");
        sb.AppendLine("- Kuwait timezone: `UTC+03:00` (shown alongside marketplace timing)");
        sb.AppendLine($"- Filter: `{filter.FromDate:yyyy-MM-dd}` through `{filter.ToDate:yyyy-MM-dd}`, hours `{filter.FromHour:00}:00` through `{filter.ToHour:00}:59` marketplace time");
        if (!string.IsNullOrWhiteSpace(filter.SellerSku)) sb.AppendLine($"- SKU filter: `{filter.SellerSku}`");
        if (!string.IsNullOrWhiteSpace(filter.Asin)) sb.AppendLine($"- ASIN filter: `{filter.Asin}`");
        sb.AppendLine($"- Generated: `{DateTimeOffset.Now:O}`");
        sb.AppendLine();

        sb.AppendLine("## Local database coverage");
        sb.AppendLine($"- Hourly sales: {coverage.HourlyRows:N0} rows, {Fmt(coverage.FirstHourlyUtc)} → {Fmt(coverage.LastHourlyUtc)} UTC");
        sb.AppendLine($"- Order rows: {coverage.OrderRows:N0}, {Fmt(coverage.FirstOrderUtc)} → {Fmt(coverage.LastOrderUtc)} UTC");
        sb.AppendLine($"- Return rows: {coverage.ReturnRows:N0}");
        sb.AppendLine($"- Price snapshots: {coverage.PriceSnapshots:N0}");
        sb.AppendLine($"- Inventory snapshots: {coverage.InventorySnapshots:N0}");
        sb.AppendLine($"- Advertising rows: {coverage.AdRows:N0}");
        sb.AppendLine();

        sb.AppendLine("## Best sales hours");
        sb.AppendLine("| Marketplace time | Kuwait equivalent observed | Orders | Units | Sales | Orders/observed day | Avg order value |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in hours.Take(12))
            sb.AppendLine($"| {row.MarketplaceHourLabel} | {row.KuwaitHourLabel} | {row.Orders} | {row.Units} | {row.Sales:N2} | {row.OrdersPerObservedDay:N2} | {row.AverageOrderValue:N2} |");
        if (hours.Count == 0) sb.AppendLine("| No hourly data in the selected range | | | | | | |");
        sb.AppendLine();

        sb.AppendLine("## Product / SKU performance");
        sb.AppendLine("| SKU | ASIN | Orders | Units | Revenue | Avg realized unit price | Promotions | Returned units | Return rate* |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in sku.Take(30))
            sb.AppendLine($"| {Esc(row.SellerSku)} | {Esc(row.Asin)} | {row.Orders} | {row.Units} | {row.Revenue:N2} | {row.AverageRealizedUnitPrice:N2} | {row.PromotionDiscount:N2} | {row.ReturnedUnits} | {row.ReturnRatePercent:N2}% |");
        sb.AppendLine("\n*Return rate is a diagnostic ratio based on return rows whose return date falls inside the selected period divided by units ordered inside the selected period. It is useful for screening but is not cohort-adjusted yet.\n");

        sb.AppendLine("## Return reasons and dispositions");
        sb.AppendLine("| SKU | ASIN | Reason | Disposition | Units | Share of selected returns | Suggested investigation |");
        sb.AppendLine("|---|---|---|---|---:|---:|---|");
        foreach (var row in returns.Take(40))
            sb.AppendLine($"| {Esc(row.SellerSku)} | {Esc(row.Asin)} | {Esc(row.Reason)} | {Esc(row.Disposition)} | {row.ReturnedUnits} | {row.ShareOfReturnsPercent:N1}% | {Esc(SuggestReturnAction(row.Reason, row.Disposition))} |");
        if (returns.Count == 0) sb.AppendLine("| No return rows in the selected range | | | | | | |");
        sb.AppendLine();

        sb.AppendLine("## Price-response evidence from actual order lines");
        sb.AppendLine("| SKU | ASIN | Realized unit price | Orders | Units | Revenue | Returned units | Diagnostic return rate |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in prices.Take(50))
            sb.AppendLine($"| {Esc(row.SellerSku)} | {Esc(row.Asin)} | {row.UnitPrice:N2} | {row.Orders} | {row.Units} | {row.Revenue:N2} | {row.ReturnedUnits} | {row.ReturnRatePercent:N2}% |");
        sb.AppendLine();

        sb.AppendLine("## Advertising vs total sales bridge");
        sb.AppendLine("Amazon Ads sales are attribution-based. The non-ad values below are estimates (`total Amazon sales - ad-attributed sales`) and should be treated as a directional bridge, not order-level causal attribution.");
        sb.AppendLine();
        sb.AppendLine("| Date | Total orders | Total sales | Ad-attributed orders | Ad sales | Est. non-ad orders | Est. non-ad sales | Ad share | Spend | ACOS | TACOS |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in attribution.TakeLast(60))
            sb.AppendLine($"| {row.MarketplaceDate} | {row.TotalOrders} | {row.TotalSales:N2} | {row.AdAttributedOrders} | {row.AdAttributedSales:N2} | {row.EstimatedNonAdOrders} | {row.EstimatedNonAdSales:N2} | {row.AdSalesSharePercent:N1}% | {row.Spend:N2} | {row.AcosPercent:N1}% | {row.TacosPercent:N1}% |");
        if (coverage.AdRows == 0) sb.AppendLine("| Advertising data not imported/connected yet. Import an Amazon Ads CSV or connect Ads API to populate this section. | | | | | | | | | | |");
        sb.AppendLine();

        sb.AppendLine("## Questions ChatGPT should answer");
        sb.AppendLine("1. Which marketplace hours are strongest for orders, units, revenue and revenue per order? Convert all recommendations to Kuwait time too.");
        sb.AppendLine("2. Which hours should receive higher/lower ad bids once hourly Ads data is present? Avoid confusing organic demand with ad causality.");
        sb.AppendLine("3. Which SKU/ASIN has the strongest and weakest sales velocity, realized price, promotion dependency and return profile?");
        sb.AppendLine("4. Is there evidence of price elasticity? Identify price points worth A/B testing and define guardrails around profit and conversion.");
        sb.AppendLine("5. What are the dominant return reasons? Separate product/QC problems, listing-expectation problems, fulfillment damage, and low-actionability reasons. Give concrete fixes.");
        sb.AppendLine("6. When Ads data exists, compare ACOS, TACOS, ad share of sales and estimated non-ad sales. Identify campaigns/time windows that are likely incremental versus merely harvesting existing demand.");
        sb.AppendLine("7. Propose a 14-day test plan with price, bid, placement, budget and daypart experiments. Change one major variable at a time where possible.");
        sb.AppendLine("8. Flag data limitations, attribution-window effects, stock-outs, promotions, and return-lag effects that could bias conclusions.");
        sb.AppendLine();
        sb.AppendLine("## Data handling note");
        sb.AppendLine("This generated report intentionally excludes API secrets and raw customer comments. It is designed to be shareable with an analysis assistant without exposing credentials.");

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
        return path;
    }

    private static string Fmt(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd HH:mm") ?? "n/a";
    private static string Esc(string value) => (value ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");

    private static string SuggestReturnAction(string reason, string disposition)
    {
        var text = (reason + " " + disposition).ToLowerInvariant();
        if (text.Contains("damage") || text.Contains("defect") || text.Contains("broken")) return "Audit QC and packaging; inspect defect concentration by lot/FNSKU/fulfillment center and add pre-shipment checks.";
        if (text.Contains("not as described") || text.Contains("description") || text.Contains("different")) return "Audit title, bullets, dimensions, images and claims against the physical product; remove expectation gaps.";
        if (text.Contains("small") || text.Contains("large") || text.Contains("size")) return "Make dimensions and scale visually explicit in listing images and copy; test expectation-setting creative.";
        if (text.Contains("missing") || text.Contains("incomplete")) return "Add packing-component checklist and final weight/QC validation; review bundle assembly process.";
        if (text.Contains("leak") || text.Contains("spill") || text.Contains("seal")) return "Test seal/closure consistency, inspect gasket/locking tolerances, and ensure listing claims match actual containment capability.";
        if (text.Contains("quality") || text.Contains("poor")) return "Inspect manufacturing lot and material/finish consistency; correlate returns with receiving batch and supplier QC records.";
        if (text.Contains("no longer") || text.Contains("changed mind") || text.Contains("accidental")) return "Low-actionability customer-choice return; monitor trend but do not over-correct the product.";
        return "Review customer feedback and disposition trend for this reason; compare by SKU, price, promotion, fulfillment center and time period before changing product/listing.";
    }
}
