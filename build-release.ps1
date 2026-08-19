$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Walka.Amazon.ConnectionTester\Walka.Amazon.ConnectionTester.csproj'
$out = Join-Path $PSScriptRoot 'artifacts\win-x64'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
Write-Host "Published to: $out" -ForegroundColor Green
