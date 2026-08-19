using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Walka.Amazon.ConnectionTester.Models;
using Walka.Amazon.ConnectionTester.Services;

namespace Walka.Amazon.ConnectionTester;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly AmazonSpApiClient _api;
    private IReadOnlyList<InventoryRow> _lastInventory = Array.Empty<InventoryRow>();
    private string _lastInventoryMarketplace = "";

    public MainWindow()
    {
        InitializeComponent();
        _api = new AmazonSpApiClient(_httpClient);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInputs(out var clientId, out var clientSecret, out var refreshToken, out var marketplaceId, out _)) return;
        SetBusy(true);
        ConnectionValue.Text = "Connecting…";
        OrdersValue.Text = SalesValue.Text = InventoryValue.Text = "—";
        LogBox.Clear();

        try
        {
            Log("Requesting LWA access token…");
            var token = await _api.GetAccessTokenAsync(clientId, clientSecret, refreshToken);
            ConnectionValue.Text = "LWA OK";

            Log("Loading seller marketplaces…");
            var marketplaces = await _api.GetMarketplacesAsync(token);
            MarketplacesGrid.ItemsSource = marketplaces;
            ConnectionValue.Text = $"SP-API OK · {marketplaces.Count} markets";

            var partialFailures = new List<string>();
            try
            {
                Log("Loading last 7 days Sales API metrics…");
                var sales = await _api.GetLast7DaysSalesAsync(marketplaceId, token);
                OrdersValue.Text = $"{sales.Orders:N0} / {sales.Units:N0} units";
                SalesValue.Text = string.IsNullOrWhiteSpace(sales.Currency) ? sales.Sales.ToString("N2") : $"{sales.Sales:N2} {sales.Currency}";
                Log($"Sales loaded: {sales.Orders:N0} orders, {sales.Units:N0} units, {sales.Sales:N2} {sales.Currency}.");
            }
            catch (Exception ex) { partialFailures.Add("Sales"); OrdersValue.Text = SalesValue.Text = "Error"; Log("SALES ERROR: " + ex.Message); }

            try
            {
                Log("Loading FBA inventory summaries…");
                _lastInventory = await _api.GetInventoryAsync(marketplaceId, token);
                _lastInventoryMarketplace = marketplaceId;
                InventoryGrid.ItemsSource = _lastInventory;
                InventoryValue.Text = $"{_lastInventory.Sum(x => x.Fulfillable):N0} units";
                Log($"Inventory loaded: {_lastInventory.Count:N0} SKUs.");
            }
            catch (Exception ex) { partialFailures.Add("FBA Inventory"); InventoryValue.Text = "Error"; Log("INVENTORY ERROR: " + ex.Message); }

            if (partialFailures.Count == 0) Log("DONE — production SP-API connection is working.");
            else { ConnectionValue.Text = "SP-API OK · partial"; Log("Connection works. Failed panels: " + string.Join(", ", partialFailures)); }
        }
        catch (Exception ex)
        {
            ConnectionValue.Text = "Failed";
            Log("CONNECTION ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Amazon connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private async void CollectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInputs(out var clientId, out var clientSecret, out var refreshToken, out var marketplaceId, out var timeZone)) return;
        SetBusy(true);
        LogBox.Clear();
        ReturnsValue.Text = BestHourValue.Text = "Collecting…";

        try
        {
            Log("Authenticating for analysis collection…");
            var token = await _api.GetAccessTokenAsync(clientId, clientSecret, refreshToken);
            ConnectionValue.Text = "SP-API collecting…";

            if (_lastInventory.Count == 0 || !string.Equals(_lastInventoryMarketplace, marketplaceId, StringComparison.Ordinal))
            {
                Log("Refreshing FBA inventory before collection…");
                _lastInventory = await _api.GetInventoryAsync(marketplaceId, token);
                _lastInventoryMarketplace = marketplaceId;
                InventoryGrid.ItemsSource = _lastInventory;
                InventoryValue.Text = $"{_lastInventory.Sum(x => x.Fulfillable):N0} units";
            }

            var collector = new AnalysisDataCollector(_api);
            var result = await collector.CollectAsync(marketplaceId, token, _lastInventory, timeZone, message => Log(message));

            HoursGrid.ItemsSource = result.HourOfDay;
            OrdersGrid.ItemsSource = result.Orders;
            ReturnsGrid.ItemsSource = result.Returns;
            PricesGrid.ItemsSource = result.Prices;

            var returnedUnits = result.Returns.Sum(x => x.Quantity);
            ReturnsValue.Text = $"{returnedUnits:N0} units";
            var best = result.HourOfDay.FirstOrDefault();
            BestHourValue.Text = best is null ? "No data" : $"{best.Label} · {best.OrdersPerObservedDay:N1}/day";
            DataFolderText.Text = "Saved analysis pack: " + result.OutputFolder;
            ConnectionValue.Text = "SP-API OK · pack saved";

            Log($"ANALYSIS PACK COMPLETE — {result.HourlySales.Count:N0} hourly points, {result.Orders.Count:N0} order lines, {result.Returns.Count:N0} return lines, {result.Prices.Count:N0} price snapshots, {result.FinancePages.Count:N0} finance pages.");
            Log("Data folder: " + result.OutputFolder);
        }
        catch (Exception ex)
        {
            ConnectionValue.Text = "SP-API OK · collection error";
            Log("COLLECTION ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Analysis collection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private bool TryGetInputs(out string clientId, out string clientSecret, out string refreshToken, out string marketplaceId, out TimeZoneInfo timeZone)
    {
        clientId = ClientIdBox.Text.Trim();
        clientSecret = ClientSecretBox.Password.Trim();
        refreshToken = RefreshTokenBox.Password.Trim();
        marketplaceId = (MarketplaceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ATVPDKIKX0DER";
        var zoneId = (TimeZoneBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Pacific Standard Time";
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch { timeZone = TimeZoneInfo.Utc; }

        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret) && !string.IsNullOrWhiteSpace(refreshToken)) return true;
        MessageBox.Show("Enter Client ID, Client Secret, and Refresh Token first.", "Missing credentials", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        CollectButton.IsEnabled = !busy;
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        _httpClient.Dispose();
        base.OnClosed(e);
    }
}
