<#
.SYNOPSIS
Writes the run manifest for a load comparison run.

.DESCRIPTION
Every field is named explicitly. Nothing here dumps $env:, the API configuration or a
connection string into the artifact: a manifest is published evidence, and a lab secret
must not be able to reach it by being added to a config section later.

Dot-source this file and call Write-LoadRunManifest.
#>

Set-StrictMode -Version Latest

function Get-ToolVersion {
    param([Parameter(Mandatory)][string]$Command, [string[]]$Arguments = @('--version'))

    try {
        $output = & $Command @Arguments 2>&1 | Select-Object -First 1
        if ($LASTEXITCODE -ne 0) { return 'unavailable' }
        return ($output | Out-String).Trim()
    }
    catch {
        return 'unavailable'
    }
}

# Asks the daemon what the lab containers are actually running, and under which limits. When
# Docker cannot be reached the list comes back empty rather than invented: a reader then sees the
# infrastructure could not be described, which is the truth about that run.
function Get-ContainerInfo {
    $containers = @()
    $prefix = if ($env:DPL_CONTAINER_PREFIX) { $env:DPL_CONTAINER_PREFIX } else { 'dpl' }

    foreach ($entry in @(
        @{ service = 'sql';   name = "$prefix-sql" }
        @{ service = 'redis'; name = "$prefix-redis" })) {

        $format = '{{.Config.Image}}|{{.Image}}|{{.HostConfig.NanoCpus}}|{{.HostConfig.Memory}}'
        try {
            $raw = & docker inspect $entry.name --format $format 2>$null
            if ($LASTEXITCODE -ne 0 -or -not $raw) { continue }
        }
        catch {
            continue
        }

        $parts = ([string]$raw).Trim() -split '\|'
        if ($parts.Count -lt 4) { continue }

        $nanoCpus = [long]$parts[2]
        $memory = [long]$parts[3]

        $containers += [ordered]@{
            service     = $entry.service
            image       = $parts[0]
            imageDigest = $parts[1]
            # A 0 from the daemon means "no limit set", not "zero CPUs"; say which.
            cpuLimit    = if ($nanoCpus -eq 0) { 'unlimited (all host CPUs)' }
                          else { '{0:0.##} CPU' -f ($nanoCpus / 1e9) }
            memoryLimit = if ($memory -eq 0) { 'unlimited (all host memory)' }
                          else { "$memory bytes" }
        }
    }

    return @($containers)
}

# Assembly versions of the data-path packages, read off the built API output. These are the
# binaries the measured process loaded, so a restore that drifted from the lock file cannot hide.
function Get-DataPathPackageVersions {
    param([Parameter(Mandatory)][string]$RepoRoot)

    $outputDirectory = Join-Path $RepoRoot 'src/Lab.Api/bin/Release/net10.0'
    $versions = [ordered]@{}

    foreach ($assembly in @(
        'Microsoft.EntityFrameworkCore'
        'Microsoft.EntityFrameworkCore.SqlServer'
        'Microsoft.Data.SqlClient'
        'StackExchange.Redis'
        'CsvHelper')) {

        $path = Join-Path $outputDirectory "$assembly.dll"
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $versions[$assembly] = (Get-Item -LiteralPath $path).VersionInfo.ProductVersion
        }
        else {
            $versions[$assembly] = 'unavailable'
        }
    }

    return $versions
}

# Total physical memory, so the manifest records the machine's RAM alongside its CPU count.
function Get-HostMemoryBytes {
    try {
        if ($IsWindows -or $env:OS -eq 'Windows_NT') {
            return [long](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory
        }

        $line = Select-String -Path '/proc/meminfo' -Pattern '^MemTotal:\s+(\d+) kB' -ErrorAction Stop
        return [long]$line.Matches[0].Groups[1].Value * 1024
    }
    catch {
        return $null
    }
}

# SHA-256 and size for a file the run produced. A file that is not on disk is reported as missing
# rather than given a fabricated hash — ADR 001 allows "kept locally", never "made up".
function Get-StoredFileInfo {
    param(
        [Parameter(Mandatory)][string]$AbsolutePath,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Storage,
        [Parameter(Mandatory)][string]$ReproduceCommand)

    if (-not (Test-Path -LiteralPath $AbsolutePath -PathType Leaf)) {
        return [ordered]@{
            path             = $RelativePath
            storage          = 'missing'
            sizeBytes        = 0
            sha256           = 'unavailable'
            reproduceCommand = $ReproduceCommand
        }
    }

    $item = Get-Item -LiteralPath $AbsolutePath
    return [ordered]@{
        path             = $RelativePath
        storage          = $Storage
        sizeBytes        = $item.Length
        sha256           = (Get-FileHash -LiteralPath $AbsolutePath -Algorithm SHA256).Hash.ToLowerInvariant()
        reproduceCommand = $ReproduceCommand
    }
}

function Get-GitInfo {
    param([Parameter(Mandatory)][string]$RepoRoot)

    $sha = (& git -C $RepoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $sha) { $sha = 'unknown' }
    # The results directory is excluded: a run writes its own artifacts there, so counting
    # it would make every published run report a dirty tree and the flag would mean nothing.
    $status = (& git -C $RepoRoot status --porcelain -- ':!results' 2>$null)

    # Otherwise a dirty tree is reported, never hidden: ADR 001 requires either
    # re-measuring from a clean commit or keeping the diff alongside the numbers.
    return [ordered]@{
        commitSha = $sha.Trim()
        isDirty   = [bool]($status -and $status.Trim())
    }
}

function Write-LoadRunManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][datetime]$StartedAtUtc,
        [Parameter(Mandatory)][hashtable]$Parameters,
        [Parameter(Mandatory)][string[]]$ResultFiles,
        [Parameter(Mandatory)][string]$Command,
        # The fixture arrives from ConvertFrom-Json, so it is a PSCustomObject, not a hashtable.
        [psobject]$Fixture,
        [string]$FailureReason,
        # Relative paths whose bytes stay on the measuring machine. Each gets a hash, a size and
        # the command that makes it again, so "kept locally" stays a verifiable claim.
        [string[]]$LocalOnlyFiles = @(),
        # The cache settings the measured API process reported about itself.
        [psobject]$CacheConfiguration
    )

    $manifest = [ordered]@{
        runId          = $RunId
        experiment     = 'cache-load-comparison'
        status         = $Status
        startedAtUtc   = $StartedAtUtc.ToString('o')
        completedAtUtc = [datetime]::UtcNow.ToString('o')
        database       = 'DataPerformanceLab'
        git            = Get-GitInfo -RepoRoot $RepoRoot
        environment    = [ordered]@{
            operatingSystem    = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            architecture       = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            processorCount     = [Environment]::ProcessorCount
            totalMemoryBytes   = Get-HostMemoryBytes
            powerShell         = $PSVersionTable.PSVersion.ToString()
            dotnetSdk          = Get-ToolVersion -Command 'dotnet'
            dotnetRuntime      = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
            k6                 = Get-ToolVersion -Command 'k6' -Arguments @('version')
            docker             = Get-ToolVersion -Command 'docker' -Arguments @('--version')
            # The packages that actually served the measurement, read from the built API rather
            # than from a lock file that only says what should have been restored.
            packageVersions    = Get-DataPathPackageVersions -RepoRoot $RepoRoot
            containers         = Get-ContainerInfo
        }
        parameters     = [ordered]@{}
        resultFiles    = @($ResultFiles)
        command        = $Command
    }

    if ($CacheConfiguration) {
        $manifest.cache = [ordered]@{
            enabled                       = $CacheConfiguration.enabled
            ttlSeconds                    = $CacheConfiguration.ttlSeconds
            instanceId                    = $CacheConfiguration.instanceId
            connectTimeoutMilliseconds    = $CacheConfiguration.connectTimeoutMilliseconds
            operationTimeoutMilliseconds  = $CacheConfiguration.operationTimeoutMilliseconds
            note = 'Reported by the measured API process via /internal/cache-metrics. ' +
                   'cache-off runs still record these settings; they simply went unused.'
        }
    }

    foreach ($key in ($Parameters.Keys | Sort-Object)) {
        $manifest.parameters[$key] = [string]$Parameters[$key]
    }

    if ($Fixture) {
        $manifest.dataset = [ordered]@{
            seed             = $Fixture.dataset.seed
            generatorVersion = $Fixture.dataset.generatorVersion
            profile          = $Fixture.dataset.profile
            productCount     = $Fixture.dataset.productCount
            dataHash         = $Fixture.dataset.dataHash
            fixtureHash      = $Fixture.fixtureHash
            fixtureSkuCount  = $Fixture.count
            fixtureStride    = $Fixture.stride
        }

        # The real distribution the experiment faced, carried through from the seed manifest.
        # The profile name alone does not say how skewed the catalog is.
        if ($Fixture.dataset.PSObject.Properties.Name -contains 'activeCount') {
            $manifest.dataset.activeCount = $Fixture.dataset.activeCount
            $manifest.dataset.inactiveCount = $Fixture.dataset.inactiveCount
            $manifest.dataset.categoryCounts = $Fixture.dataset.categoryCounts
        }
        else {
            $manifest.dataset.distributionNote =
                'This fixture was written before the seed distribution was carried into it; ' +
                'category and active/inactive counts are unavailable for this run. ' +
                'Re-running `dpl fixture` records them.'
        }
    }

    # Published artifacts get a hash and a size so the bytes are verifiable, not merely listed.
    # Large raw streams stay local and are recorded the same way plus the command to remake them.
    $storedFiles = @()
    foreach ($relative in $ResultFiles) {
        $storedFiles += Get-StoredFileInfo `
            -AbsolutePath (Join-Path $OutputDirectory $relative) `
            -RelativePath $relative -Storage 'published' -ReproduceCommand $Command
    }
    foreach ($relative in $LocalOnlyFiles) {
        $storedFiles += Get-StoredFileInfo `
            -AbsolutePath (Join-Path $OutputDirectory $relative) `
            -RelativePath $relative -Storage 'local-only' -ReproduceCommand $Command
    }
    $manifest.storedFiles = @($storedFiles)

    if ($FailureReason) {
        $manifest.failureReason = $FailureReason
    }

    $path = Join-Path $OutputDirectory 'manifest.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $path -Encoding utf8
    return $path
}
