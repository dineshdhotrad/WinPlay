# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
    Builds and tests every WinPlay project, exactly as CI does.

.DESCRIPTION
    One canonical definition of "the whole product", used by both developers and CI.

    This exists because the two used to be defined separately: CI listed the projects in its
    workflow, while a developer building the project they were editing would not build its
    consumers. The DiscoveryCli tool consumes internal Core APIs, so changing a Core signature
    compiled and tested green locally and only failed later, in CI, in a project the author had
    not touched. Anything that is only checked somewhere else is something you find out about
    later, from a robot, after you have moved on.

    The solution file is deliberately not used: .slnx support varies by SDK version, and a
    restore failing for that reason is a confusing way to learn about it.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER SkipTests
    Build only. Useful when iterating on a compile error.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Configuration Debug -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Dependency order, so a break surfaces in the project that caused it rather than in a consumer.
# WinPlay.App is last and needs an explicit platform: it is a WinUI app with a RID-specific build.
$projects = @(
    @{ Path = 'src/WinPlay.Core/WinPlay.Core.csproj' }
    @{ Path = 'src/WinPlay.Capture/WinPlay.Capture.csproj' }
    @{ Path = 'src/WinPlay.Diagnostics/WinPlay.Diagnostics.csproj' }
    @{ Path = 'tools/WinPlay.DiscoveryCli/WinPlay.DiscoveryCli.csproj' }
    @{ Path = 'src/WinPlay.App/WinPlay.App.csproj'; Platform = 'x64' }
)

$testProjects = @(
    @{ Path = 'tests/WinPlay.Core.Tests/WinPlay.Core.Tests.csproj' }
    @{ Path = 'tests/WinPlay.Capture.Tests/WinPlay.Capture.Tests.csproj'; Platform = 'x64' }
    @{ Path = 'tests/WinPlay.Diagnostics.Tests/WinPlay.Diagnostics.Tests.csproj' }
)

function Invoke-Step {
    param([string]$Label, [string[]]$DotnetArgs)
    Write-Host "==> $Label" -ForegroundColor Cyan
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE" }
}

# -warnaserror everywhere: WinPlay holds a zero-warning bar, and a bar nothing enforces is a
# preference rather than a bar.
foreach ($p in $projects) {
    $a = @('build', $p.Path, '-c', $Configuration, '--nologo', '-warnaserror')
    if ($p.Platform) { $a += "-p:Platform=$($p.Platform)" }
    Invoke-Step "build $($p.Path)" $a
}

foreach ($p in $testProjects) {
    $a = @('build', $p.Path, '-c', $Configuration, '--nologo', '-warnaserror')
    if ($p.Platform) { $a += "-p:Platform=$($p.Platform)" }
    Invoke-Step "build $($p.Path)" $a
}

if (-not $SkipTests) {
    foreach ($p in $testProjects) {
        # A .trx per project: CI collects these as an artifact, so a failure can be read
        # afterwards instead of only in the log.
        $name = [System.IO.Path]::GetFileNameWithoutExtension($p.Path)
        $a = @('test', $p.Path, '-c', $Configuration, '--nologo', '--no-build',
               '--logger', "trx;LogFileName=$name.trx")
        if ($p.Platform) { $a += "-p:Platform=$($p.Platform)" }
        Invoke-Step "test $($p.Path)" $a
    }
}

Write-Host ''
Write-Host "All projects built with zero warnings$(if (-not $SkipTests) { ' and all tests passed' })." -ForegroundColor Green
