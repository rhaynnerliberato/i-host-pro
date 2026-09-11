#!/usr/bin/env pwsh
# Matrix Process-Level Failure Observability Gate: complements report-trx-failures.ps1
# for failures that leave zero <UnitTestResult outcome="Failed"> in the TRX at all -
# e.g. --blame-hang killing the testhost after its own timeout, or the job's own
# timeout-minutes cancelling it before dotnet test ever finishes writing a TRX. Scans
# this step's own console capture (a "tee" of the Integration Tests step's stdout/
# stderr, Reservations/Housekeeping only - see ci.yml) for known process-level failure
# signatures and surfaces only the matching lines - sanitized, bounded - as a public
# annotation. Never infers: reports exactly the lines found, nothing more. Never
# changes the exit code the caller observed - diagnostics-only.
param(
    [Parameter(Mandatory = $true)]
    [string]$ConsoleLogPath,
    [Parameter(Mandatory = $true)]
    [string]$JobLabel
)

$MaxChars = 6000
# Whole-word matches only (\b...\b): the resource telemetry this job already emits for
# every Reservations/Housekeeping run (success or failure) is tagged
# "MATRIX_TIMEOUT_DIAG" - a bare substring match on "timeout" would fire on that tag
# on every single invocation, defeating the point of this script. Underscore is a word
# character in .NET regex, so \btimeout\b does not match inside MATRIX_TIMEOUT_DIAG.
$ProcessPatterns = @(
    "blame", "hang", "testhost", "dump", "crash", "aborted", "terminated",
    "timeout", "timed out", "process exited", "host process", "sequence.xml",
    "hang dump"
)
$BlameHangPatterns = @("blame", "hang", "testhost")
$DumpPatterns = @("dump")

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

$allLines = $rawText -split "`r`n|`n"

$processRegex = ($ProcessPatterns | ForEach-Object { '\b' + [regex]::Escape($_) + '\b' }) -join '|'
$blameHangRegex = ($BlameHangPatterns | ForEach-Object { '\b' + [regex]::Escape($_) + '\b' }) -join '|'
$dumpRegex = ($DumpPatterns | ForEach-Object { '\b' + [regex]::Escape($_) + '\b' }) -join '|'

$matchedLines = @($allLines | Where-Object { $_ -match $processRegex })
$processFailureEvidenceFound = $matchedLines.Count -gt 0
$blameHangEvidenceFound = ($allLines | Where-Object { $_ -match $blameHangRegex }).Count -gt 0
$dumpEvidenceFound = ($allLines | Where-Object { $_ -match $dumpRegex }).Count -gt 0

Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "## $JobLabel - process-level diagnostics"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "MatrixContext=$JobLabel"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "ProcessFailureEvidenceFound=$processFailureEvidenceFound"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "BlameHangEvidenceFound=$blameHangEvidenceFound"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "DumpEvidenceFound=$dumpEvidenceFound"

if (-not $processFailureEvidenceFound) {
    Write-Host "report-test-process-failure.ps1: $JobLabel - no process-level diagnostic patterns found in console log."
    exit 0
}

$sanitizedMatchedLines = Get-SanitizedText -RawText ($matchedLines -join "`n") -MaxChars $MaxChars

Write-Host "::error title=$JobLabel process-level test diagnostics::$sanitizedMatchedLines"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ""
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "### Process-level diagnostic lines found (LastRelevantLines)"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value '```'
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $sanitizedMatchedLines
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value '```'
