<#
.SYNOPSIS
Full local verification: locked restore, Release build, unit + integration tests.

.DESCRIPTION
Requires the .NET 10 SDK pinned in global.json and a running Docker engine
(integration tests use real SQL Server 2022 and Redis 7.4 via Testcontainers).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host '== dotnet restore (locked) ==' -ForegroundColor Cyan
dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '== dotnet build (Release) ==' -ForegroundColor Cyan
dotnet build -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '== dotnet test (Release) ==' -ForegroundColor Cyan
dotnet test -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Verify completed successfully.' -ForegroundColor Green
exit 0
