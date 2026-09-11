#!/usr/bin/env pwsh
# Matrix Process-Level Failure Observability Gate: complements report-trx-failures.ps1
# for failures that leave zero <UnitTestResult outcome="Failed"> in the TRX at all -
# e.g. --blame-hang killing the testhost after its own timeout, or the job's own
# timeout-minutes cancelling it before dotnet test ever finishes writing a TRX. Scans
# this step's own console capture (a "tee" of the test step's stdout/stderr - see
# ci.yml) for known process-level failure signatures and surfaces a sanitized,
# bounded CONTEXT WINDOW around each match (not just the matching line itself) as a
# public annotation - a bare matching line alone (e.g. one "at Npgsql.Pooling
# DataSource.Get(...)" stack frame) is meaningless without the exception header/
# message that precedes it in the real console output. Never infers beyond what
# these flags describe. Never changes the exit code the caller observed -
# diagnostics-only.
#
# Cross-Job Test Infrastructure Hang Forensics Gate: a real CI run's Housekeeping
# job matched the previous version of this script on the bare word "timeout" -
# which turned out to be the "timeout" PARAMETER NAME in Npgsql's own
# "PoolingDataSource.Get(NpgsqlConnection conn, NpgsqlTimeout timeout, ...)" method
# signature, not an actual timeout message. BlameHangEvidenceFound is therefore its
# own strict pattern set (explicit blame/hang collector vocabulary only) - never the
# generic "timeout" family, which stays in $ProcessPatterns as a soft/broad signal.
param(
    [Parameter(Mandatory = $true)]
    [string]$ConsoleLogPath,
    [Parameter(Mandatory = $true)]
    [string]$JobLabel
)

$AnnotationMaxChars = 3800
$SummaryMaxChars = 12000
$LinesBefore = 3
$LinesAfter = 5

# Soft/broad signal - drives ProcessFailureEvidenceFound only, never BlameHangEvidenceFound.
$ProcessPatterns = @(
    "blame", "hang", "testhost", "dump", "crash", "aborted", "terminated",
    "timeout", "timed out", "process exited", "host process", "sequence.xml",
    "hang dump"
)
# Strict/explicit signal - real VSTest/xUnit blame-hang collector vocabulary only.
$BlameHangPatterns = @(
    "blame-hang", "hang dump", "hang timeout collector", "Dump file was successfully created",
    "testhost process did not exit", "test host process hung", "Aborting test run", "Test Run Aborted"
)
$DumpPatterns = @("dump")
$NpgsqlPoolWaitPattern = "Npgsql.PoolingDataSource.Get"
$WolverineExecutorPattern = "Wolverine.Runtime.Handlers.Executor"

function Get-SanitizedText {
    param(
        [string]$RawText,
        [int]$MaxChars
    )

    if ([string]::IsNullOrWhiteSpace($RawText)) {
        return ""
    }

    # Private key blocks span multiple lines - strip on the raw text first,
    # before any line-splitting could separate BEGIN from END.
    $text = $RawText -replace '(?s)-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----', '[REDACTED_PRIVATE_KEY]'

    $lines = $text -split "`r`n|`n" | Where-Object { $_.Trim().Length -gt 0 }

    $sanitized = foreach ($line in $lines) {
        $l = $line
        $l = $l -replace '(?i)(authorization\s*[:=]\s*)\S+', '$1[REDACTED]'
        $l = $l -replace '(?i)(bearer\s+)\S+', '$1[REDACTED]'
        $l = $l -replace 'eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+', '[REDACTED_JWT]'
        $l = $l -replace 'AKIA[0-9A-Z]{16}', '[REDACTED_AWS_KEY]'
        $l = $l -replace '(?i)(aws[_-]?secret[_-]?access[_-]?key\s*[:=]\s*)\S+', '$1[REDACTED]'
        $l = $l -replace '(?i)(aws[_-]?session[_-]?token\s*[:=]\s*)\S+', '$1[REDACTED]'
        $l = $l -replace 'ghp_[A-Za-z0-9]{20,}', '[REDACTED_GH_TOKEN]'
        $l = $l -replace 'gho_[A-Za-z0-9]{20,}', '[REDACTED_GH_TOKEN]'
        $l = $l -replace 'ghs_[A-Za-z0-9]{20,}', '[REDACTED_GH_TOKEN]'
        $l = $l -replace 'github_pat_[A-Za-z0-9_]{20,}', '[REDACTED_GH_TOKEN]'
        $l = $l -replace '(?i)(x-api-key\s*[:=]\s*)\S+', '$1[REDACTED]'
        $l = $l -replace '(?i)(api[_-]?key\s*[:=]\s*)\S+', '$1[REDACTED]'
        $l = $l -replace '(?i)(password\s*[:=]\s*)\S+', '$1[REDACTED]'
        $l = $l -replace '(?i)(pwd\s*[:=]\s*)\S+', '$1[REDACTED]'
        $l = $l -replace '(?i)(secret\s*[:=]\s*)\S+', '$1[REDACTED]'
        $l = $l -replace '(?i)(connectionstring\s*[:=]\s*).+', '$1[REDACTED]'
        $l
    }
    $sanitized = @($sanitized)

    $joined = ($sanitized -join ' | ')
    if ($joined.Length -gt $MaxChars) {
        $joined = $joined.Substring(0, $MaxChars) + " [process diagnostics truncated]"
    }
    return $joined
}

if (-not (Test-Path $ConsoleLogPath)) {
    Write-Host "::warning::report-test-process-failure.ps1: no console log found at $ConsoleLogPath - nothing to report."
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "## $JobLabel - process-level diagnostics: no console log captured"
    exit 0
}

$rawText = Get-Content -Raw $ConsoleLogPath -ErrorAction SilentlyContinue
if ([string]::IsNullOrEmpty($rawText)) {
    Write-Host "report-test-process-failure.ps1: $JobLabel - console log is empty, nothing to report."
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "## $JobLabel - process-level diagnostics: console log was empty"
    exit 0
}

$allLines = @($rawText -split "`r`n|`n")

$processRegex = ($ProcessPatterns | ForEach-Object { '\b' + [regex]::Escape($_) + '\b' }) -join '|'
$blameHangRegex = ($BlameHangPatterns | ForEach-Object { [regex]::Escape($_) }) -join '|'
$dumpRegex = ($DumpPatterns | ForEach-Object { '\b' + [regex]::Escape($_) + '\b' }) -join '|'
$npgsqlRegex = [regex]::Escape($NpgsqlPoolWaitPattern)
$wolverineRegex = [regex]::Escape($WolverineExecutorPattern)

$matchIndices = [System.Collections.Generic.SortedSet[int]]::new()
for ($i = 0; $i -lt $allLines.Count; $i++) {
    if ($allLines[$i] -match $processRegex -or $allLines[$i] -match $blameHangRegex `
            -or $allLines[$i] -match $dumpRegex -or $allLines[$i] -match $npgsqlRegex `
            -or $allLines[$i] -match $wolverineRegex) {
        [void]$matchIndices.Add($i)
    }
}

$processFailureEvidenceFound = ($allLines | Where-Object { $_ -match $processRegex }).Count -gt 0
$blameHangEvidenceFound = ($allLines | Where-Object { $_ -match $blameHangRegex }).Count -gt 0
$dumpEvidenceFound = ($allLines | Where-Object { $_ -match $dumpRegex }).Count -gt 0
$npgsqlPoolWaitPathObserved = ($allLines | Where-Object { $_ -match $npgsqlRegex }).Count -gt 0
$wolverineExecutorPathObserved = ($allLines | Where-Object { $_ -match $wolverineRegex }).Count -gt 0

Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "## $JobLabel - process-level diagnostics"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "MatrixContext=$JobLabel"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "ProcessFailureEvidenceFound=$processFailureEvidenceFound"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "BlameHangEvidenceFound=$blameHangEvidenceFound"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "DumpEvidenceFound=$dumpEvidenceFound"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "NpgsqlPoolWaitPathObserved=$npgsqlPoolWaitPathObserved"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "WolverineExecutorPathObserved=$wolverineExecutorPathObserved"

if ($matchIndices.Count -eq 0) {
    Write-Host "report-test-process-failure.ps1: $JobLabel - no process-level diagnostic patterns found in console log."
    exit 0
}

# Build context windows (LinesBefore/LinesAfter around each match) and merge overlapping
# ranges, so the exception header/message immediately preceding a matched stack frame -
# or the collector output immediately following it - is preserved instead of just the
# bare matching line.
$ranges = [System.Collections.Generic.List[int[]]]::new()
foreach ($idx in $matchIndices) {
    $start = [Math]::Max(0, $idx - $LinesBefore)
    $end = [Math]::Min($allLines.Count - 1, $idx + $LinesAfter)
    $ranges.Add(@($start, $end))
}

$mergedRanges = [System.Collections.Generic.List[int[]]]::new()
foreach ($range in $ranges) {
    if ($mergedRanges.Count -gt 0 -and $range[0] -le ($mergedRanges[$mergedRanges.Count - 1][1] + 1)) {
        $mergedRanges[$mergedRanges.Count - 1][1] = [Math]::Max($mergedRanges[$mergedRanges.Count - 1][1], $range[1])
    }
    else {
        $mergedRanges.Add($range)
    }
}

$contextBlocks = foreach ($range in $mergedRanges) {
    ($allLines[$range[0]..$range[1]] -join "`n")
}
$fullContextText = ($contextBlocks -join "`n---`n")

$sanitizedForAnnotation = Get-SanitizedText -RawText $fullContextText -MaxChars $AnnotationMaxChars
$sanitizedForSummary = Get-SanitizedText -RawText $fullContextText -MaxChars $SummaryMaxChars

Write-Host "::error title=$JobLabel process-level test diagnostics::$sanitizedForAnnotation"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ""
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "### Process-level diagnostic context (LastRelevantLines)"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value '```'
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $sanitizedForSummary
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value '```'
