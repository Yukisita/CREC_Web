$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('crec-update-tests-' + [Guid]::NewGuid().ToString('N'))
try {
    $package = Join-Path $fixture 'package'
    $installation = Join-Path $fixture 'installation'
    foreach ($root in @($package, $installation)) {
        [System.IO.Directory]::CreateDirectory((Join-Path $root 'Projects')) | Out-Null
        [System.IO.Directory]::CreateDirectory((Join-Path $root 'web/Projects')) | Out-Null
    }
    foreach ($relativePath in @('Projects/user.crec', 'web/Projects/user.crec')) {
        Set-Content -LiteralPath (Join-Path $package $relativePath) -Value 'package must not overwrite this'
        Set-Content -LiteralPath (Join-Path $installation $relativePath) -Value 'user data'
    }
    Set-Content -LiteralPath (Join-Path $package 'app.txt') -Value 'updated app'
    Set-Content -LiteralPath (Join-Path $installation 'app.txt') -Value 'old app'
    Set-Content -LiteralPath (Join-Path $installation 'retained.txt') -Value 'retained'
    & (Join-Path $PSScriptRoot '../scripts/Update-Crec.ps1') -SourcePackage $package -InstallDirectory $installation
    foreach ($relativePath in @('Projects/user.crec', 'web/Projects/user.crec')) {
        if ((Get-Content -LiteralPath (Join-Path $installation $relativePath)) -ne 'user data') { throw "User data overwritten: $relativePath" }
    }
    if ((Get-Content -LiteralPath (Join-Path $installation 'app.txt')) -ne 'updated app') { throw 'Application file was not updated' }
    if (!(Test-Path -LiteralPath (Join-Path $installation 'retained.txt'))) { throw 'Existing file deleted' }
    Write-Output 'PASS: application updated; standalone and desktop Projects data preserved'
}
finally {
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixture)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (!$resolvedFixture.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolvedFixture -Leaf).StartsWith('crec-update-tests-')) {
        throw 'Unexpected cleanup directory'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
