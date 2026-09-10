#!/usr/bin/env pwsh
# CI Frontend E2E Failure Observability Gate: exposes each failed test's name,
# message and stack trace - sanitized, no secrets/PII, none of which this
# file ever handles - as a GitHub Actions error annotation (readable via the
# public, unauthenticated Checks API: no artifact download or repo sign-in
# required) plus a short table in the job's own Step Summary. Never changes
# the exit code the caller observed - this is diagnostics-only, run after the
# real test step regardless of its own outcome.
param(
    [Parameter(Mandatory = $true)]
    [string]$TrxPath,

    [Parameter(Mandatory = $true)]
    [string]$JobLabel
)

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
