[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $SourcePackage,
    [Parameter(Mandatory)][string] $InstallDirectory
)

$ErrorActionPreference = 'Stop'
$packageRoot = [System.IO.Path]::GetFullPath($SourcePackage)
$installRoot = [System.IO.Path]::GetFullPath($InstallDirectory)
$comparison = [System.StringComparison]::OrdinalIgnoreCase

function Test-Within([string] $Path, [string] $Root) {
    return $Path.Equals($Root, $comparison) -or $Path.StartsWith($Root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Assert-NoLinks([string] $Path) {
    for ($currentPath = $Path; $currentPath; $currentPath = [System.IO.Path]::GetDirectoryName($currentPath)) {
        if (Test-Path -LiteralPath $currentPath) {
            if ((Get-Item -LiteralPath $currentPath -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Links and junctions are not supported: $currentPath"
            }
        }
    }
}

if (!(Test-Path -LiteralPath $packageRoot -PathType Container)) { throw 'SourcePackage must be an extracted application package directory.' }
if ((Test-Within $packageRoot $installRoot) -or (Test-Within $installRoot $packageRoot)) { throw 'Package and installation directories must not overlap.' }
Assert-NoLinks $packageRoot
Assert-NoLinks $installRoot

# Prepare the complete copy list before writing. Projects is user data at every depth.
$files = [System.Collections.Generic.List[object]]::new()
$directories = [System.Collections.Generic.Stack[string]]::new()
$directories.Push($packageRoot)
while ($directories.Count -gt 0) {
    foreach ($entry in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
        if ($entry.Name.Equals('Projects', $comparison)) { continue }
        if ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "Package contains a link: $($entry.FullName)" }
        if ($entry.PSIsContainer) { $directories.Push($entry.FullName); continue }
        $relativePath = [System.IO.Path]::GetRelativePath($packageRoot, $entry.FullName)
        $targetPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($installRoot, $relativePath))
        if (!(Test-Within $targetPath $installRoot)) { throw 'Package path is outside the installation directory.' }
        Assert-NoLinks $targetPath
        $files.Add(@{ Source = $entry.FullName; Target = $targetPath })
    }
}

foreach ($file in $files) {
    if ($PSCmdlet.ShouldProcess($file.Target, 'Copy application file')) {
        Assert-NoLinks $file.Target
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($file.Target)) | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $file.Target -Force
    }
}
# No recursive deletion: existing Projects and files absent from the package remain in place.
