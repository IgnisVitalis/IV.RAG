#!/usr/bin/env pwsh
param(
    [string]$Project,
    [string]$Version,
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Debug",
    [switch]$BumpMinor
)

$ErrorActionPreference = "Stop"

$RepoRoot   = Resolve-Path "$PSScriptRoot/.."
$OutputPath = Join-Path $env:USERPROFILE ".nuget\local-feed"
if (-not (Test-Path $OutputPath)) { New-Item -ItemType Directory -Path $OutputPath | Out-Null }

$SrcProjects = @(
    'src/IV.RAG.Abstractions'
    'src/IV.RAG.Core'
    'src/IV.RAG.Ingestion'
    'src/IV.RAG.Ollama'
    'src/IV.RAG.Postgres'
    'src/IV.RAG.Remote.Contracts'
    'src/IV.RAG.Remote.Http'
)

function Get-NextVersion {
    param([string]$Folder, [string]$Mode)

    $refPackage = 'IV.RAG.Abstractions'

    $rx = [regex]("^" + [regex]::Escape($refPackage) + "\.(\d+)\.(\d+)\.(\d+)\.nupkg$")
    $items = Get-ChildItem $Folder -Filter "$refPackage.*.nupkg" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike "*.symbols.nupkg" } |
        ForEach-Object {
            $m = $rx.Match($_.Name)
            if ($m.Success) {
                [pscustomobject]@{
                    Major = [int]$m.Groups[1].Value
                    Minor = [int]$m.Groups[2].Value
                    Patch = [int]$m.Groups[3].Value
                }
            }
        }

    if (-not $items) { return "0.1.0" }

    $v = $items | Sort-Object Major, Minor, Patch -Descending | Select-Object -First 1
    if ($Mode -eq "minor") {
        return "$($v.Major).$($v.Minor + 1).0"
    }
    return "$($v.Major).$($v.Minor).$($v.Patch + 1)"
}

if (-not $Version) {
    $mode    = if ($BumpMinor) { "minor" } else { "patch" }
    $Version = Get-NextVersion $OutputPath $mode
}

$projectsTopack = if ($Project) {
    @((Resolve-Path (Join-Path $RepoRoot $Project)).Path)
} else {
    $SrcProjects | ForEach-Object { (Resolve-Path (Join-Path $RepoRoot $_)).Path }
}

foreach ($proj in $projectsTopack) {
    $projName = Split-Path $proj -Leaf
    $pkg = Join-Path $OutputPath "$projName.$Version.nupkg"
    if (Test-Path $pkg) { throw "Package already exists: $pkg" }

    dotnet pack `
        $proj `
        -c $Configuration `
        --no-restore `
        -o $OutputPath `
        -p:Version=$Version `
        -p:TreatWarningsAsErrors=true
}

Write-Host "Packed $($projectsTopack.Count) package(s), version $Version -> $OutputPath"
