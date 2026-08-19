using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Walka.Amazon.ConnectionTester.Services;

namespace Walka.Amazon.ConnectionTester;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        var clientSecret = ClientSecretBox.Password.Trim();
        var refreshToken = RefreshTokenBox.Password.Trim();
        var marketplaceId = (MarketplaceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ATVPDKIKX0DER";

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(refreshToken))
        {
            MessageBox.Show("Enter Client ID, Client Secret, and Refresh Token first.", "Missing credentials", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ConnectButton.IsEnabled = false;
        ConnectionValue.Text = "Connecting…";
        OrdersValue.Text = SalesValue.Text = InventoryValue.Text = "—";
        LogBox.Clear();

        try
        {
            var api = new AmazonSpApiClient(_httpClient);
            Log("1/4 Requesting LWA access token…");
            var token = await api.GetAccessTokenAsync(clientId, clientSecret, refreshToken);
            ConnectionValue.Text = "LWA OK";
            Log("LWA authentication succeeded.");

            Log("2/4 Loading seller marketplaces…");
            var marketplaces = await api.GetMarketplacesAsync(token);
            MarketplacesGrid.ItemsSource = marketplaces;
            ConnectionValue.Text = $"SP-API OK · {marketplaces.Count} markets";
            Log($"Loaded {marketplaces.Count} marketplace participation records.");

            Log("3/4 Loading last 7 days Sales API metrics…");
            var sales = await api.GetLast7DaysSalesAsync(marketplaceId, token);
            OrdersValue.Text = $"{sales.Orders:N0} orders / {sales.Units:N0} units";
            SalesValue.Text = string.IsNullOrWhiteSpace(sales.Currency) ? sales.Sales.ToString("N2") : $"{sales.Sales:N2} {sales.Currency}";
            Log($"Sales loaded: {sales.Orders} orders, {sales.Units} units, {sales.Sales:N2} {sales.Currency}.");

            Log("4/4 Loading FBA inventory summaries…");
            var inventory = await api.GetInventoryAsync(marketplaceId, token);
            InventoryGrid.ItemsSource = inventory;
            InventoryValue.Text = $"{inventory.Sum(x => x.Fulfillable):N0} units";
            Log($"Inventory loaded: {inventory.Count} SKUs, {inventory.Sum(x => x.Fulfillable)} fulfillable units.");
            Log("DONE — production SP-API connection is working.");
        }
        catch (Exception ex)
        {
            ConnectionValue.Text = "Failed";
            Log("ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Amazon connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
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
