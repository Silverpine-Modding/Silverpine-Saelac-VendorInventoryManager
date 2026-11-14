$ErrorActionPreference = 'Stop'
$allocationSource = Join-Path $PSScriptRoot '..\StockAllocation.cs'
$testSource = Join-Path $PSScriptRoot 'StockAllocationTests.cs'
Add-Type -Path $allocationSource, $testSource
[StockAllocationTests]::Run()
