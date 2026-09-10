#!/usr/bin/env pwsh
# CI Frontend E2E Failure Observability Gate: exposes each failed test's name,
# message and stack trace - sanitized, no secrets/PII, none of which this
# file ever handles - as a GitHub Actions error annotation (readable via the
# public, unauthenticated Checks API: no artifact download or repo sign-in
# required) plus a short table in the job's own Step Summary. Never changes
# the exit code the caller observed - this is diagnostics-only, run after the
# real test step regardless of its own outcome.
#
# Reservations Concurrency Forensics Gate: also scans for forensic diagnostic
# markers (e.g. ReservationsE2ETests' own failure-only Console.Error capture).
# A real CI run proved a per-test <Output><StdOut> lookup never finds
# anything: a disposable probe project confirmed VSTest/xUnit's trx logger
# writes EVERY test's Console.Out/Console.Error into ONE single run-level
# blob under /TestRun/ResultSummary/Output/StdOut - never per
# <UnitTestResult> - so nothing can be reliably attributed to one specific
# test. Instead of dumping that whole (potentially huge, multi-test) blob,
# this scans it for lines containing a known marker and surfaces only those,
# sanitized and bounded, as a run-level (not per-test) annotation.
param(
    [Parameter(Mandatory = $true)]
    [string]$TrxPath,
    [Parameter(Mandatory = $true)]
    [string]$JobLabel
)

$MarkerOutputMaxChars = 4000
$ForensicMarkers = @("RESERVATIONS CONCURRENCY VIOLATION DIAGNOSTIC")

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
        $joined = $joined.Substring(0, $MaxChars) + " [truncated]"
    }
    return $joined
}

if (-not (Test-Path $TrxPath)) {
    Write-Host "::warning::report-trx-failures.ps1: no .trx found at $TrxPath - nothing to report."
    exit 0
}

[xml]$trx = Get-Content -Raw $TrxPath
$ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
$ns.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")

$results = $trx.SelectNodes("//t:UnitTestResult", $ns)
$failed = @($results | Where-Object { $_.outcome -eq "Failed" })

$total = $results.Count
$passed = @($results | Where-Object { $_.outcome -eq "Passed" }).Count
$failedCount = $failed.Count

Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "## $JobLabel - Total=$total Passed=$passed Failed=$failedCount"

if ($failedCount -eq 0) {
    Write-Host "report-trx-failures.ps1: $JobLabel - no failed tests in $TrxPath."
    exit 0
}

Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "| Failed Test | Message |"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "|---|---|"

foreach ($result in $failed) {
    $testName = $result.testName
    $errorInfo = $result.SelectSingleNode(".//t:Output/t:ErrorInfo", $ns)
    if ($errorInfo) {
        $message = $errorInfo.SelectSingleNode("t:Message", $ns).InnerText
    } else {
        $message = "(no message captured)"
    }
    if ($errorInfo) {
        $stack = $errorInfo.SelectSingleNode("t:StackTrace", $ns).InnerText
    } else {
        $stack = ""
    }

    # ::error:: is a single-line annotation - collapse to one line and cap length so a large stack never floods the Checks UI.
    $flatMessage = ($message -replace "[\r\n]+", " ").Trim()
    if ($flatMessage.Length -gt 500) { $flatMessage = $flatMessage.Substring(0, 500) + "..." }

    Write-Host "::error title=$JobLabel failure - $testName::$flatMessage"
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "| ``$testName`` | $flatMessage |"

    if ($stack) {
        $flatStack = ($stack -replace "[\r\n]+", " | ").Trim()
        if ($flatStack.Length -gt 800) { $flatStack = $flatStack.Substring(0, 800) + "..." }
        Write-Host "::error title=$JobLabel stack - $testName::$flatStack"
    }
}

# Run-level forensic marker scan (see file header): every test's console
# output is merged into one blob, so this cannot be attributed to a specific
# failed test above - it is reported once for the whole job instead.
$runStdOutNode = $trx.SelectSingleNode("/t:TestRun/t:ResultSummary/t:Output/t:StdOut", $ns)
if ($runStdOutNode) {
    $runStdOutLines = $runStdOutNode.InnerText -split "`r`n|`n"
    foreach ($marker in $ForensicMarkers) {
        $markerLines = @($runStdOutLines | Where-Object { $_ -like "*$marker*" })
        if ($markerLines.Count -gt 0) {
            $sanitizedMarkerOutput = Get-SanitizedText -RawText ($markerLines -join "`n") -MaxChars $MarkerOutputMaxChars
            if ($sanitizedMarkerOutput) {
                Write-Host "::error title=$JobLabel forensic diagnostics ($marker)::$sanitizedMarkerOutput"
                Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ""
                Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "### Forensic diagnostics found in test output ($marker)"
                Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "``````"
                Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $sanitizedMarkerOutput
                Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "``````"
            }
        }
    }
}
