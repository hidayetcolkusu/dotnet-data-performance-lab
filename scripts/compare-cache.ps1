<#
.SYNOPSIS
Runs the cache off/on load comparison and writes its full evidence trail.

.DESCRIPTION
Both arms run the same API binary, the same configuration and the same fixture; the only
difference is Cache:Enabled. Each arm is a fresh API process, warmed up by a separate k6
process, then measured for one constant-arrival-rate run.

Five repetitions alternate AB / BA, so a monotonic drift in machine state (thermal, other
processes, SQL buffer pool growth) cannot be mistaken for a difference between the arms.

Every run keeps its stdout log, the raw k6 JSON sample stream and a summary JSON. A run
whose artifacts are missing, whose checks failed or which dropped iterations is reported as
invalid by compare-report rather than averaged into the result.

Nothing in this script writes to the catalog: the comparison is a read-only measurement.

.EXAMPLE
./scripts/compare-cache.ps1 -Profile default -Output results/local/20260911T101500Z
#>
[CmdletBinding()]
param(
    # Exposed as -Profile, but named SeedProfile: $Profile is a PowerShell
    # automatic variable and assigning to it has side effects beyond this script.
    [Alias('Profile')]
    [ValidateSet('ci', 'default', 'large')][string]$SeedProfile = 'default',
    [string]$Output,
    [ValidateRange(1, 20)][int]$Repetitions = 5,
    [ValidateRange(1, 10000)][int]$Rate = 20,
    [string]$Duration = '60s',
    [string]$WarmupDuration = '30s',
    [ValidateRange(1, 100000)][int]$FixtureCount = 1000,
    [ValidateRange(1024, 65535)][int]$Port = 8080,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'collect-manifest.ps1')

# k6 echoes the script and fixture paths it was given into its own log, and those logs are
# published. An absolute path would bake this machine's home directory into the evidence — noise
# at best, and something CI's hygiene job rejects outright. Run from the repo root and hand k6
# repo-relative paths instead.
Set-Location $repoRoot

$runId = [datetime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'")
$startedAtUtc = [datetime]::UtcNow
if (-not $Output) { $Output = Join-Path $repoRoot "results/local/$runId" }
$outputDirectory = (New-Item -ItemType Directory -Path $Output -Force).FullName
$runsDirectory = (New-Item -ItemType Directory -Path (Join-Path $outputDirectory 'runs') -Force).FullName

$baseUrl = "http://127.0.0.1:$Port"
$apiDll = Join-Path $repoRoot 'src/Lab.Api/bin/Release/net10.0/Lab.Api.dll'
$cliDll = Join-Path $repoRoot 'src/Lab.Cli/bin/Release/net10.0/Lab.Cli.dll'
$fixturePath = Join-Path $outputDirectory 'fixture.json'

# k6 resolves its *output* paths (--out, SUMMARY_OUT) against the working directory, so those can
# be relative and stay out of the published logs. The fixture is different: it is read with k6's
# open(), which resolves against the **script's** directory, not the working directory — a relative
# value there would look for the fixture under experiments/k6/lib/. So FIXTURE stays absolute. It
# is passed as an -e value, which k6 does not echo, so it never reaches a successful run's log.
$outputRelative = [System.IO.Path]::GetRelativePath($repoRoot, $outputDirectory).Replace('\', '/')

# --------------------------------------------------------------------------------------
# Preconditions
# --------------------------------------------------------------------------------------

function Assert-Tool {
    param([string]$Name, [string]$Hint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' was not found on PATH. $Hint"
    }
}

Assert-Tool -Name 'dotnet' -Hint 'Install the .NET SDK pinned in global.json.'
Assert-Tool -Name 'k6' -Hint 'Install k6 (https://k6.io/docs/get-started/installation/).'

if (-not $env:MSSQL_SA_PASSWORD) {
    throw 'MSSQL_SA_PASSWORD is not set. Run ./scripts/bootstrap.ps1 first.'
}

# The CLI refuses to run outside Development/Testing. bootstrap.ps1 sets this, but the
# comparison is also run from a fresh shell, and a missing value fails only after the
# containers are already warm.
if (-not $env:DOTNET_ENVIRONMENT) { $env:DOTNET_ENVIRONMENT = 'Development' }
elseif ($env:DOTNET_ENVIRONMENT -notin @('Development', 'Testing')) {
    throw "DOTNET_ENVIRONMENT is '$env:DOTNET_ENVIRONMENT'; the lab CLI only runs in Development or Testing."
}

# --------------------------------------------------------------------------------------
# Build once, so both arms run byte-identical binaries
# --------------------------------------------------------------------------------------

if (-not $SkipBuild) {
    Write-Host '== dotnet build (Release) ==' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'DataPerformanceLab.slnx') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

foreach ($dll in @($apiDll, $cliDll)) {
    if (-not (Test-Path $dll)) {
        throw "'$dll' is missing. Run without -SkipBuild."
    }
}

# --------------------------------------------------------------------------------------
# Fixture
# --------------------------------------------------------------------------------------

Write-Host "== building a $FixtureCount SKU fixture ==" -ForegroundColor Cyan
& dotnet $cliDll fixture --output $fixturePath --count $FixtureCount
if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed with exit code $LASTEXITCODE." }

$fixture = Get-Content $fixturePath -Raw | ConvertFrom-Json
if ($fixture.dataset.profile -ne $SeedProfile) {
    throw "The database is seeded with profile '$($fixture.dataset.profile)' but -Profile is '$SeedProfile'. " +
          'Re-seed, or pass the profile that is actually loaded.'
}

# --------------------------------------------------------------------------------------
# API process control
# --------------------------------------------------------------------------------------

# A fresh cache namespace per individual run — not one per comparison.
#
# The AB/BA alternation puts two cache-on runs back to back (…AB then BA… runs on, then on),
# so a comparison-wide namespace let the second of the pair start on entries the first had
# just written. The measured hit rate showed it plainly: ~0.29 on AB repetitions against
# ~0.64 on BA. Both arms get a namespace and cache-off never touches it, so Cache:Enabled
# remains the only differing factor — but now every cache-on run is warmed only by its own
# warmup, exactly like every other.
function New-CacheInstanceId {
    param([int]$Repetition, [string]$Order, [string]$Arm)
    return "compare-$runId-$Repetition-$Order-$Arm"
}

function Start-LabApi {
    param(
        [Parameter(Mandatory)][bool]$CacheEnabled,
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$CacheInstanceId)

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $baseUrl
    $env:Cache__Enabled = $CacheEnabled.ToString().ToLowerInvariant()
    $env:Cache__InstanceId = $CacheInstanceId

    # -NoNewWindow: no console window pops up on Windows during a timed measurement.
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) `
        -WorkingDirectory $repoRoot -NoNewWindow -PassThru `
        -RedirectStandardOutput $LogPath -RedirectStandardError ($LogPath -replace '\.log$', '.err')

    Remove-Item Env:\Cache__Enabled, Env:\Cache__InstanceId, Env:\ASPNETCORE_URLS -ErrorAction SilentlyContinue
    return $process
}

function Wait-LabApiReady {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process, [int]$TimeoutSeconds = 60)

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "The API exited with code $($Process.ExitCode) before becoming ready."
        }

        try {
            $response = Invoke-WebRequest -Uri "$baseUrl/health/ready" -TimeoutSec 5 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { return }
        }
        catch {
            # Connection refused while the host is still binding; keep polling.
        }

        Start-Sleep -Milliseconds 500
    }

    throw "The API did not report ready at $baseUrl/health/ready within $TimeoutSeconds seconds."
}

function Stop-LabApi {
    param([System.Diagnostics.Process]$Process)

    if (-not $Process -or $Process.HasExited) { return }

    # Only the process this script started, addressed by its own PID: never a name-based
    # kill that could take out an unrelated dotnet process on the machine.
    Stop-Process -Id $Process.Id -ErrorAction SilentlyContinue
    $null = $Process.WaitForExit(15000)
}

# --------------------------------------------------------------------------------------
# k6 invocation
# --------------------------------------------------------------------------------------

# --------------------------------------------------------------------------------------
# Cache counter snapshots
# --------------------------------------------------------------------------------------

# The counters are process-wide and cumulative, and the warmup drives traffic through the very
# same API process the measured run uses. Reading them only once, at the end, therefore reports
# warmup lookups and measured lookups added together — which is not the hit rate of the window
# whose latency is being reported. Two snapshots bound the measured window instead: one taken
# after the warmup settles, one after the measured run exits. The difference is the measured
# window, and it is the only figure allowed to be quoted as this run's hit rate.
function Get-CacheCounterSnapshot {
    param([Parameter(Mandatory)][string]$Label)

    $response = Invoke-WebRequest -Uri "$baseUrl/internal/cache-metrics" -TimeoutSec 5
    $snapshot = $response.Content | ConvertFrom-Json
    return [ordered]@{
        label                = $Label
        capturedAtUtc        = [datetime]::UtcNow.ToString('o')
        cacheEnabled         = $snapshot.cacheEnabled
        # The effective cache settings of the process that served this window, reported by that
        # process rather than read out of a config file the run may have overridden.
        configuration        = $snapshot.configuration
        hits                 = [long]$snapshot.hits
        misses               = [long]$snapshot.misses
        bypasses             = [long]$snapshot.bypasses
        readFailures         = [long]$snapshot.readFailures
        writeFailures        = [long]$snapshot.writeFailures
        invalidationFailures = [long]$snapshot.invalidationFailures
        sqlFallbacks         = [long]$snapshot.sqlFallbacks
    }
}

function New-CacheCounterDelta {
    param(
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Start,
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$End)

    $hits = $End.hits - $Start.hits
    $misses = $End.misses - $Start.misses
    $bypasses = $End.bypasses - $Start.bypasses
    $eligible = $hits + $misses

    return [ordered]@{
        schema = 'dpl.cache-metrics-delta.v1'
        # Stated explicitly so a reader never has to assume which window the rate describes.
        window = [ordered]@{
            description  = 'measured run only; warmup lookups are excluded by construction'
            startedAtUtc = $Start.capturedAtUtc
            endedAtUtc   = $End.capturedAtUtc
        }
        cacheEnabled = $End.cacheEnabled
        delta = [ordered]@{
            hits                 = $hits
            misses               = $misses
            bypasses             = $bypasses
            totalLookups         = $hits + $misses + $bypasses
            readFailures         = $End.readFailures - $Start.readFailures
            writeFailures        = $End.writeFailures - $Start.writeFailures
            invalidationFailures = $End.invalidationFailures - $Start.invalidationFailures
            sqlFallbacks         = $End.sqlFallbacks - $Start.sqlFallbacks
        }
        # Same definition the API uses: over cache-eligible lookups only, null when there were
        # none, so a cache-off run reports no hit rate instead of a misleading zero.
        hitRate = if ($eligible -eq 0) { $null } else { [double]$hits / [double]$eligible }
        cumulative = [ordered]@{ start = $Start; end = $End }
    }
}

function Invoke-K6 {
    param(
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$LogPath,
        [string]$RawJsonPath,
        [string]$SummaryPath,
        [Parameter(Mandatory)][hashtable]$EnvironmentValues
    )

    $arguments = @('run', '--no-color')
    if ($RawJsonPath) { $arguments += @('--out', "json=$RawJsonPath") }
    foreach ($key in ($EnvironmentValues.Keys | Sort-Object)) {
        $arguments += @('-e', "$key=$($EnvironmentValues[$key])")
    }
    if ($SummaryPath) { $arguments += @('-e', "SUMMARY_OUT=$SummaryPath") }
    # Relative, and the working directory is the repo root — see the note at the top.
    $arguments += $Script

    # Out-Host, not a bare pipeline: anything left on the output stream would be returned
    # by the function alongside the exit code, and the caller would compare k6's log text
    # to 0 instead of its exit status.
    & k6 @arguments 2>&1 | Tee-Object -FilePath $LogPath | Out-Host
    return $LASTEXITCODE
}

# --------------------------------------------------------------------------------------
# The comparison
# --------------------------------------------------------------------------------------

$runIndex = [System.Collections.Generic.List[object]]::new()
$resultFiles = [System.Collections.Generic.List[string]]::new()
$resultFiles.Add('fixture.json')
$runFailures = [System.Collections.Generic.List[string]]::new()

# The cache settings the measured API reported about itself. Kept from the last snapshot taken,
# so the manifest records what a process actually ran with instead of what appsettings.json says.
$lastCacheConfiguration = $null

# Every override this invocation actually used, not the two-flag shape of the documented example.
# Without the rest, a published command reproduces a different measurement than the one recorded.
$effectiveCommand = (
    "./scripts/compare-cache.ps1 -Profile $SeedProfile -Output $Output " +
    "-Repetitions $Repetitions -Rate $Rate -Duration $Duration " +
    "-WarmupDuration $WarmupDuration -FixtureCount $FixtureCount -Port $Port" +
    $(if ($SkipBuild) { ' -SkipBuild' } else { '' }))

for ($repetition = 1; $repetition -le $Repetitions; $repetition++) {
    # AB on odd repetitions, BA on even: the arm that runs first alternates.
    $forward = ($repetition % 2) -eq 1
    $order = if ($forward) { 'AB' } else { 'BA' }
    $arms = if ($forward) { @('cache-off', 'cache-on') } else { @('cache-on', 'cache-off') }

    foreach ($arm in $arms) {
        $stem = "$repetition-$order-$arm"
        Write-Host "== repetition $repetition ($order): $arm ==" -ForegroundColor Cyan

        $apiLog = Join-Path $runsDirectory "$stem.api.log"
        $warmupLog = Join-Path $runsDirectory "$stem.warmup.log"
        $measuredLog = Join-Path $runsDirectory "$stem.k6.log"
        # Relative for the same reason as the fixture: k6 logs the output paths it was given.
        $rawJson = "$outputRelative/runs/$stem.raw.json"
        $summaryJson = "$outputRelative/runs/$stem.summary.json"

        $k6Environment = @{
            BASE_URL        = $baseUrl
            FIXTURE         = $fixturePath
            ARM             = $arm
            RATE            = $Rate
            DURATION        = $Duration
            WARMUP_DURATION = $WarmupDuration
            REPETITION      = $repetition
            ORDER           = $order
        }

        # Captured even when a step throws, so the index records k6's own verdict rather than
        # leaving compare-report to infer it from which files happen to exist.
        $warmupExit = $null
        $measuredExit = $null

        $api = $null
        try {
            $cacheInstanceId = New-CacheInstanceId -Repetition $repetition -Order $order -Arm $arm
            $api = Start-LabApi -CacheEnabled ($arm -eq 'cache-on') -LogPath $apiLog `
                -CacheInstanceId $cacheInstanceId
            Wait-LabApiReady -Process $api

            $warmupExit = Invoke-K6 -Script 'experiments/k6/warmup.js' -LogPath $warmupLog `
                -EnvironmentValues $k6Environment

            if ($warmupExit -ne 0) {
                # No summary file is written, so compare-report reports this run as a
                # missing artifact rather than as a latency observation.
                $runFailures.Add("$stem : warmup exited $warmupExit; the measured run was skipped.")
                Write-Warning "warmup failed for $stem (exit $warmupExit); skipping the measured run."
            }
            else {
                # Taken after the warmup has exited and before the measured run starts, so every
                # lookup the warmup caused is on the far side of this boundary.
                $countersStart = Get-CacheCounterSnapshot -Label 'after-warmup'
                $countersStart | ConvertTo-Json -Depth 4 |
                    Set-Content -Path (Join-Path $runsDirectory "$stem.cache-metrics-start.json") -Encoding utf8

                $measuredExit = Invoke-K6 -Script 'experiments/k6/product-detail.js' -LogPath $measuredLog `
                    -RawJsonPath $rawJson -SummaryPath $summaryJson -EnvironmentValues $k6Environment

                if ($measuredExit -ne 0) {
                    $runFailures.Add("$stem : k6 exited $measuredExit (thresholds not met).")
                    Write-Warning "k6 exited $measuredExit for $stem."
                }

                # Read the counters after the measured run, never during it. The delta between the
                # two snapshots — not the cumulative end value — is this run's hit rate.
                $countersEnd = Get-CacheCounterSnapshot -Label 'after-measured-run'
                $lastCacheConfiguration = $countersEnd.configuration
                $countersEnd | ConvertTo-Json -Depth 4 |
                    Set-Content -Path (Join-Path $runsDirectory "$stem.cache-metrics-end.json") -Encoding utf8

                $delta = New-CacheCounterDelta -Start $countersStart -End $countersEnd
                $delta | ConvertTo-Json -Depth 5 |
                    Set-Content -Path (Join-Path $runsDirectory "$stem.cache-metrics-delta.json") -Encoding utf8

                # A counter that went backwards means the API process was restarted mid-run, so
                # the window these numbers describe is not the one the latency came from.
                $negative = @($delta.delta.Keys | Where-Object { $delta.delta[$_] -lt 0 })
                if ($negative.Count -gt 0) {
                    $runFailures.Add("$stem : cache counter(s) $($negative -join ', ') went backwards; " +
                                     'the measured window cannot be bounded.')
                    Write-Warning "$stem : negative cache counter delta ($($negative -join ', '))."
                }
            }
        }
        catch {
            $runFailures.Add("$stem : $($_.Exception.Message)")
            Write-Warning "$stem failed: $($_.Exception.Message)"
        }
        finally {
            Stop-LabApi -Process $api
        }

        # The small artifacts a complete run must leave behind — the ones a published directory
        # can actually carry. The raw sample stream and the API request log are deliberately
        # absent: both run to megabytes, stay local by design, and the manifest records their
        # hash and size instead, so their absence from a published directory is not a defect.
        $requiredArtifacts = @(
            "runs/$stem.summary.json"
            "runs/$stem.k6.log"
            "runs/$stem.warmup.log"
            "runs/$stem.cache-metrics-delta.json"
        )

        $runIndex.Add([ordered]@{
            repetition        = $repetition
            order             = $order
            arm               = $arm
            summaryFile       = "runs/$stem.summary.json"
            warmupExitCode    = $warmupExit
            measuredExitCode  = $measuredExit
            requiredArtifacts = $requiredArtifacts
        })

        foreach ($artifact in @("$stem.api.log", "$stem.api.err", "$stem.warmup.log", "$stem.k6.log",
                                "$stem.raw.json", "$stem.summary.json",
                                "$stem.cache-metrics-start.json", "$stem.cache-metrics-end.json",
                                "$stem.cache-metrics-delta.json")) {
            if (Test-Path (Join-Path $runsDirectory $artifact)) { $resultFiles.Add("runs/$artifact") }
        }

        # The index is rewritten after every run, so an interrupted comparison still records
        # which runs were supposed to exist — and what it had declared it would measure.
        [ordered]@{
            schema   = 'dpl.load-run-index.v1'
            expected = [ordered]@{
                repetitions     = $Repetitions
                profile         = $SeedProfile
                rate            = $Rate
                duration        = $Duration
                warmupDuration  = $WarmupDuration
                fixtureHash     = $fixture.fixtureHash
                fixtureSkuCount = $fixture.count
            }
            runs     = @($runIndex)
        } | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $outputDirectory 'runs.json') -Encoding utf8
    }
}

# --------------------------------------------------------------------------------------
# Aggregation and manifest
# --------------------------------------------------------------------------------------

Write-Host '== aggregating ==' -ForegroundColor Cyan
& dotnet $cliDll compare-report --input $outputDirectory
$reportExit = $LASTEXITCODE

foreach ($artifact in @('runs.json', 'comparison.json', 'comparison.md')) {
    if (Test-Path (Join-Path $outputDirectory $artifact)) { $resultFiles.Add($artifact) }
}

# The raw k6 sample streams and the API request logs are large and stay on the measuring machine
# by design. They are recorded by hash and size rather than published, so a reader can verify a
# local copy and re-create the missing one instead of having to trust the summaries alone.
$localOnlyFiles = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $runIndex) {
    $stem = "$($entry.repetition)-$($entry.order)-$($entry.arm)"
    foreach ($suffix in @('raw.json', 'api.log', 'api.err')) {
        $relative = "runs/$stem.$suffix"
        if (Test-Path (Join-Path $outputDirectory $relative)) { $localOnlyFiles.Add($relative) }
    }
}
# A path cannot be both published evidence and a local-only record.
$resultFiles = [System.Collections.Generic.List[string]]@(
    $resultFiles | Where-Object { $_ -notin $localOnlyFiles })

$status = if ($reportExit -eq 0 -and $runFailures.Count -eq 0) { 'completed' } else { 'failed' }
$failureReason = if ($runFailures.Count -gt 0) { $runFailures -join ' | ' }
                 elseif ($reportExit -ne 0) { 'compare-report reported an invalid comparison.' }
                 else { $null }

$manifestPath = Write-LoadRunManifest -RepoRoot $repoRoot -OutputDirectory $outputDirectory `
    -RunId $runId -Status $status -StartedAtUtc $startedAtUtc -Fixture $fixture `
    -Parameters @{
        profile        = $SeedProfile
        repetitions    = $Repetitions
        rate           = $Rate
        duration       = $Duration
        warmupDuration = $WarmupDuration
        fixtureCount   = $FixtureCount
        fixtureHash    = $fixture.fixtureHash
        baseUrl        = $baseUrl
        # The real alternation this run used, not the five-repetition default.
        roundOrder     = (1..$Repetitions | ForEach-Object { if ($_ % 2) { 'AB' } else { 'BA' } }) -join '/'
        cacheInstance  = "compare-$runId-<rep>-<order>-<arm>"
        differingFactor = 'Cache:Enabled'
    } `
    -ResultFiles $resultFiles `
    -LocalOnlyFiles $localOnlyFiles `
    -CacheConfiguration $lastCacheConfiguration `
    -Command $effectiveCommand `
    -FailureReason $failureReason

Write-Host "Manifest: $manifestPath"
Write-Host "Artifacts: $outputDirectory"

if ($status -eq 'completed') {
    Write-Host 'Comparison completed and every run is valid.' -ForegroundColor Green
    exit 0
}

Write-Warning 'The comparison is not valid evidence; see comparison.md and manifest.json.'
exit 2
