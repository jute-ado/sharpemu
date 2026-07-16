# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,

    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY,

    [string]$BaselinePath
)

$reports = @(
    Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter coverage.cobertura.xml |
        Group-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } |
        ForEach-Object { $_.Group[0] }
)
if ($reports.Count -eq 0) {
    throw "No Cobertura coverage reports were produced under '$ResultsDirectory'."
}

$lineCoverage = @{}
$branchCoverage = @{}
foreach ($report in $reports) {
    [xml]$coverage = Get-Content -LiteralPath $report.FullName
    foreach ($class in @($coverage.coverage.packages.package.classes.class)) {
        if ($null -eq $class -or [string]::IsNullOrWhiteSpace([string]$class.filename)) {
            continue
        }

        $file = [string]$class.filename
        foreach ($line in @($class.lines.line)) {
            $key = "$file`:$($line.number)"
            $covered = [long]$line.hits -gt 0
            if (!$lineCoverage.ContainsKey($key) -or $covered) {
                $lineCoverage[$key] = $covered
            }

            if ([string]$line.branch -ne 'True') {
                continue
            }

            $match = [regex]::Match([string]$line.'condition-coverage', '\((\d+)/(\d+)\)')
            if (!$match.Success) {
                continue
            }

            $coveredBranches = [long]$match.Groups[1].Value
            $validBranches = [long]$match.Groups[2].Value
            if (!$branchCoverage.ContainsKey($key)) {
                $branchCoverage[$key] = @($coveredBranches, $validBranches)
                continue
            }

            $existing = $branchCoverage[$key]
            $branchCoverage[$key] = @(
                [Math]::Max([long]$existing[0], $coveredBranches),
                [Math]::Max([long]$existing[1], $validBranches))
        }
    }
}

$linesValid = $lineCoverage.Count
$linesCovered = @($lineCoverage.Values | Where-Object { $_ }).Count
$branchesCovered = 0L
$branchesValid = 0L
foreach ($branch in $branchCoverage.Values) {
    $branchesCovered += [long]$branch[0]
    $branchesValid += [long]$branch[1]
}

$lineRate = if ($linesValid -eq 0) { 0 } else { 100 * $linesCovered / $linesValid }
$branchRate = if ($branchesValid -eq 0) { 0 } else { 100 * $branchesCovered / $branchesValid }
$minimumLineRate = 0.0
$minimumBranchRate = 0.0
if (![string]::IsNullOrWhiteSpace($BaselinePath)) {
    if (!(Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
        throw "Coverage baseline '$BaselinePath' does not exist."
    }

    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    if ($null -eq $baseline.linePercent -or $null -eq $baseline.branchPercent) {
        throw "Coverage baseline must define linePercent and branchPercent."
    }

    $minimumLineRate = [double]$baseline.linePercent
    $minimumBranchRate = [double]$baseline.branchPercent
    if ($minimumLineRate -lt 0 -or $minimumLineRate -gt 100 -or
        $minimumBranchRate -lt 0 -or $minimumBranchRate -gt 100) {
        throw "Coverage baseline percentages must be between 0 and 100."
    }
}

$lineRateText = $lineRate.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
$branchRateText = $branchRate.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
$minimumLineRateText = $minimumLineRate.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
$minimumBranchRateText = $minimumBranchRate.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
$summary = @(
    "## Test coverage baseline"
    ""
    "| Metric | Covered | Total | Rate | Required |"
    "| --- | ---: | ---: | ---: | ---: |"
    "| Lines | $linesCovered | $linesValid | $lineRateText% | $minimumLineRateText% |"
    "| Branches | $branchesCovered | $branchesValid | $branchRateText% | $minimumBranchRateText% |"
    ""
    "Merged reports: $($reports.Count)"
) -join "`n"

Write-Output $summary
if (![string]::IsNullOrWhiteSpace($SummaryPath)) {
    Add-Content -LiteralPath $SummaryPath -Value $summary
}

$failures = @()
if ($lineRate -lt $minimumLineRate) {
    $failures += "line coverage $lineRateText% is below $minimumLineRateText%"
}

if ($branchRate -lt $minimumBranchRate) {
    $failures += "branch coverage $branchRateText% is below $minimumBranchRateText%"
}

if ($failures.Count -gt 0) {
    throw "Coverage baseline failed: $($failures -join '; ')."
}
