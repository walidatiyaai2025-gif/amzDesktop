namespace Walka.Amazon.ConnectionTester.Models;

public sealed record MarketplaceRow(string Id, string Name, string Country, string Currency, bool Participating, bool Suspended);
public sealed record InventoryRow(string SellerSku, string Asin, string FnSku, string ProductName, int Fulfillable, int Reserved, int Inbound, int Total);
public sealed record SalesSummary(int Orders, int Units, int OrderItems, decimal Sales, string Currency);
