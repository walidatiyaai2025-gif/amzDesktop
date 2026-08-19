using Walka.Amazon.ConnectionTester.Models;

namespace Walka.Amazon.ConnectionTester.Services;

public sealed class HistoricalBackfillService(
    AmazonSpApiClient api,
    HistoricalSpApiClient historicalApi,
    HistoricalDatabase database)
{
    public async Task<BackfillResult> BackfillAsync(
        string marketplaceId,
        string accessToken,
        int days,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        days = Math.Clamp(days, 30, 729);
        var warnings = new List<string>();
        var now = DateTimeOffset.UtcNow;
        var earliest = now.AddDays(-days);
        var dailyCount = 0;
        var orderCount = 0;
        var returnCount = 0;
        var financeCount = 0;
        var trafficCount = 0;
        var trafficStore = new SalesTrafficStore(database.DatabasePath);

        await database.InitializeAsync(ct);
        await trafficStore.InitializeAsync(ct);
        await database.AddCollectionAsync(marketplaceId, "Historical backfill", $"Requested {days} days", ct);

        try
        {
            progress?.Invoke($"Backfill 1/5 — requesting {days} days of daily Sales API metrics…");
            var daily = await historicalApi.GetDailySalesAsync(marketplaceId, accessToken, days, ct);
            await database.SaveDailySalesAsync(marketplaceId, daily, ct);
            dailyCount = daily.Count;
            progress?.Invoke($"Saved {dailyCount:N0} daily sales rows.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn("Daily Sales", ex, warnings, progress);
        }

        progress?.Invoke("Backfill 2/5 — requesting historical order reports in chunks…");
        foreach (var range in Ranges(earliest, now, 90))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Invoke($"Orders: {range.Start:yyyy-MM-dd} → {range.End:yyyy-MM-dd}");
                var raw = await api.GetAllOrdersReportAsync(marketplaceId, accessToken, range.Start, range.End, ct);
                var rows = AnalysisDataCollector.ParseOrders(raw);
                await database.SaveOrdersAsync(marketplaceId, rows, ct);
                await database.SaveRawDocumentAsync(marketplaceId, "all-orders", range.Start, range.End, raw, ct);
                orderCount += rows.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn($"Orders {range.Start:yyyy-MM-dd}..{range.End:yyyy-MM-dd}", ex, warnings, progress);
            }
        }

        progress?.Invoke("Backfill 3/5 — requesting historical FBA return reports in chunks…");
        foreach (var range in Ranges(earliest, now, 60))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Invoke($"Returns: {range.Start:yyyy-MM-dd} → {range.End:yyyy-MM-dd}");
                var raw = await api.GetFbaReturnsReportAsync(marketplaceId, accessToken, range.Start, range.End, ct);
                var rows = AnalysisDataCollector.ParseReturns(raw);
                await database.SaveReturnsAsync(marketplaceId, rows, ct);
                await database.SaveRawDocumentAsync(marketplaceId, "fba-returns", range.Start, range.End, raw, ct);
                returnCount += rows.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn($"Returns {range.Start:yyyy-MM-dd}..{range.End:yyyy-MM-dd}", ex, warnings, progress);
            }
        }

        progress?.Invoke("Backfill 4/5 — requesting Sales & Traffic history in 30-day chunks…");
        foreach (var range in Ranges(earliest, now, 30))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Invoke($"Sales & Traffic: {range.Start:yyyy-MM-dd} → {range.End:yyyy-MM-dd}");
                var raw = await api.GetSalesAndTrafficReportAsync(marketplaceId, accessToken, range.Start, range.End, ct);
                await database.SaveRawDocumentAsync(marketplaceId, "sales-traffic", range.Start, range.End, raw, ct);
                await trafficStore.SaveFromJsonAsync(marketplaceId, raw, ct);
                trafficCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn($"Sales & Traffic {range.Start:yyyy-MM-dd}..{range.End:yyyy-MM-dd}", ex, warnings, progress);
            }
        }

        progress?.Invoke("Backfill 5/5 — requesting Finance history in <180-day windows…");
        foreach (var range in Ranges(earliest, now.AddMinutes(-3), 179))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Invoke($"Finance: {range.Start:yyyy-MM-dd} → {range.End:yyyy-MM-dd}");
                var pages = await api.GetFinanceTransactionPagesAsync(marketplaceId, accessToken, range.Start, range.End, ct);
                foreach (var page in pages)
                {
                    await database.SaveRawDocumentAsync(marketplaceId, "finance-transactions", range.Start, range.End, page, ct);
                    financeCount++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn($"Finance {range.Start:yyyy-MM-dd}..{range.End:yyyy-MM-dd}", ex, warnings, progress);
            }
        }

        progress?.Invoke($"Historical backfill finished: {dailyCount:N0} daily sales rows, {orderCount:N0} order rows, {returnCount:N0} return rows, {trafficCount:N0} traffic documents, {financeCount:N0} finance pages. Warnings: {warnings.Count}.");
        return new BackfillResult(dailyCount, orderCount, returnCount, financeCount, trafficCount, warnings);
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Ranges(DateTimeOffset start, DateTimeOffset end, int chunkDays)
    {
        var cursor = start;
        while (cursor < end)
        {
            var next = cursor.AddDays(chunkDays);
            if (next > end) next = end;
            yield return (cursor, next);
            cursor = next;
        }
    }

    private static void Warn(string dataset, Exception ex, List<string> warnings, Action<string>? progress)
    {
        var message = $"{dataset}: {ex.Message}";
        warnings.Add(message);
        progress?.Invoke("WARNING — " + message + " Continuing.");
    }
}
