namespace Walka.Amazon.ConnectionTester.Models;

public sealed record HistoryFilter(
    string MarketplaceId,
    DateTime FromDate,
    DateTime ToDate,
    int FromHour,
    int ToHour,
    string? SellerSku = null,
    string? Asin = null);

public sealed record HistoricalHourRow(
    DateTimeOffset IntervalStartUtc,
    string MarketplaceTime,
    string KuwaitTime,
    string DayOfWeek,
    int Orders,
    int Units,
    decimal Sales,
    decimal AverageUnitPrice,
    string Currency);

public sealed record HourAggregateRow(
    int MarketplaceHour,
    string MarketplaceHourLabel,
    string KuwaitHourLabel,
    int Orders,
    int Units,
    decimal Sales,
    decimal AverageOrderValue,
    decimal OrdersPerObservedDay,
    int ObservedDays);

public sealed record SkuPerformanceRow(
    string SellerSku,
    string Asin,
    string ProductName,
    int Orders,
    int Units,
    decimal Revenue,
    decimal AverageRealizedUnitPrice,
    decimal PromotionDiscount,
    int ReturnedUnits,
    decimal ReturnRatePercent);

public sealed record ReturnReasonSummaryRow(
    string SellerSku,
    string Asin,
    string Reason,
    string Disposition,
    int ReturnedUnits,
    int ReturnLines,
    decimal ShareOfReturnsPercent);

public sealed record PricePerformanceRow(
    string SellerSku,
    string Asin,
    decimal UnitPrice,
    int Orders,
    int Units,
    decimal Revenue,
    int ReturnedUnits,
    decimal ReturnRatePercent);

public sealed record AdsPerformanceRow(
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    string MarketplaceId,
    string CampaignId,
    string CampaignName,
    string AdGroupId,
    string AdGroupName,
    string KeywordOrTarget,
    string SearchTerm,
    string Placement,
    long Impressions,
    long Clicks,
    decimal Spend,
    int AttributedOrders,
    int AttributedUnits,
    decimal AttributedSales,
    string Currency,
    string Source);

public sealed record SalesAttributionRow(
    DateTime Date,
    string MarketplaceDate,
    int TotalOrders,
    decimal TotalSales,
    int AdAttributedOrders,
    decimal AdAttributedSales,
    int EstimatedNonAdOrders,
    decimal EstimatedNonAdSales,
    decimal AdSalesSharePercent,
    decimal Spend,
    decimal AcosPercent,
    decimal TacosPercent);

public sealed record DatabaseCoverage(
    DateTimeOffset? FirstHourlyUtc,
    DateTimeOffset? LastHourlyUtc,
    int HourlyRows,
    DateTimeOffset? FirstOrderUtc,
    DateTimeOffset? LastOrderUtc,
    int OrderRows,
    int ReturnRows,
    int PriceSnapshots,
    int InventorySnapshots,
    int AdRows,
    string DatabasePath);

public sealed record BackfillResult(
    int DailySalesRows,
    int OrderRows,
    int ReturnRows,
    int FinanceDocuments,
    int TrafficDocuments,
    IReadOnlyList<string> Warnings);
