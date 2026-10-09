<#
.SYNOPSIS
Prepares a release: version bump, release notes, build, tests, NuGet package and a check that the
package holds nothing personal. It never pushes anything; it prints the commands that publish.

.EXAMPLE
tools/release.ps1 -Notes "SSIS Settings layout.`n- Template and New project sections."
tools/release.ps1 -Version 1.2026.10.10 -NotesFile notes.txt -Install

.DESCRIPTION
1. Checks that the working tree is clean and on main.
2. Version: -Version, or today's date as 1.YYYY.M.D. It must be higher than the current version.
3. Writes the version to the csproj <Version> and the nuspec <version> (they must match, or
   XrmToolBox keeps offering the same update) and adds the notes at the top of <releaseNotes>,
   with < > & escaped. Notes are plain text: a first line summary, then "- " bullet lines.
4. Builds in Release and runs HarnessTests and HarnessIntegration.
5. Packs artifacts/DataverseMigrationScaffolder.<version>.nupkg and checks every file in it for
   your user name, machine name, profile path and OneDrive, plus the regular expressions (one per
   line) in tools/release-check.local.txt, a file you keep only on your machine (it is ignored by
   Git) for names that must never be published, such as your organization's.
6. Commits the bump on a new branch release-<version> (not with -NoCommit).
7. With -Install, copies the DLL into XrmToolBox's Plugins folder (only while XrmToolBox is closed).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Notes,
    [string]$NotesFile,
    [switch]$Install,
    [switch]$NoCommit
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$csproj = Join-Path $root 'DataverseMigrationScaffolder\DataverseMigrationScaffolder.csproj'
$nuspec = Join-Path $root 'DataverseMigrationScaffolder.nuspec'
$dll = Join-Path $root 'DataverseMigrationScaffolder\bin\Release\DataverseMigrationScaffolder.dll'

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
function Run($exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "$exe $args failed (exit $LASTEXITCODE)" }
}
# Read and write text keeping the file's byte order mark and line endings.
function ReadText($path) { [IO.File]::ReadAllText($path) }
function WriteText($path, $text) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $bom))
}

# 1. Clean tree on main
if ($NotesFile) { $Notes = [IO.File]::ReadAllText((Resolve-Path $NotesFile)) }
if (-not $Notes -or -not $Notes.Trim()) { throw 'Pass -Notes or -NotesFile: a summary line, then "- " bullet lines.' }
if (git status --porcelain) { throw 'The working tree has changes; commit or stash them first.' }
$branch = git rev-parse --abbrev-ref HEAD
if (-not $NoCommit -and $branch -ne 'main') { throw "Run from main (now on $branch), or pass -NoCommit." }

# 2. Version
$csprojText = ReadText $csproj
$nuspecText = ReadText $nuspec
$current = [regex]::Match($csprojText, '<Version>([^<]+)</Version>').Groups[1].Value
$nuspecCurrent = [regex]::Match($nuspecText, '<version>([^<]+)</version>').Groups[1].Value
if ($current -ne $nuspecCurrent) { Write-Warning "csproj ($current) and nuspec ($nuspecCurrent) versions differ; both become the new version." }
if (-not $Version) { $Version = '1.{0}.{1}.{2}' -f (Get-Date).Year, (Get-Date).Month, (Get-Date).Day }
if ([version]$Version -le [version]$current) {
    throw "Version $Version is not higher than the current $current; pass a higher -Version (e.g. $([version]$current | ForEach-Object { '{0}.{1}.{2}.{3}' -f $_.Major, $_.Minor, $_.Build, ($_.Revision + 1) }))."
}
Step "Release $Version (current $current)"

# 3. Version and release notes
$eol = if ($nuspecText.Contains("`r`n")) { "`r`n" } else { "`n" }
$lines = ($Notes.Trim() -replace "`r`n", "`n").Split("`n") | ForEach-Object { $_.TrimEnd() }
$escaped = ($lines | ForEach-Object { $_.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;') }) -join $eol
$csprojText = [regex]::Replace($csprojText, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
$nuspecText = [regex]::Replace($nuspecText, '<version>[^<]+</version>', "<version>$Version</version>")
$marker = '<releaseNotes>' + $eol
if (-not $nuspecText.Contains($marker)) { throw 'The nuspec has no <releaseNotes> block starting on its own line.' }
$at = $nuspecText.IndexOf($marker) + $marker.Length
$nuspecText = $nuspecText.Insert($at, "${Version}: $escaped$eol$eol")
WriteText $csproj $csprojText
WriteText $nuspec $nuspecText
$null = [xml]$nuspecText   # still well-formed XML

try {
    # 4. Build and test
    Step 'Build'
    foreach ($project in 'DataverseMigrationScaffolder\DataverseMigrationScaffolder.csproj',
                         'tests\HarnessTests\HarnessTests.csproj',
                         'tests\HarnessIntegration\HarnessIntegration.csproj') {
        Run dotnet build $project -c Release -nologo -v q
    }
    Step 'Tests'
    Run (Join-Path $root 'tests\HarnessTests\bin\Release\net48\HarnessTests.exe')
    Run (Join-Path $root 'tests\HarnessIntegration\bin\Release\net48\HarnessIntegration.exe')

    # 5. Pack and check
    Step 'Pack'
    $nuget = Join-Path $root 'nuget.exe'
    Run $nuget pack $nuspec -OutputDirectory (Join-Path $root 'artifacts') -NonInteractive
    $package = Join-Path $root "artifacts\DataverseMigrationScaffolder.$Version.nupkg"

    Step 'Check the package for personal text'
    $patterns = @([regex]::Escape($env:USERNAME), [regex]::Escape($env:COMPUTERNAME), [regex]::Escape($env:USERPROFILE), 'OneDrive')
    $local = Join-Path $PSScriptRoot 'release-check.local.txt'
    if (Test-Path $local) { $patterns += Get-Content $local | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } }
    else { Write-Warning 'No tools/release-check.local.txt: checking only your user name, machine name, profile path and OneDrive.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    $found = @()
    try {
        foreach ($entry in $zip.Entries) {
            $stream = $entry.Open(); $buffer = New-Object IO.MemoryStream; $stream.CopyTo($buffer); $stream.Dispose()
            $bytes = $buffer.ToArray()
            # Text as single bytes and as UTF-16 (strings inside the DLL).
            $texts = [Text.Encoding]::GetEncoding(28591).GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes)
            foreach ($pattern in $patterns) {
                foreach ($text in $texts) {
                    $m = [regex]::Match($text, $pattern, 'IgnoreCase')
                    if ($m.Success) { $found += "$($entry.FullName): matches '$pattern'"; break }
                }
            }
        }
    }
    finally { $zip.Dispose() }
    if ($found) {
        $found | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Remove-Item $package
        throw 'The package contains personal or organization-specific text; it was deleted. Fix the source and run again.'
    }
    Write-Host "  clean: $package"
}
catch {
    # Leave the repository as it was.
    git checkout -- $csproj $nuspec
    throw
}

# 6. Commit
if (-not $NoCommit) {
    Step "Commit on release-$Version"
    Run git checkout -q -b "release-$Version"
    Run git add $csproj $nuspec
    Run git commit -q -m "${Version}: $($lines[0])"
}

# 7. Install
if ($Install) {
    $plugins = Join-Path $env:APPDATA 'MscrmTools\XrmToolBox\Plugins'
    if (Get-Process XrmToolBox -ErrorAction SilentlyContinue) { Write-Warning 'XrmToolBox is running; close it and copy the DLL yourself (or run again with -Install).' }
    elseif (-not (Test-Path $plugins)) { Write-Warning "$plugins not found; is XrmToolBox installed?" }
    else { Copy-Item $dll $plugins -Force; Step "Installed into $plugins" }
}

Write-Host ''
Write-Host "Release $Version is ready. To publish:" -ForegroundColor Green
if (-not $NoCommit) { Write-Host "  git push -u origin release-$Version   (then open and merge the pull request)" }
# Full paths, so the command works from any folder.
Write-Host "  & `"$(Join-Path $root 'nuget.exe')`" push `"$(Join-Path $root "artifacts\DataverseMigrationScaffolder.$Version.nupkg")`" -Source https://api.nuget.org/v3/index.json -ApiKey <your key>"
Write-Host "  git tag v$Version <merge commit>; git push origin v$Version   (optional)"
