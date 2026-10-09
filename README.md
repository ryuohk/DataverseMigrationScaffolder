# Dataverse Migration Scaffolder (XrmToolBox tool)

<img align="right" width="110" alt="Dataverse Migration Scaffolder icon" src="https://github.com/user-attachments/assets/201e3f53-4d9d-4d74-a2c1-bb74a8b15f01" />

[XrmToolBox Tool Library](https://www.xrmtoolbox.com/plugins/plugininfo/?id=ccf116e2-dc7a-f111-b27e-000d3add9fd6) · [NuGet](https://www.nuget.org/packages/DataverseMigrationScaffolder/)

Generates the SQL DDL for a data-migration harness directly from Dataverse metadata:

- **Staging tables** (`stage_<Table>`): `DROP TABLE IF EXISTS` + `CREATE TABLE`, one column per
  (filtered) Dataverse attribute, typed by the mapping table below, plus the fixed audit
  boilerplate. Each `*legacyid` column is `UNIQUE` (constraint `UQ_stage_<Table>_<column>`), so
  a legacy record can only be staged once.
- **GUID mapping tables** (`guid_<Table>`): created only if missing (`IF OBJECT_ID(...) IS NULL`),
  containing only the unique identifier column and the legacyid (a legacy id -> GUID crosswalk).

Scripts are split into separate files for staging vs guid, with **strictly one dependency tier
per file**. Tier 0 is everything with no lookup dependencies inside the selection; tier n sits at
the end of a dependency chain of length n. That lines up with SSIS packages organized by
dependency layer. A tier larger than the batch size (default 40) is split into parts, but tiers
are never mixed within one file.

Cycles are broken by dropping the offending dependency edges, which is called out in the file
header. Tiers are then computed on the clean graph, so cycle members merge into their natural
tier instead of inflating the tier count. Connection and environment selection come from
XrmToolBox's built-in connection manager.

## Install

Open XrmToolBox, go to the **Tool Library**, search for **Dataverse Migration Scaffolder**, and
install. That is the normal path and it keeps you on the latest release automatically.

## Build from source

1. Open `DataverseMigrationScaffolder.sln` in Visual Studio 2022.
2. Restore NuGet packages (the project references the latest `XrmToolBoxPackage`, which pulls in
   XrmToolBox.Extensibility and the Dataverse SDK).
3. Build (Debug or Release, Any CPU, .NET Framework 4.8).

Copy `DataverseMigrationScaffolder.dll` from `bin\Debug` (or `bin\Release`) into your XrmToolBox
plugins folder, typically:

```
%APPDATA%\MscrmTools\XrmToolBox\Plugins
```

Restart XrmToolBox. The tool appears as **Dataverse Migration Scaffolder**.

Tip for debugging: in the project's Debug settings, set the start program to `XrmToolBox.exe` and
add a post-build event to copy the DLL into the plugins folder.

## Usage

1. Open the tool and connect to an environment (XrmToolBox connection manager).

2. **Load Tables** retrieves the table list and the solution list. Nothing is checked by default,
   but tables you checked in a previous session are re-checked automatically.

3. Pick a **Solution** in the toolbar. Default means everything. Any other solution filters both
   the table grid and the generated columns down to that solution's components. Entities added
   with subcomponents include all their attributes; otherwise only explicitly added attributes
   are emitted. Primary id and primary name are always kept. The choice is remembered per
   environment.

4. Check the tables to include. The filter box and Category dropdown narrow the grid, and the
   checkbox in the Include column header checks or unchecks everything currently shown by the
   filter.

5. Pick a folder with **Set Output Folder**. Step 3 holds settings only:

   - **Table list**: Filter, Category and Checked only narrow the grid.
   - **Staging tables**: table-name prefix, *Drop & recreate* or *Create if missing* mode,
     **Schema** (default `dbo`) and **Tables per file** (default 40).
   - **GUID tables**: prefix and mode, and **Match key** suffixes (default `legacyid`).
     Match keys are `UNIQUE` in both staging and GUID tables, which also indexes them.

6. Step 4: **Generate SSIS Project** builds the migration harness (see below). **Export**
   saves files to the output folder without a project:
   - **SQL scripts (staging and GUID tables)**: `01_create_staging.sql`, `02_create_staging.sql`,
     ... and `01_create_guid.sql`, ..., one dependency tier per file.
   - **Data dictionary (Excel)**: `data_dictionary.xlsx`, one sheet per table ordered by display
     name, each with an entity info block and per-column logical name, display name, type, lookup
     targets, description, and SQL type. A `~Tables` index sheet shows tier, file number, and
     cycle-dropped dependencies.
   - **Scaffolder run (manifest, scripts, metadata seed)**: the SQL scripts plus `manifest.json`
     and `meta_seed.sql`, everything needed to build an SSIS project later without connecting.

   Each run retrieves attribute metadata per checked table, sorts by dependency and shows a
   preview per file. Circular dependencies are broken automatically and noted in both the file
   header comment and the warnings panel. The checked selection is saved with each successful run.

## manifest.json

A machine-readable description of the run, written alongside the .sql files so an ETL pipeline
can consume the scaffolding instead of parsing SQL. Top-level keys:

| Key | Contents |
|---|---|
| `generator` | tool name, assembly version, ISO-8601 timestamp |
| `options` | schema, prefixes, match-key suffixes, batch size, per-kind existence mode, dependency-ranking exclusions |
| `files` | every emitted .sql file: name, kind (`staging` / `guid`), tier, part, and the tables it contains |
| `tables` | per table: logical/schema/display name, `tier`, `fileNumber`, `stagingFile` / `guidFile`, fully-qualified `stagingTable` / `guidTable`, primary id and name attributes, `matchKeys`, in-scope `dependencies`, `externalDependencies`, `droppedDependencies`, `isCycleMember`, and `columns` |
| `columns` (per table) | `name`, `sqlType`, `dataverseType`, flags (`isPrimaryId`, `isPrimaryName`, `isMatchKey`, `isLookup`, `isPolymorphic`, `isCustom`), `targets` and `targetsInScope` for lookups, and `requiresDeferredUpdate` |
| `cycles` | each table whose dependency edges were dropped, with the dropped targets |
| `warnings` | the same warnings shown in the UI |

Typical uses: sequence SSIS or ETL packages by `tier`, generate deferred-lookup UPDATE passes
from the columns flagged `requiresDeferredUpdate`, or diff manifests between runs to detect
schema drift. File names in the manifest are produced by the same helpers that name the actual
files, so the two can never drift apart.

```json
{
  "manifestVersion": 1,
  "tables": [
    {
      "logicalName": "contoso_case",
      "tier": 2,
      "fileNumber": 3,
      "stagingFile": "03_create_staging.sql",
      "stagingTable": "[dbo].[stage_contoso_Case]",
      "matchKeys": [ "contoso_legacyid" ],
      "dependencies": [ "contoso_matter" ],
      "droppedDependencies": [ "contoso_caseevent" ],
      "isCycleMember": true,
      "columns": [
        {
          "name": "contoso_caseeventid",
          "sqlType": "NVARCHAR(100)",
          "dataverseType": "Lookup",
          "isLookup": true,
          "targets": [ "contoso_caseevent" ],
          "targetsInScope": [ "contoso_caseevent" ],
          "requiresDeferredUpdate": true
        }
      ]
    }
  ]
}
```

## Type conventions (and where to change them)

| Dataverse type | SQL type | Where |
|---|---|---|
| String | `NVARCHAR(MaxLength)` from metadata | `Core/MetadataMapper.cs` |
| Memo | `NVARCHAR(MAX)` | |
| Lookup / Customer / Owner | `NVARCHAR(100)`; polymorphic lookups also get `<name>type` | |
| Choice (picklist) | `NVARCHAR(100)` | |
| Multi-select choice | `NVARCHAR(MAX)` | |
| Money | `NVARCHAR(100)` + companion `<name>_base` | |
| Decimal / Double | `NVARCHAR(100)` | |
| Whole number / BigInt | `INT` / `BIGINT` | |
| Boolean | `BIT` | |
| DateTime / Date-only | `DATETIME2(7)` / `DATE` | |
| Primary key (uniqueidentifier) | `NVARCHAR(100)` (staging), `VARCHAR(100)` (guid) | |

Fixed staging boilerplate, always appended in this order: `overriddencreatedon`, `ownerid`,
`owneridtype`, `statecode INT`. These are standard Dataverse concepts valid for any table.
Last comes `tablename NVARCHAR(100) DEFAULT '<staging table>'` (for example
`DEFAULT 'stage_new_Project'`): the Stage SQL never inserts it, so every row names the staging
table it came from, and the migration harness can write it to the error table.
Everything else, including custom audit columns like legacyid fields, is emitted only if it
exists in the table's metadata. Change the block in `Core/ScriptGenerator.cs`.

Skipped attributes: system audit columns (`createdon`, `modifiedby`, and friends), `statuscode`,
`statecode` (re-added as boilerplate), virtual and helper attributes, non-primary
uniqueidentifiers such as `address1_addressid`, file/image/partylist columns, and `_base`
metadata rows (regenerated from the money column instead). Edit `GlobalSkip` in
`Core/MetadataMapper.cs`.

**GUID tables** contain only the `<primaryid>` as `VARCHAR(100)` and any `*legacyid` column the
table actually has in Dataverse, which is `UNIQUE` (`CONSTRAINT [UQ_<guid prefix><table>_<column>]`):
each legacy record maps to exactly one Dataverse record. They are a legacy id ->
GUID crosswalk: staging SQL resolves lookups by joining on these two columns, and the migration
harness writes them as records are created. Primary name, lookup and state columns are not
stored; they live in staging and Dataverse. GUID tables created by earlier versions keep their
extra columns (the script never alters an existing GUID table); they are nullable and unused.
In *Create if missing* mode an existing GUID table also does not get the `UNIQUE` constraint; add it
with `ALTER TABLE <guid table> ADD CONSTRAINT [UQ_<guid table>_<column>] UNIQUE ([<column>])` once
any duplicate legacy ids are removed.

**Column inclusion rule (all tables):** with the Default solution selected, every non-system
attribute is included. With a specific solution selected, only that solution's components are
included (see Usage). The primary id and primary name columns are always kept. The Category
column in the grid is just the publisher prefix parsed from the logical name, or "oob" when
there is no prefix.

## Quality-of-life features

- **Metadata cache**: attribute metadata is cached per session, so regenerating after a settings
  tweak is near-instant. Load Tables or switching connection clears the cache.
- **Cancelable generation**: the progress overlay has a Cancel button.
- **Per-environment selections**: checked tables are remembered separately for each connected org
  and restored when you switch back.
- **Checked only**: the checkbox next to the Category filter shows just the checked tables. The
  status bar shows the live checked count, connected org, output folder, and last run summary.
- **Custom prefixes**: the staging and guid table name prefixes are editable in the output
  options row, so `custom_` gives you `custom_Account`.

## Adapting it to your own conventions

- Prefixes (`stage_`, `guid_`) and the values used for Category labelling live in
  `Core/ToolSettings.cs`. Point them at whatever publisher prefix you use.
- The generator is isolated in `Core/ScriptGenerator.cs`. Adding a new output kind (SELECT
  column lists, KingswaySoft column maps) means adding one method that walks the same
  `TableModel` list.
- Metadata retrieval and dependency sorting carry no environment-specific logic, so they run
  against any org as they are.

## Project layout

```
DataverseMigrationScaffolder.sln
DataverseMigrationScaffolder/
  DataverseMigrationScaffolder.csproj   SDK-style, net48, XrmToolBoxPackage
  Plugin.cs                        MEF export / tool registration
  MainControl.cs                   UI (PluginControlBase)
  ExclusionsDialog.cs              dependency-ranking exclusions editor
  Core/
    Models.cs                      TableModel / SqlColumn / results
    ToolSettings.cs                persisted settings (per-org selections, output options)
    MetadataService.cs             RetrieveAllEntities / RetrieveEntity wrappers
    MetadataMapper.cs              attribute filtering + SQL type mapping
    DependencySorter.cs            topological sort with cycle breaking
    ScriptGenerator.cs             staging + guid DDL emission, batching, extra outputs
    JsonWriter.cs                  minimal dependency-free JSON writer (manifest.json)
    XlsxWriter.cs                  minimal dependency-free xlsx writer (data dictionary)
```

## Generate an SSIS migration project (1.2026.9.29)

Step 4's **Generate SSIS Project** button builds the migration harness straight from the
checked tables. Nothing else needs to be installed: the generator is part of the plugin
(`DataverseMigrationScaffolder/Harness/`), and no
script or manifest files need to be written first.

1. Load tables, check the ones to migrate, and set the output folder (steps 1-3).
2. Click **Generate SSIS Project**. The first time, the SSIS settings open: choose the
   **built-in template** and enter your SQL Server and the staging and legacy database names
   (the Dataverse URL defaults to the connected environment), or choose **my own reference
   project** and its `.dtproj` or `.sln`, template package and so on (see below). Name the new
   project, then **Save and Generate**. These are remembered, so later runs need one click.
3. The scaffolder retrieves the metadata, builds the staging and GUID scripts, manifest and
   metadata seed in memory, and writes the SSIS project to a new `SSIS-<date-time>` folder
   inside the output folder. The scaffolder run it was built from (scripts, `manifest.json`,
   `meta_seed.sql`) is saved in that folder's `Scaffolder` subfolder.
4. Review the summary (first entry in the file list) and `harnessgen-report.json`, then
   build the project in Visual Studio with SSIS Projects and KingswaySoft.

### Built-in template or your own reference project

The generated project is cloned from a *reference project* that shows how one table is
migrated. Every table name, column and publisher prefix in the result comes from your
Dataverse environment, not from the reference.

- **Built-in template** (no files needed): the plugin carries a reference project for a neutral
  sample table (`new_category`, with `new_` columns). It has the recommended KingswaySoft
  settings (owner, state and audit columns, automation options), a `Stage <Table>` task with
  its staging SQL, and error logging that records each row's staging `tablename`. When you
  generate, it is unpacked to a temporary folder with your SQL Server, staging and legacy
  database names and Dataverse URL written into its connections and staging SQL, and removed
  afterwards. The Staging and Legacy connections use Windows authentication. Enter the
  Dataverse client id and secret in Visual Studio after opening the project; the plugin never
  stores them.
- **My own reference project**: a hand-built SSIS project of yours, with its own connections and
  settings, copied the same way. Its template package must contain the supported single
  `Migrate <Table>` data flow (see below). If the table it migrates is not among the tables you
  generate, put a `template.json` next to its `.dtproj` naming that table's primary name and
  legacy key (`{"table": "...", "primaryName": "...", "matchKey": "..."}`), as the built-in
  template does.

**SSIS Settings...** changes the template, or builds a project from a previous
scaffolder run without connecting to Dataverse: by default the `Scaffolder\manifest.json` of
the newest `SSIS-*` folder, or any `manifest.json` saved with **Export > Scaffolder run**.

**Sensitive data** in SSIS Settings sets the new project's protection level:

- Unticked (default): the reference's protection level is kept and its encrypted values, such as
  the Dataverse client secret, are copied unchanged. With the usual *EncryptSensitiveWithUserKey*
  they only open for the person who saved the reference.
- Ticked (*Don't save passwords or secrets*): the project and every package are saved with
  *DontSaveSensitive* and every stored password and secret is removed. Anyone can open the
  project and it is safe to commit. Supply the secret when deploying, in the SSIS catalog's
  connection manager settings (for example `CM.Dynamics CRM Connection Manager.ClientSecret`) or
  a SQL Agent job step; every connection setting can be overridden there per environment. To run
  in Visual Studio, re-enter the secret after opening the project (it is not saved).

The built-in template's package, `01b - Harness.dtsx`, is the model for your own: its GUID table
holds just the record GUID and legacy ID, matching the GUID scripts, and the create
destination's Default Output writes `SavedRecordId` and the legacy ID. (An OLE DB Command on the
update branch that updates the GUID table by `SavedRecordId` is also accepted.) The package also
holds a `Stage Category` Execute SQL task that runs `Queries\stage_new_Category.sql`; it is the
template for every table's Stage task and SQL.

Generated layout per scaffolder file group:
- `NNa - Staging`: Create Staging Tables, then one `Stage <Table>` task per table, loading
  staging from the legacy database with the SQL in the output's `Queries` folder (lookups
  are resolved through the referenced tables' GUID tables).
- `NNb - Harness`: Create GUID Tables, then one `Migrate <Table>` data flow per table.
- `00 - Error and UpdateTime Tables` also creates every GUID table, so staging SQL can join
  them on a first run. `Run_Migration` runs each group's staging package, then its harness
  package, tier by tier, then the deferred updates.

Other project-deployment-model SSIS projects are accepted when their selected
template package contains the supported single `Migrate <Table>` data flow:
staging source, create/update split, KingswaySoft destinations, GUID create output,
error outputs and optional GUID update command, plus an optional `Stage <Table>` task.
This is not an arbitrary SSIS project converter; unsupported templates fail with an
explanation. KingswaySoft destination settings, including the automation options (bypass
Power Automate flows, disable plugins, workflows and auditing), are copied to every table.

Project connection manager files and required package connections are retained.
Credentials protected by the original user's Windows identity may require that
same identity or reconfiguration on another machine. Source files are not changed.
The integration refuses a nonempty output folder and does not execute SQL or
Dataverse writes. Without a Stage task template, staging packages are not run by
`Run_Migration.dtsx` because they recreate staging tables; load legacy data first.

Tables without usable legacy match keys remain explicitly skipped by the existing
engine. The current sample generates 20 packages: 9 staging, 8 harness, setup,
deferred updates and the entry point, with migration flows for 42 of 113 tables.
Do not treat successful generation as complete coverage of all selected tables.

### Build and verify the integration

```powershell
dotnet build DataverseMigrationScaffolder/DataverseMigrationScaffolder.csproj -c Release
dotnet build tests/HarnessIntegration/HarnessIntegration.csproj -c Release
# Test the integration entry point used by the dialog on the neutral test fixtures:
tests/HarnessIntegration/bin/Release/net48/HarnessIntegration.exe
# or on your own scaffolder output and reference project, into an EMPTY folder:
tests/HarnessIntegration/bin/Release/net48/HarnessIntegration.exe manifest.json reference.sln template.dtsx new-output
./nuget.exe pack DataverseMigrationScaffolder.nuspec -OutputDirectory artifacts
```

The test checks solution/project discovery, in-process generation, unchanged reference
files, byte-identical connection managers, invalid package rejection, nonempty-output
refusal, error reporting, DontSaveSensitive and the built-in template. No real SSIS project or
connection string is embedded.

GitHub Actions (`.github/workflows/build.yml`) builds everything and runs both tests on every pull
request and push to `main`, checks that the csproj and nuspec versions match, and packs the
NuGet package (downloadable from the run as the `nupkg` artifact).

### Generator regression tests

`tests/HarnessTests` generates every scenario in `tests/HarnessTests/TestData/scenarios.json`
(templates with and without a Stage task, GUID sync, polymorphic lookups, UNIQUE GUID keys,
staging `tablename` logging, the built-in template, owner and audit columns, protection levels,
and the expected errors for bad input) from its fixtures, once from files and once from text held
in memory as the plugin does, and requires every output file to match `TestData/Expected`
byte for byte (the output folder is written as `{OUTPUT}`). The expected output was recorded from
the original Python generator, which this C# generator replaced. `TestData` and the built-in
template are stored byte for byte (`.gitattributes`), so Git never changes their line endings.

```powershell
dotnet build tests/HarnessTests/HarnessTests.csproj -c Release
tests/HarnessTests/bin/Release/net48/HarnessTests.exe
# After an intended change to the generated output, review the differences, then record it:
tests/HarnessTests/bin/Release/net48/HarnessTests.exe --update
```

### Validate a generated project in SSIS

`tools/validate-harness.ps1 -ProjectDir <generated project>` builds the project with Visual Studio
and runs or validates every package against a throwaway LocalDB database (never your staging
database). It needs Visual Studio with SSIS Projects, KingswaySoft and SQL Server Express LocalDB.

### Refresh the built-in template

`tools/MakeBuiltInTemplate` makes `DataverseMigrationScaffolder/BuiltInTemplate` from a clone of
your own reference project, replacing organization-specific names with neutral ones and removing
connections, secrets, creator names and personal paths. It fails if anything specific is left.
The renames and forbidden words come from a rules file you keep outside this repository:

```powershell
dotnet build tools/MakeBuiltInTemplate/MakeBuiltInTemplate.csproj -c Release
tools/MakeBuiltInTemplate/bin/Release/net48/MakeBuiltInTemplate.exe <reference repo> DataverseMigrationScaffolder/BuiltInTemplate <manifest.json> <rules.json>
```

`rules.json` is `{"renames": [["old", "new"], ...], "forbidden": ["regex", ...]}`; the manifest must
contain the reference project's table, for its primary name and match key.

## License

MIT. See [LICENSE](LICENSE).
