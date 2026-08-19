using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Walka.Amazon.ConnectionTester.Models;
using Walka.Amazon.ConnectionTester.Services;

namespace Walka.Amazon.ConnectionTester;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly AmazonSpApiClient _api;
    private readonly HistoricalDatabase _database;
    private readonly SalesTrafficStore _trafficStore;
    private IReadOnlyList<InventoryRow> _lastInventory = Array.Empty<InventoryRow>();
    private string _lastInventoryMarketplace = "";
    private CancellationTokenSource? _operationCts;

    public MainWindow()
    {
        InitializeComponent();
        _api = new AmazonSpApiClient(_httpClient);
        _database = new HistoricalDatabase();
        _trafficStore = new SalesTrafficStore(_database.DatabasePath);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var hours = Enumerable.Range(0, 24).Select(h => new HourChoice(h, $"{h:00}:00")).ToArray();
        FromHourBox.ItemsSource = hours;
        ToHourBox.ItemsSource = hours;
        FromHourBox.SelectedValue = 0;
        ToHourBox.SelectedValue = 23;
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Today;

        try
        {
            await _database.InitializeAsync();
            await _trafficStore.InitializeAsync();
            DataFolderText.Text = "Database: " + _database.DatabasePath;
            await LoadOfflineViewsAsync();
        }
        catch (Exception ex)
        {
            Log("DATABASE INIT ERROR: " + ex.Message);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCredentials(out var clientId, out var clientSecret, out var refreshToken)) return;
        var marketplaceId = GetMarketplaceId();
        var ct = BeginOperation();
        ConnectionValue.Text = "Connecting…";
        OrdersValue.Text = SalesValue.Text = InventoryValue.Text = "—";
        LogBox.Clear();

        try
        {
            Log("Requesting LWA access token…");
            var token = await _api.GetAccessTokenAsync(clientId, clientSecret, refreshToken, ct);
            ConnectionValue.Text = "LWA OK";

            Log("Loading seller marketplaces…");
            var marketplaces = await _api.GetMarketplacesAsync(token, ct);
            MarketplacesGrid.ItemsSource = marketplaces;
            ConnectionValue.Text = $"SP-API OK · {marketplaces.Count} markets";

            try
            {
                Log("Loading last 7 days Sales API metrics…");
                var sales = await _api.GetLast7DaysSalesAsync(marketplaceId, token, ct);
                OrdersValue.Text = $"{sales.Orders:N0} / {sales.Units:N0} units";
                SalesValue.Text = string.IsNullOrWhiteSpace(sales.Currency) ? sales.Sales.ToString("N2") : $"{sales.Sales:N2} {sales.Currency}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OrdersValue.Text = SalesValue.Text = "Error";
                Log("SALES ERROR: " + ex.Message);
            }

            try
            {
                Log("Loading FBA inventory summaries…");
                _lastInventory = await _api.GetInventoryAsync(marketplaceId, token, ct);
                _lastInventoryMarketplace = marketplaceId;
                InventoryGrid.ItemsSource = _lastInventory;
                InventoryValue.Text = $"{_lastInventory.Sum(x => x.Fulfillable):N0} units";
                await _database.SaveInventoryAsync(marketplaceId, _lastInventory, DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                InventoryValue.Text = "Error";
                Log("INVENTORY ERROR: " + ex.Message);
            }

            Log("Connection test finished. Historical database remains available offline.");
            await LoadOfflineViewsAsync(ct);
        }
        catch (OperationCanceledException) { Log("Operation cancelled."); }
        catch (Exception ex)
        {
            ConnectionValue.Text = "Failed";
            Log("CONNECTION ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Amazon connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private async void CollectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCredentials(out var clientId, out var clientSecret, out var refreshToken)) return;
        var marketplaceId = GetMarketplaceId();
        var timeZone = GetSelectedTimeZone();
        var ct = BeginOperation();
        LogBox.Clear();
        ConnectionValue.Text = "Collecting…";

        try
        {
            var token = await _api.GetAccessTokenAsync(clientId, clientSecret, refreshToken, ct);
            if (_lastInventory.Count == 0 || !string.Equals(_lastInventoryMarketplace, marketplaceId, StringComparison.Ordinal))
            {
                Log("Refreshing FBA inventory before collection…");
                _lastInventory = await _api.GetInventoryAsync(marketplaceId, token, ct);
                _lastInventoryMarketplace = marketplaceId;
                InventoryGrid.ItemsSource = _lastInventory;
                InventoryValue.Text = $"{_lastInventory.Sum(x => x.Fulfillable):N0} units";
            }

            var collector = new AnalysisDataCollector(_api);
            var result = await collector.CollectAsync(marketplaceId, token, _lastInventory, timeZone, Log, ct);
            Log("Saving cumulative SQLite history…");
            await _database.SaveAnalysisPackAsync(marketplaceId, result, _lastInventory, ct);
            if (!string.IsNullOrWhiteSpace(result.SalesTrafficRawJson))
            {
                var parsedTraffic = await _trafficStore.SaveFromJsonAsync(marketplaceId, result.SalesTrafficRawJson, ct);
                Log($"Saved {parsedTraffic:N0} Sales & Traffic daily rows to SQLite.");
            }

            DataFolderText.Text = "Latest pack: " + result.OutputFolder + " | Database: " + _database.DatabasePath;
            ConnectionValue.Text = "SP-API OK · history saved";
            Log($"LATEST PACK COMPLETE — {result.HourlySales.Count:N0} hourly points, {result.Orders.Count:N0} order lines, {result.Returns.Count:N0} return lines, {result.Prices.Count:N0} price snapshots.");
            await LoadOfflineViewsAsync(ct);
        }
        catch (OperationCanceledException) { Log("Collection cancelled."); }
        catch (Exception ex)
        {
            ConnectionValue.Text = "Collection error";
            Log("COLLECTION ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Analysis collection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private async void BackfillButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCredentials(out var clientId, out var clientSecret, out var refreshToken)) return;
        if (!int.TryParse(BackfillDaysBox.Text.Trim(), out var days)) days = 729;
        days = Math.Clamp(days, 30, 729);
        BackfillDaysBox.Text = days.ToString();
        var marketplaceId = GetMarketplaceId();
        var ct = BeginOperation();
        LogBox.Clear();
        ConnectionValue.Text = "Backfilling history…";

        try
        {
            Log($"Historical backfill requested for {days} days. This can take a while because Amazon reports are asynchronous and rate limited.");
            var token = await _api.GetAccessTokenAsync(clientId, clientSecret, refreshToken, ct);
            var historicalApi = new HistoricalSpApiClient(_httpClient);
            var backfill = new HistoricalBackfillService(_api, historicalApi, _database);
            var result = await backfill.BackfillAsync(marketplaceId, token, days, Log, ct);
            ConnectionValue.Text = result.Warnings.Count == 0 ? "Backfill complete" : $"Backfill · {result.Warnings.Count} warnings";
            await LoadOfflineViewsAsync(ct);
        }
        catch (OperationCanceledException) { ConnectionValue.Text = "Backfill cancelled"; Log("Historical backfill cancelled. Data already saved remains in the database."); }
        catch (Exception ex)
        {
            ConnectionValue.Text = "Backfill error";
            Log("BACKFILL ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Historical backfill failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private async void LoadOfflineButton_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadOfflineViewsAsync(); }
        catch (Exception ex) { Log("OFFLINE ANALYSIS ERROR: " + ex.Message); MessageBox.Show(ex.Message, "Offline analysis", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ImportAdsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Amazon Ads CSV (*.csv)|*.csv|All files (*.*)|*.*", Title = "Import Amazon Ads report" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var rows = await AdsCsvImporter.ReadAsync(dialog.FileName, GetMarketplaceId(), GetSelectedTimeZone());
            await _database.SaveAdsAsync(rows);
            Log($"Imported {rows.Count:N0} Amazon Ads rows from {dialog.FileName}.");
            await LoadOfflineViewsAsync();
        }
        catch (Exception ex)
        {
            Log("ADS IMPORT ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Amazon Ads import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var generator = new ChatGptReportGenerator(_database);
            var path = await generator.GenerateAsync(BuildFilter(), GetSelectedTimeZone());
            Log("ChatGPT analysis report created: " + path);
            DataFolderText.Text = "ChatGPT report: " + path;
            if (MessageBox.Show("Report created. Open it now?\n\n" + path, "ChatGPT report", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("REPORT ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Report generation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private async Task LoadOfflineViewsAsync(CancellationToken ct = default)
    {
        var filter = BuildFilter();
        var zone = GetSelectedTimeZone();
        await _database.InitializeAsync(ct);
        await _trafficStore.InitializeAsync(ct);

        var hours = await _database.QueryHoursAsync(filter, zone, ct);
        var hourSummary = await _database.QueryHourSummaryAsync(filter, zone, ct);
        var sku = await _database.QuerySkuPerformanceAsync(filter, zone, ct);
        var returns = await _database.QueryReturnReasonsAsync(filter, zone, ct);
        var prices = await _database.QueryPricePerformanceAsync(filter, zone, ct);
        var traffic = await _trafficStore.QueryAsync(filter.MarketplaceId, filter.FromDate, filter.ToDate, ct);
        var bridge = await new AdsSalesBridgeService(_database.DatabasePath).QueryAsync(filter, zone, ct);
        var coverage = await _database.GetCoverageAsync(filter.MarketplaceId, ct);

        HourlyGrid.ItemsSource = hours;
        HoursGrid.ItemsSource = hourSummary;
        TrafficGrid.ItemsSource = traffic;
        SkuGrid.ItemsSource = sku;
        ReturnsGrid.ItemsSource = returns;
        PricePerformanceGrid.ItemsSource = prices;
        AttributionGrid.ItemsSource = bridge;

        ReturnsValue.Text = $"{returns.Sum(x => x.ReturnedUnits):N0} units";
        var best = hourSummary.FirstOrDefault();
        BestHourValue.Text = best is null ? "No hourly data" : $"{best.MarketplaceHourLabel} → KW {best.KuwaitHourLabel}";
        CoverageValue.Text = $"{coverage.HourlyRows:N0}h · {coverage.OrderRows:N0} orders · {coverage.AdRows:N0} ads";
        DataFolderText.Text = "Database: " + coverage.DatabasePath;
        if (coverage.HourlyRows > 0 || coverage.OrderRows > 0) ConnectionValue.Text = "Offline history ready";

        Log($"OFFLINE VIEW — {filter.FromDate:yyyy-MM-dd}..{filter.ToDate:yyyy-MM-dd}, {filter.FromHour:00}:00..{filter.ToHour:00}:59 marketplace time. Exact Kuwait time is shown per hourly row.");
        if (!string.IsNullOrWhiteSpace(filter.SellerSku) || !string.IsNullOrWhiteSpace(filter.Asin))
            Log("SKU/ASIN filter applies to product, return, price and order-based Ads bridge analysis. Store-level Sales API hourly totals remain marketplace-wide.");
    }

    private HistoryFilter BuildFilter()
    {
        var from = FromDatePicker.SelectedDate ?? DateTime.Today.AddDays(-30);
        var to = ToDatePicker.SelectedDate ?? DateTime.Today;
        if (from > to) (from, to) = (to, from);
        var fromHour = FromHourBox.SelectedValue is int fh ? fh : 0;
        var toHour = ToHourBox.SelectedValue is int th ? th : 23;
        return new HistoryFilter(GetMarketplaceId(), from.Date, to.Date, fromHour, toHour, NullIfBlank(SkuFilterBox.Text), NullIfBlank(AsinFilterBox.Text));
    }

    private bool TryGetCredentials(out string clientId, out string clientSecret, out string refreshToken)
    {
        clientId = ClientIdBox.Text.Trim();
        clientSecret = ClientSecretBox.Password.Trim();
        refreshToken = RefreshTokenBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret) && !string.IsNullOrWhiteSpace(refreshToken)) return true;
        MessageBox.Show("Enter Client ID, Client Secret, and Refresh Token first. Offline analysis does not require credentials.", "Missing credentials", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private string GetMarketplaceId() => (MarketplaceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ATVPDKIKX0DER";

    private TimeZoneInfo GetSelectedTimeZone()
    {
        var id = (TimeZoneBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Pacific Standard Time";
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    private CancellationToken BeginOperation()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        SetBusy(true);
        return _operationCts.Token;
    }

    private void EndOperation()
    {
        SetBusy(false);
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        CollectButton.IsEnabled = !busy;
        BackfillButton.IsEnabled = !busy;
        LoadOfflineButton.IsEnabled = !busy;
        ImportAdsButton.IsEnabled = !busy;
        ExportReportButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected override void OnClosed(EventArgs e)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _httpClient.Dispose();
        base.OnClosed(e);
    }

    private sealed record HourChoice(int Hour, string Label);
}
