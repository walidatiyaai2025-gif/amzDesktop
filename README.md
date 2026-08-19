# WALKA Amazon Connection Tester

Small Windows desktop utility for validating a **production Amazon Selling Partner API (SP-API)** connection and loading a few pieces of real seller data.

## What it tests

- Login With Amazon (LWA) refresh-token exchange.
- Seller marketplace participations.
- Last 7 days aggregated Sales API metrics.
- FBA inventory summaries for the selected North America marketplace.

## Security

The application does **not** write the LWA Client Secret or Refresh Token to disk. Credentials are kept in memory only for the current process. Do not commit credentials to this repository.

## Requirements

- Windows 10/11
- .NET 8 SDK to build from source
- A production SP-API app with LWA Client ID, LWA Client Secret, and Refresh Token
- Appropriate SP-API roles (Selling Partner Insights/Product Listing, Pricing, and Amazon Fulfillment/Product Listing for the calls used by this tester)

## Run from source

```powershell
dotnet run --project .\src\Walka.Amazon.ConnectionTester\Walka.Amazon.ConnectionTester.csproj
```

## Build a Windows executable

```powershell
.\build-release.ps1
```

The published files will be under `artifacts\win-x64`.

## Usage

1. Start the app.
2. Paste the LWA Client ID, LWA Client Secret, and Refresh Token.
3. Choose a marketplace (US, Canada, or Mexico).
4. Click **Test connection & load real data**.
5. The app first validates LWA, then loads marketplaces, seven-day sales metrics, and FBA inventory.

> This project intentionally starts read-only. It does not change listings, prices, inventory, or advertising campaigns.
