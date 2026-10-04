<#
.SYNOPSIS
Prepare the current PowerShell session for local lab work.

.DESCRIPTION
- creates .env from .env.example if missing
- loads MSSQL_SA_PASSWORD and the host port settings from .env into the process environment
- mirrors DPL_SQL_PORT/DPL_REDIS_PORT into the app's Data__* configuration keys, so the
  containers and the application always agree on where the database is
- sets DOTNET_ENVIRONMENT/ASPNETCORE_ENVIRONMENT to Development
- with -StartContainers, starts SQL Server + Redis and waits for healthchecks

.NOTES
Run this IN the shell you are going to work in:

    ./scripts/bootstrap.ps1 -StartContainers

`pwsh -File scripts/bootstrap.ps1` does NOT work for this purpose. It starts a second PowerShell,
sets the variables there, and throws them away on exit — the containers start, but the shell you
are typing in never learns the password, the ports or the environment. The script detects that
case and says so rather than letting the next command fail with a confusing error.

.EXAMPLE
./scripts/bootstrap.ps1 -StartContainers
#>
[CmdletBinding()]
param([switch]$StartContainers)

$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot '.env'
$envExample = Join-Path $repoRoot '.env.example'

if (-not (Test-Path $envFile)) {
    if (Test-Path $envExample) {
        Copy-Item $envExample $envFile
        Write-Warning 'Created .env from .env.example - review MSSQL_SA_PASSWORD before use.'
    }
    else {
        throw '.env not found and .env.example is missing.'
    }
}

Get-Content $envFile | ForEach-Object {
    $line = $_.Trim()
    if ($line -and -not $line.StartsWith('#')) {
        $parts = $line -split '=', 2
        if ($parts.Length -eq 2) {
            [Environment]::SetEnvironmentVariable($parts[0].Trim(), $parts[1].Trim(), 'Process')
        }
    }
}

# One source of truth for the ports. compose.yaml reads DPL_*_PORT to publish them; the app
# reads Data__*__Port to connect. Deriving the second from the first here is what keeps a
# machine whose 1433 is already taken from silently pointing the app at the wrong server.
$sqlPort = if ($env:DPL_SQL_PORT) { $env:DPL_SQL_PORT } else { '1433' }
$redisPort = if ($env:DPL_REDIS_PORT) { $env:DPL_REDIS_PORT } else { '6379' }

foreach ($port in @($sqlPort, $redisPort)) {
    if ($port -notmatch '^[0-9]+$' -or [int]$port -lt 1024 -or [int]$port -gt 65535) {
        throw "Invalid port '$port' in .env; DPL_SQL_PORT and DPL_REDIS_PORT must be 1024-65535."
    }
}

$env:DPL_SQL_PORT = $sqlPort
$env:DPL_REDIS_PORT = $redisPort
$env:Data__SqlServer__Port = $sqlPort
$env:Data__Redis__Port = $redisPort

$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Write-Host 'Loaded .env into the session (MSSQL_SA_PASSWORD) and set Development environment.'

if ($StartContainers) {
    Write-Host 'Starting SQL Server + Redis containers (waits for healthchecks)...' -ForegroundColor Cyan
    docker compose up -d --wait
    if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed.' }
    Write-Host "SQL Server: 127.0.0.1:$sqlPort, Redis: 127.0.0.1:$redisPort" -ForegroundColor Green
}

# The whole point of this script is the environment it leaves behind, and that only survives if it
# ran in the shell that is going to use it. When pwsh was launched *for* this script, everything
# above is about to be discarded — so say so here, where it is still actionable, rather than
# letting the next `dotnet run` fail with "MSSQL_SA_PASSWORD is not set".
$ownCommandLine = [Environment]::GetCommandLineArgs() -join ' '
if ($ownCommandLine -match '(?i)bootstrap\.ps1') {
    Write-Warning @"
This ran as its own PowerShell process, so the environment it just set up is about to be thrown
away. The containers are up, but this shell has no MSSQL_SA_PASSWORD, ports or environment.

Run it in the shell you will work in instead:

    ./scripts/bootstrap.ps1 -StartContainers

(or dot-source it: . ./scripts/bootstrap.ps1 -StartContainers)
"@
}
