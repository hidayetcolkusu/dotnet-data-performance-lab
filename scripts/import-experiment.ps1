<#
.SYNOPSIS
Runs every import scenario the plan asks for evidence of, against real SQL Server, and writes the
CLI exit codes and SQL-derived reports as publishable artifacts.

.DESCRIPTION
Seven scenarios, each a real `dpl` invocation whose exit code is recorded rather than asserted in a
test harness:

  1 success            a clean file imports, exit 0
  2 file-rejected      a bad header is a file-level failure, exit 1, no job created
  3 row-rejected       a mixed file completes with rejections, exit 2
  4 interrupted        a crash before a batch commits leaves a resumable job
  5 resumed            that job is resumed from its checkpoint and completes
  6 re-import          the same bytes return the stored result without redoing work
  7 busy               a second importer is refused while the lock is held, exit 3

The integration tests already prove these behaviours; this script produces the *evidence package*
the plan asks for separately, because a test source file is not a recorded command and exit code.

This runs against a dedicated test-prefixed database, not the API's, so the catalog a published
load measurement was taken against is never mutated. It needs DOTNET_ENVIRONMENT=Testing: the
fault injection used by scenarios 4 and 5 is refused in any other environment.

The scenario database is dropped at the end, and only after it has proven — by its LabMetadata
marker — that it belongs to the lab. Pass -KeepDatabase to inspect its rows instead.

.EXAMPLE
./scripts/import-experiment.ps1 -Output results/local/import-20260912
#>
[CmdletBinding()]
param(
    [string]$Output,
    [ValidateRange(1, 2000)][int]$BatchSize = 100,
    [switch]$SkipBuild,
    # Keeps the scenario database so its rows can be inspected. Off by default: a run that leaves
    # a database behind every time turns into a pile of them within an afternoon.
    [switch]$KeepDatabase
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'collect-manifest.ps1')

$runId = [datetime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'")
$startedAtUtc = [datetime]::UtcNow
if (-not $Output) { $Output = Join-Path $repoRoot "results/local/import-$runId" }
$outputDirectory = (New-Item -ItemType Directory -Path $Output -Force).FullName

$cliDll = Join-Path $repoRoot 'src/Lab.Cli/bin/Release/net10.0/Lab.Cli.dll'
$sqlContainer = "$(if ($env:DPL_CONTAINER_PREFIX) { $env:DPL_CONTAINER_PREFIX } else { 'dpl' })-sql"

# Paths handed to the CLI are kept repo-relative, and the script runs from the repo root. The CLI
# echoes the path it was given, so an absolute one would bake this machine's home directory into a
# published log — which CI's hygiene job rejects, and rightly: it is noise at best and an
# information leak at worst.
Set-Location $repoRoot
$samples = 'data/samples'
$outputRelative = [System.IO.Path]::GetRelativePath($repoRoot, $outputDirectory).Replace('\', '/')

# --------------------------------------------------------------------------------------
# Preconditions
# --------------------------------------------------------------------------------------

if (-not $env:MSSQL_SA_PASSWORD) {
    throw 'MSSQL_SA_PASSWORD is not set. Run ./scripts/bootstrap.ps1 -StartContainers first.'
}

# Fault injection — scenarios 4 and 5 — is only armable in Testing. Setting it here rather than
# asking the reader to remember keeps the documented command a single line that actually works.
$env:DOTNET_ENVIRONMENT = 'Testing'

if (-not $SkipBuild) {
    Write-Host '== dotnet build (Release) ==' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'DataPerformanceLab.slnx') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path $cliDll)) { throw "'$cliDll' is missing. Run without -SkipBuild." }

# A dedicated database per run: the API's catalog, and any load measurement taken against it,
# stays untouched. The test prefix is what makes this name acceptable to the CLI in Testing.
$database = "DataPerformanceLab_Test_import$($runId.Substring(9, 6))"

Write-Host "== preparing $database ==" -ForegroundColor Cyan
& dotnet $cliDll db migrate --database $database
if ($LASTEXITCODE -ne 0) { throw "Could not migrate '$database' (exit $LASTEXITCODE)." }

# The import's categoryId is a real foreign key, so the 20 categories must exist before any row
# can land. The ci profile is the cheapest way to create them; its SKU-* products do not collide
# with the samples' IMP-* SKUs, so the scenarios below still start from an empty import history.
& dotnet $cliDll seed --profile ci --database $database
if ($LASTEXITCODE -ne 0) { throw "Could not seed '$database' (exit $LASTEXITCODE)." }

# --------------------------------------------------------------------------------------
# Scenario driver
# --------------------------------------------------------------------------------------

$scenarios = [System.Collections.Generic.List[object]]::new()
$resultFiles = [System.Collections.Generic.List[string]]::new()

function Invoke-Dpl {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$ExpectedExit,
        [string]$Expectation)

    $logRelative = "logs/$Name.log"
    $logPath = Join-Path $outputDirectory $logRelative
    $null = New-Item -ItemType Directory -Path (Split-Path $logPath) -Force

    Write-Host "== $Name ==" -ForegroundColor Cyan
    & dotnet $cliDll @Arguments 2>&1 | Tee-Object -FilePath $logPath | Out-Host
    $exit = $LASTEXITCODE

    $resultFiles.Add($logRelative)

    $record = [ordered]@{
        scenario     = $Name
        expectation  = $Expectation
        # The command as it would be typed, with the password-free arguments only.
        command      = "dpl " + ($Arguments -join ' ')
        expectedExit = $ExpectedExit
        actualExit   = $exit
        matched      = ($exit -eq $ExpectedExit)
        log          = $logRelative
    }

    if (-not $record.matched) {
        Write-Warning "$Name exited $exit but $ExpectedExit was expected."
    }

    $scenarios.Add($record)
    return $record
}

# Pulls the job id out of the CLI's own output, so the recorded id is the one SQL holds.
function Get-JobIdFromLog {
    param([Parameter(Mandatory)][string]$LogRelativePath)

    $text = Get-Content (Join-Path $outputDirectory $LogRelativePath) -Raw
    $match = [regex]::Match($text, 'Job ([0-9a-fA-F-]{36})')
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

# Regenerates the report from SQL alone. The original CSV is deliberately not passed: a report
# that still reproduces without the source file is the contract being demonstrated.
function Write-JobReport {
    param([Parameter(Mandatory)][string]$JobId, [Parameter(Mandatory)][string]$Name)

    $reportRelative = "reports/$Name"
    $reportPath = Join-Path $outputDirectory $reportRelative
    $null = New-Item -ItemType Directory -Path $reportPath -Force

    & dotnet $cliDll report --job-id $JobId --output "$outputRelative/$reportRelative" --database $database 2>&1 |
        Tee-Object -FilePath (Join-Path $outputDirectory "logs/$Name-report.log") | Out-Host
    $reportExit = $LASTEXITCODE

    $resultFiles.Add("logs/$Name-report.log")
    foreach ($file in @('report.json', 'rejected-rows.csv')) {
        if (Test-Path (Join-Path $reportPath $file)) { $resultFiles.Add("$reportRelative/$file") }
    }

    return $reportExit
}

# --------------------------------------------------------------------------------------
# The scenarios
# --------------------------------------------------------------------------------------

$failures = [System.Collections.Generic.List[string]]::new()

# 1 — a clean file imports completely.
$success = Invoke-Dpl -Name '1-success' -ExpectedExit 0 `
    -Expectation 'every row accepted; exit 0' `
    -Arguments @('import', '--file', "$samples/catalog-valid.csv",
                 '--batch-size', $BatchSize, '--database', $database)

$successJobId = Get-JobIdFromLog -LogRelativePath $success.log
if ($successJobId) { $null = Write-JobReport -JobId $successJobId -Name '1-success' }

# 2 — a bad header is a file-level failure: no job exists to resume.
$null = Invoke-Dpl -Name '2-file-rejected' -ExpectedExit 1 `
    -Expectation 'bad header rejects the whole file; exit 1 and no job created' `
    -Arguments @('import', '--file', "$samples/catalog-bad-header.csv",
                 '--batch-size', $BatchSize, '--database', $database)

# 3 — a mixed file completes, with the invalid rows rejected rather than failing the file.
$rowRejected = Invoke-Dpl -Name '3-row-rejected' -ExpectedExit 2 `
    -Expectation 'invalid rows are rejected, valid rows land; exit 2' `
    -Arguments @('import', '--file', "$samples/catalog-mixed.csv",
                 '--batch-size', $BatchSize, '--database', $database)

$mixedJobId = Get-JobIdFromLog -LogRelativePath $rowRejected.log
if ($mixedJobId) { $null = Write-JobReport -JobId $mixedJobId -Name '3-row-rejected' }

# 4 — a crash before a batch commits. The CLI exits 1 and leaves a resumable job; the rows of the
#     aborted batch must not be present, because the batch transaction never committed.
$interrupted = Invoke-Dpl -Name '4-interrupted-before-commit' -ExpectedExit 1 `
    -Expectation 'aborts before batch 2 commits; exit 1, job left resumable' `
    -Arguments @('import', '--file', "$samples/catalog-duplicate-sku.csv",
                 '--batch-size', '1', '--fail-before-commit-on-batch', '2',
                 '--database', $database)

$interruptedJobId = Get-JobIdFromLog -LogRelativePath $interrupted.log

# 5 — resuming that job from its stored checkpoint, without the original file.
if ($interruptedJobId) {
    $null = Invoke-Dpl -Name '5-resumed' -ExpectedExit 2 `
        -Expectation 'resumes from the SQL checkpoint without the CSV; completes with rejections' `
        -Arguments @('resume', '--job-id', $interruptedJobId, '--batch-size', '1',
                     '--database', $database)

    $null = Write-JobReport -JobId $interruptedJobId -Name '5-resumed'
}
else {
    $failures.Add('4-interrupted-before-commit did not report a job id, so 5-resumed was skipped.')
    Write-Warning 'No job id was found for the interrupted run; the resume scenario was skipped.'
}

# 6 — the same bytes again. The finished job is returned unchanged: same id, same counts, same
#     exit code. Exit 2 is correct here and is not a new failure — it is the stored result.
$reimport = Invoke-Dpl -Name '6-reimport-same-bytes' -ExpectedExit 2 `
    -Expectation 'identical bytes return the stored result: same job id, same exit 2' `
    -Arguments @('import', '--file', "$samples/catalog-mixed.csv",
                 '--batch-size', $BatchSize, '--database', $database)

$reimportJobId = Get-JobIdFromLog -LogRelativePath $reimport.log
$jobIdReused = ($mixedJobId -and $reimportJobId -and $mixedJobId -eq $reimportJobId)
if (-not $jobIdReused) {
    $failures.Add("6-reimport-same-bytes produced job '$reimportJobId' but '$mixedJobId' was expected.")
}

# 7 — a second importer while the lock is held. The lock is taken from inside the SQL container
#     by a session that then sleeps, which makes this deterministic rather than a race against a
#     concurrently running import. The password is read from the container's own environment, so
#     it never appears on a host command line or in a log.
$lockHeld = $false
if (Get-Command docker -ErrorAction SilentlyContinue) {
    $lockSql = "EXEC sp_getapplock @Resource='DataPerformanceLab:import', " +
               "@LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; " +
               "WAITFOR DELAY '00:00:30';"
    $lockCommand = '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" ' +
                   "-d $database -Q `"$lockSql`""

    & docker exec -d $sqlContainer bash -c $lockCommand 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        # Give the holder a moment to actually acquire it before the importer asks.
        Start-Sleep -Seconds 3
        $lockHeld = $true
    }
    else {
        Write-Warning 'Could not take the import lock inside $sqlContainer; the busy scenario was skipped.'
    }
}
else {
    Write-Warning 'docker is not on PATH; the busy scenario was skipped.'
}

if ($lockHeld) {
    $null = Invoke-Dpl -Name '7-busy' -ExpectedExit 3 `
        -Expectation 'another session holds the import lock; exit 3 and nothing is changed' `
        -Arguments @('import', '--file', "$samples/catalog-valid.csv",
                     '--batch-size', $BatchSize, '--database', $database)
}
else {
    $failures.Add('7-busy could not run: the import lock could not be held.')
}

# --------------------------------------------------------------------------------------
# Summary, manifest and teardown
# --------------------------------------------------------------------------------------

foreach ($scenario in $scenarios) {
    if (-not $scenario.matched) {
        $failures.Add("$($scenario.scenario): exit $($scenario.actualExit), expected $($scenario.expectedExit).")
    }
}

$summary = [ordered]@{
    schema    = 'dpl.import-experiment.v1'
    runId     = $runId
    database  = $database
    batchSize = $BatchSize
    notes     = @(
        'Every exit code here is the real CLI exit code of the command shown, not an assertion.'
        'Scenario 6 exits 2 because it returns the stored result of a job that had rejections.'
        'A different byte sequence carrying the same products would create its own job and skip them.'
        'Reports are regenerated from SQL with the original CSV absent.'
        'Scenario 7 holds the applock from a separate SQL session so the refusal is deterministic.'
    )
    scenarios = @($scenarios)
    jobIds    = [ordered]@{
        success     = $successJobId
        mixed       = $mixedJobId
        interrupted = $interruptedJobId
        reimport    = $reimportJobId
    }
    reimportReusedTheJob = $jobIdReused
    status    = if ($failures.Count -eq 0) { 'completed' } else { 'failed' }
}

$summary | ConvertTo-Json -Depth 6 |
    Set-Content -Path (Join-Path $outputDirectory 'import-experiment.json') -Encoding utf8
$resultFiles.Add('import-experiment.json')

$manifestPath = Write-LoadRunManifest -RepoRoot $repoRoot -OutputDirectory $outputDirectory `
    -RunId $runId -Status $summary.status -StartedAtUtc $startedAtUtc `
    -Parameters @{
        experiment = 'import-scenarios'
        database   = $database
        batchSize  = $BatchSize
        samples    = 'data/samples/catalog-valid.csv, catalog-bad-header.csv, catalog-mixed.csv, catalog-duplicate-sku.csv'
        environment = 'Testing (required to arm import fault injection)'
    } `
    -ResultFiles $resultFiles `
    -Command "./scripts/import-experiment.ps1 -Output $Output -BatchSize $BatchSize" `
    -FailureReason $(if ($failures.Count -gt 0) { $failures -join ' | ' } else { $null })

Write-Host "Manifest: $manifestPath"
Write-Host "Artifacts: $outputDirectory"
Write-Host ''

# --------------------------------------------------------------------------------------
# Teardown
# --------------------------------------------------------------------------------------

# The drop applies the same rule the CLI does: a database is only dropped once it has proven it
# belongs to the lab. The name prefix is a filter, never the authority — so this checks the
# LabMetadata marker in SQL and silently does nothing if it is absent or wrong. Belt and braces
# for a statement that is, after all, a DROP DATABASE built from a variable.
function Remove-ScenarioDatabase {
    param([Parameter(Mandatory)][string]$Name)

    if ($Name -notlike 'DataPerformanceLab_Test_*') {
        Write-Warning "Refusing to drop '$Name': not a test-prefixed scenario database."
        return $false
    }

    $dropSql = @"
SET NOCOUNT ON;
IF DB_ID(N'$Name') IS NULL BEGIN PRINT 'absent'; RETURN; END
DECLARE @marker nvarchar(256);
DECLARE @probe nvarchar(max) = N'SELECT @v = MetadataValue FROM [$Name].dbo.LabMetadata WHERE MetadataKey = N''LabIdentity''';
BEGIN TRY EXEC sp_executesql @probe, N'@v nvarchar(256) OUTPUT', @v = @marker OUTPUT; END TRY
BEGIN CATCH SET @marker = NULL; END CATCH
IF @marker <> N'dotnet-data-performance-lab' OR @marker IS NULL
BEGIN PRINT 'not-a-lab-database'; RETURN; END
ALTER DATABASE [$Name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$Name];
PRINT 'dropped';
"@

    $command = '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" ' +
               "-h -1 -W -Q `"$($dropSql -replace '"', '\"')`""

    $output = & docker exec $sqlContainer bash -c $command 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not drop '$Name'. Output: $($output -join ' ')"
        return $false
    }

    if ($output -match 'dropped') { return $true }

    Write-Warning "'$Name' was not dropped: $($output -join ' ')"
    return $false
}

if ($KeepDatabase) {
    Write-Host "The scenario database '$database' was kept (-KeepDatabase)." -ForegroundColor DarkGray
}
elseif (Get-Command docker -ErrorAction SilentlyContinue) {
    if (Remove-ScenarioDatabase -Name $database) {
        Write-Host "Dropped the scenario database '$database'." -ForegroundColor DarkGray
    }
}
else {
    Write-Host "docker is not on PATH; '$database' was left in place." -ForegroundColor DarkGray
}

if ($failures.Count -eq 0) {
    Write-Host 'Every import scenario produced the expected exit code.' -ForegroundColor Green
    exit 0
}

Write-Warning "Some scenarios did not match: $($failures -join ' | ')"
exit 1
