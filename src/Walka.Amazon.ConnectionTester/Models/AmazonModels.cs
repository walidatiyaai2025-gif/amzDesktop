namespace Walka.Amazon.ConnectionTester.Models;

public sealed record MarketplaceRow(string Id, string Name, string Country, string Currency, bool Participating, bool Suspended);
public sealed record InventoryRow(string SellerSku, string Asin, string FnSku, string ProductName, int Fulfillable, int Reserved, int Inbound, int Total);
public sealed record SalesSummary(int Orders, int Units, int OrderItems, decimal Sales, string Currency);

public sealed record HourlySalesPoint(
    DateTimeOffset IntervalStartUtc,
    DateTimeOffset IntervalEndUtc,
    int Orders,
    int Units,
    int OrderItems,
    decimal Sales,
    decimal AverageUnitPrice,
    string Currency);

public sealed record HourOfDaySummary(
    int Hour,
    string Label,
    int Orders,
    int Units,
    decimal Sales,
    decimal AverageOrderValue,
    decimal OrdersPerObservedDay);

public sealed record OrderLineRow(
    string AmazonOrderId,
    DateTimeOffset? PurchaseDate,
    string OrderStatus,
    string FulfillmentChannel,
    string ProductName,
    string SellerSku,
    string Asin,
    int Quantity,
    string Currency,
    decimal ItemPrice,
    decimal ItemPromotionDiscount,
    decimal ShipPromotionDiscount,
    string PromotionIds);

public sealed record ReturnRow(
    DateTimeOffset? ReturnDate,
    string OrderId,
    string SellerSku,
    string Asin,
    string FnSku,
    string ProductName,
    int Quantity,
    string FulfillmentCenterId,
    string DetailedDisposition,
    string Reason,
    string Status,
    string CustomerComments);

public sealed record PriceSnapshotRow(
    DateTimeOffset CapturedAtUtc,
    string SellerSku,
    string Asin,
    string Status,
    decimal ListingPrice,
    decimal Shipping,
    decimal LandedPrice,
    string Currency,
    bool? BuyBoxWinner);

public sealed record AnalysisPackResult(
    string OutputFolder,
    IReadOnlyList<HourlySalesPoint> HourlySales,
    IReadOnlyList<HourOfDaySummary> HourOfDay,
    IReadOnlyList<OrderLineRow> Orders,
    IReadOnlyList<ReturnRow> Returns,
    IReadOnlyList<PriceSnapshotRow> Prices,
    string SalesTrafficRawJson,
    IReadOnlyList<string> FinancePages);
