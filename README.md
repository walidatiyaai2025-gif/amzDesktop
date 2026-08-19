# WALKA Amazon Analyzer

Windows desktop analytics application for collecting Amazon Selling Partner API (SP-API) data into a cumulative local database and analyzing it even when the computer is offline from Amazon.

## Current capabilities

- Production LWA/SP-API connection validation.
- US, Canada and Mexico marketplace selection.
- Last-7-day sales and current FBA inventory quick view.
- Latest analysis collection:
  - hourly Sales API metrics,
  - All Orders report,
  - FBA customer returns,
  - Sales & Traffic,
  - Finance transactions,
  - seller price snapshots,
  - inventory snapshots.
- Cumulative SQLite history at `%LOCALAPPDATA%\WALKA.Analyzer\Data\walka-history.db`.
- Best-effort historical backfill up to 729 days where each Amazon dataset permits it.
- Offline date, hour, SKU and ASIN filtering.
- Marketplace-time and Kuwait-time views side-by-side.
- Sales & Traffic analytics: sessions, page views, Unit Session %, Buy Box %, refunds and average selling price.
- SKU sales / realized-price / promotion / return analysis.
- Return reason and disposition summaries.
- Price-response evidence from actual order history.
- Amazon Ads CSV import bridge with campaign/ad group/keyword/search-term/placement metrics.
- Ads-vs-total-sales bridge with spend, attributed sales/orders, estimated non-ad sales, ACOS and TACOS.
- Shareable Markdown report designed for ChatGPT analysis, without API credentials or raw customer comments.

## Important historical-data behavior

Amazon endpoints do not all expose the same lookback. The analyzer therefore stores every successful collection locally and deduplicates/upserts it so the local history grows over time. Hourly data is especially valuable to collect frequently because Amazon's hourly Sales API lookback is shorter than daily/report history.

The **Backfill history** action requests the maximum practical history in chunks and continues when one optional dataset is unavailable. Previously saved data is not deleted when a later request fails.

## Advertising attribution

The local database already contains an `ads_performance` schema. Until direct Amazon Ads API authorization is connected, use **Import Amazon Ads CSV** to add advertising history. The app combines total order history with Amazon's ad-attributed metrics for directional organic-vs-ad analysis.

Ad-attributed conversions use Amazon attribution windows, so `total sales - ad-attributed sales` is an estimate rather than deterministic order-level attribution. The UI and generated report state that limitation explicitly.

## Kuwait time

Amazon/marketplace timestamps are stored in UTC and converted for analysis. Hourly views show the selected marketplace timezone and the corresponding Kuwait time (`UTC+03:00`) for the actual timestamp. This preserves DST changes in US marketplace time instead of using a fixed manual hour offset.

## Security

- LWA Client Secret and Refresh Token are not persisted by the application.
- Credentials remain in process memory only.
- The SQLite history contains business analytics data, not the LWA credentials.
- The generated ChatGPT report intentionally excludes API credentials and raw customer return comments.
- The current application is read-only with respect to Amazon: it does not change campaigns, listings, prices or inventory.

## Requirements

- Windows 10/11 x64.
- A production SP-API app and self-authorization with the required roles.
- LWA Client ID, LWA Client Secret and Refresh Token.
- .NET 8 SDK only if building from source; the published Windows artifact is self-contained.

## Run from source

```powershell
dotnet run --project .\src\Walka.Amazon.ConnectionTester\Walka.Amazon.ConnectionTester.csproj
```

## Build a Windows executable

```powershell
.\build-release.ps1
```

Published files are written to `artifacts\win-x64`.

## Suggested workflow

1. Choose the Amazon marketplace and its local timezone.
2. Enter the LWA credentials locally and click **Test connection**.
3. Click **Collect latest pack** to capture current hourly, inventory, price, order, return, traffic and financial information.
4. Run **Backfill history** once to populate as much older data as Amazon makes available.
5. Use the offline date/hour/SKU/ASIN filters without reconnecting to Amazon.
6. Import Amazon Ads CSV reports until direct Ads API integration is enabled.
7. Review **Best Hours**, **Hourly · Market + Kuwait**, **Sales & Traffic**, **SKU Performance**, **Return Reasons**, **Price Response**, and **Ads vs Sales**.
8. Click **Export ChatGPT report** and upload the generated Markdown file to ChatGPT for deeper analysis and experiment planning.

## Data locations

- SQLite database: `%LOCALAPPDATA%\WALKA.Analyzer\Data\walka-history.db`
- Raw/latest analysis packs: `%LOCALAPPDATA%\WALKA.Analyzer\Data\<marketplace>\<timestamp>`
- ChatGPT reports: `%LOCALAPPDATA%\WALKA.Analyzer\Reports`

> This remains a read-only analytics build. Campaign/bid/budget writes will be added only after Amazon Ads API authorization and a separate approval/audit safety layer are in place.
