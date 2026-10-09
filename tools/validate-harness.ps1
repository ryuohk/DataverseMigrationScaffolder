<#
.SYNOPSIS
    Build a generated harness in Visual Studio and validate every package with SSIS,
    against a throwaway LocalDB staging database.

.DESCRIPTION
    1. Copies the generated project to a short temporary path (SSIS builds fail on long paths)
       and builds it with devenv.com, producing the .ispac.
    2. Opens the .ispac with the SSIS runtime and points the project's Staging connection at a
       new LocalDB database, in memory only. The project files, and the real staging database,
       are never changed.
    3. Executes "00 - ..." and every "NNa - Staging" package, whose SQL creates the tables.
       When the project has Stage <Table> SQL (Queries\*.sql), an empty stand-in for the legacy
       database it reads is created on LocalDB first (never over an existing database).
    4. Validates every other package (harness, deferred updates, Run_Migration). KingswaySoft
       destinations validate with the Dynamics CRM connection manager as saved in the project.
    5. Drops the LocalDB database unless -KeepDatabase is given.

    Requires Visual Studio 2022 with SQL Server Integration Services Projects, KingswaySoft
    SSIS Integration Toolkit for Microsoft Dynamics 365, and SQL Server Express LocalDB.

.EXAMPLE
    .\tools\validate-harness.ps1 -ProjectDir C:\work\MyHarness
#>
param(
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [string]$Database = "harnessgen_validate",
    [string]$StagingConnection = "Staging",
    [int]$MaxMessages = 6,
    [switch]$KeepDatabase
)
$ErrorActionPreference = 'Stop'

$dtproj = Get-ChildItem -Path $ProjectDir -Filter *.dtproj | Select-Object -First 1
if (-not $dtproj) { throw "No .dtproj in $ProjectDir" }
$name = $dtproj.BaseName

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -property installationPath
$devenv = Join-Path $vs 'Common7\IDE\devenv.com'
if (-not (Test-Path $devenv)) { throw "devenv.com not found under $vs" }

# SQL Server version targeted by the project decides the SSIS runtime to load.
$target = ([xml](Get-Content $dtproj.FullName -Raw)).Project.DeploymentModelSpecificContent.Manifest.Project.Properties.Property |
    Where-Object { $_.Name -eq 'TargetServerVersion' } | Select-Object -ExpandProperty '#text' -ErrorAction SilentlyContinue
$versions = @{ 'SQLServer2016' = '13'; 'SQLServer2017' = '14'; 'SQLServer2019' = '15'; 'SQLServer2022' = '16' }
$major = if ($target -and $versions.ContainsKey($target)) { $versions[$target] } else { '16' }
$dts = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\Microsoft.SqlServer.ManagedDTS' -Recurse -Filter Microsoft.SqlServer.ManagedDTS.dll |
    Where-Object { $_.FullName -like "*v4.0_$major.0.0.0*" } | Select-Object -First 1
if (-not $dts) { throw "SSIS runtime $major.0 (Microsoft.SqlServer.ManagedDTS) is not installed" }
Add-Type -Path $dts.FullName

$work = Join-Path $env:TEMP "hgv-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
Copy-Item $ProjectDir $work -Recurse
try {
    $log = "$work.build.log"
    $build = Start-Process -FilePath $devenv -ArgumentList "`"$work\$name.dtproj`" /Build Development /Out `"$log`"" -NoNewWindow -Wait -PassThru
    Get-Content $log | Select-String -Pattern 'error|Build: ' | ForEach-Object { $_.Line }
    if ($build.ExitCode -ne 0) { throw "Visual Studio build failed (exit $($build.ExitCode)); see $log" }
    $ispac = Join-Path $work "bin\Development\$name.ispac"

    $master = New-Object System.Data.SqlClient.SqlConnection 'Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=SSPI'
    function Invoke-Master($sql) { $master.Open(); try { $c = $master.CreateCommand(); $c.CommandText = $sql; [void]$c.ExecuteNonQuery() } finally { $master.Close() } }
    Invoke-Master "IF DB_ID(N'$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END; CREATE DATABASE [$Database];"

    # Stage <Table> SQL (Queries\*.sql) reads the legacy database by three-part name. Create an
    # empty stand-in on LocalDB with every table and column the SQL reads, so the staging
    # packages run and the deferred update sources validate. An existing database of that
    # name is never touched.
    $legacy = @{}
    foreach ($file in @(Get-ChildItem (Join-Path $work 'Queries') -Filter *.sql -ErrorAction SilentlyContinue)) {
        $text = Get-Content $file.FullName -Raw
        foreach ($m in [regex]::Matches($text, 'FROM\s+\[(\w+)\]\.\[(\w+)\]\.\[(\w+)\]\s+(\w+)')) {
            $db, $schema, $table, $alias = $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value, $m.Groups[4].Value
            if (-not $legacy.ContainsKey($db)) { $legacy[$db] = @{} }
            $key = "[$schema].[$table]"
            if (-not $legacy[$db].ContainsKey($key)) { $legacy[$db][$key] = New-Object System.Collections.Generic.HashSet[string] }
            foreach ($c in [regex]::Matches($text, "\b$alias\.\[(\w+)\]")) { [void]$legacy[$db][$key].Add($c.Groups[1].Value) }
        }
    }
    $stubs = @()
    foreach ($db in $legacy.Keys) {
        $check = $master.CreateCommand(); $master.Open()
        try { $check.CommandText = "SELECT DB_ID(N'$db')"; $exists = $check.ExecuteScalar() -isnot [DBNull] } finally { $master.Close() }
        if ($exists) { throw "LocalDB already has a database named '$db'; drop or rename it to validate staging SQL" }
        Invoke-Master "CREATE DATABASE [$db];"
        $stubs += $db
        $sql = ($legacy[$db].GetEnumerator() | ForEach-Object {
            "CREATE TABLE $($_.Key) (" + (($_.Value | ForEach-Object { "[$_] NVARCHAR(400) NULL" }) -join ', ') + ");"
        }) -join "`n"
        Invoke-Master "USE [$db]; $sql"
        "legacy stand-in: $db ($($legacy[$db].Count) table(s), empty)"
    }

    $project = [Microsoft.SqlServer.Dts.Runtime.Project]::OpenProject($ispac)
    $staging = $project.ConnectionManagerItems | Where-Object { $_.ConnectionManager.Name -eq $StagingConnection }
    if (-not $staging) { throw "The project has no connection manager named '$StagingConnection'" }
    $staging.ConnectionManager.ConnectionString = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=$Database;Provider=MSOLEDBSQL.1;Integrated Security=SSPI;Auto Translate=False;"

    $failed = 0
    $items = @($project.PackageItems | Sort-Object StreamName)
    $setup = { $_.StreamName -like '00 - *' -or $_.StreamName -like '*a - Staging.dtsx' }
    foreach ($pass in @(@{ What = 'execute'; Items = $items | Where-Object $setup },
                        @{ What = 'validate'; Items = $items | Where-Object { -not (& $setup) } })) {
        foreach ($item in $pass.Items) {
            $pkg = $item.LoadPackage($null)
            $result = if ($pass.What -eq 'execute') { $pkg.Execute() } else { $pkg.Validate($null, $null, $null, $null) }
            $errs = @($pkg.Errors | ForEach-Object { "[$($_.Source)] $(($_.Description -replace '\s+', ' ').Trim())" } | Select-Object -Unique)
            $warns = @($pkg.Warnings | ForEach-Object { "[$($_.Source)] $(($_.Description -replace '\s+', ' ').Trim())" } | Select-Object -Unique)
            "{0,-42} {1,-9} {2,-8} errors={3} warnings={4}" -f $item.StreamName, $pass.What, $result, $errs.Count, $warns.Count
            $errs | Select-Object -First $MaxMessages | ForEach-Object { "    E $_" }
            $warns | Select-Object -First $MaxMessages | ForEach-Object { "    W $_" }
            if ("$result" -ne 'Success') { $failed++ }
        }
    }
    if (-not $KeepDatabase) {
        [System.Data.SqlClient.SqlConnection]::ClearAllPools()
        Invoke-Master "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database];"
    }
    if ($failed) { throw "$failed package(s) failed" }
    "All packages passed."
} finally {
    # The legacy stand-ins are always dropped, even after a failure, so the next run can recreate them.
    if ($stubs -and -not $KeepDatabase) {
        [System.Data.SqlClient.SqlConnection]::ClearAllPools()
        foreach ($db in $stubs) {
            try { Invoke-Master "ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db];" }
            catch { "could not drop legacy stand-in ${db}: $_" }
        }
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
