#!/usr/bin/env pwsh
# CI Frontend E2E Failure Observability Gate: exposes each failed test's name,
# message and stack trace - sanitized, no secrets/PII, none of which this
# file ever handles - as a GitHub Actions error annotation (readable via the
# public, unauthenticated Checks API: no artifact download or repo sign-in
# required) plus a short table in the job's own Step Summary. Never changes
# the exit code the caller observed - this is diagnostics-only, run after the
# real test step regardless of its own outcome.
#
# Reservations Concurrency Forensics Gate: also reads each failed test's
# <Output><StdOut> - the only place a test's own Console.Error/Console.Out
# writes land in a .trx (confirmed against a real local .trx: this content is
# never inside <ErrorInfo>, which holds only the assertion exception's own
# Message/StackTrace) - since ReservationsE2ETests' own failure-only
# diagnostic capture writes there and was otherwise invisible via this same
# public API. Sanitized independently of ErrorInfo (arbitrary test/app log
# lines are far more likely to accidentally contain a token or connection
# string than a FluentAssertions message ever is) and bounded so one noisy
# test can never flood the Checks UI.
param(
    [Parameter(Mandatory = $true)]
    [string]$TrxPath,
    [Parameter(Mandatory = $true)]
    [string]$JobLabel
)

$StdOutMaxChars = 4000
$ForensicMarker = "RESERVATIONS CONCURRENCY VIOLATION DIAGNOSTIC"

function Get-SanitizedStdOut {
    param(
        [string]$RawStdOut,
        [int]$MaxChars,
        [string]$PriorityMarker
    )

    if ([string]::IsNullOrWhiteSpace($RawStdOut)) {
        return ""
    }

    # Private key blocks span multiple lines - strip on the raw text first,
    # before any line-splitting could separate BEGIN from END.
    $text = $RawStdOut -replace '(?s)-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----', '[REDACTED_PRIVATE_KEY]'

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

    # Forensic priority: if a diagnostic marker line exists, surface it
    # first - a long, unrelated stdout must never push it out of the bound.
    $priorityLines = @($sanitized | Where-Object { $_ -like "*$PriorityMarker*" })
    $otherLines = @($sanitized | Where-Object { $_ -notlike "*$PriorityMarker*" })
    $ordered = $priorityLines + $otherLines

    $joined = ($ordered -join ' | ')
    if ($joined.Length -gt $MaxChars) {
        $joined = $joined.Substring(0, $MaxChars) + " [stdout truncated]"
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

Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "| Failed Test | Message | StdOut/Diagnostics |"
Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "|---|---|---|"

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

    if ($stack) {
        $flatStack = ($stack -replace "[\r\n]+", " | ").Trim()
        if ($flatStack.Length -gt 800) { $flatStack = $flatStack.Substring(0, 800) + "..." }
        Write-Host "::error title=$JobLabel stack - $testName::$flatStack"
    }

    $stdOutNode = $result.SelectSingleNode(".//t:Output/t:StdOut", $ns)
    $sanitizedStdOut = ""
    if ($stdOutNode) {
        $sanitizedStdOut = Get-SanitizedStdOut -RawStdOut $stdOutNode.InnerText -MaxChars $StdOutMaxChars -PriorityMarker $ForensicMarker
        if ($sanitizedStdOut) {
            Write-Host "::error title=$JobLabel stdout - $testName::$sanitizedStdOut"
        }
    }

    $summaryStdOut = if ($sanitizedStdOut) { $sanitizedStdOut } else { "(none)" }
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "| ``$testName`` | $flatMessage | $summaryStdOut |"
}
