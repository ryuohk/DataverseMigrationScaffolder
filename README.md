# Dataverse Migration Scaffolder (XrmToolBox tool)

<img width="120" alt="Dataverse Migration Scaffolder icon" src="https://github.com/user-attachments/assets/201e3f53-4d9d-4d74-a2c1-bb74a8b15f01" />

Generates the SQL DDL for a data-migration harness directly from Dataverse metadata:

- **Staging tables** (`stage_<Table>`): `DROP TABLE IF EXISTS` + `CREATE TABLE`, one column per
  (filtered) Dataverse attribute, typed by the mapping table below, plus the fixed audit
  boilerplate.
- **GUID mapping tables** (`guid_<Table>`): created only if missing (`IF OBJECT_ID(...) IS NULL`),
  containing the unique identifier column, primary name column, legacyid, and all lookup columns.

Scripts are split into separate files for staging vs guid, with **strictly one dependency tier
per file**. Tier 0 is everything with no lookup dependencies inside the selection; tier n sits at
the end of a dependency chain of length n. That lines up with SSIS packages organized by
dependency layer. A tier larger than the batch size (default 40) is split into parts, but tiers
are never mixed within one file.

Cycles are broken by dropping the offending dependency edges, which is called out in the file
header. Tiers are then computed on the clean graph, so cycle members merge into their natural
tier instead of inflating the tier count. Connection and environment selection come from
XrmToolBox's built-in connection manager.

## Build

1. Open `DataverseMigrationScaffolder.sln` in Visual Studio 2022.
2. Restore NuGet packages (the project references the latest `XrmToolBoxPackage`, which pulls in
   XrmToolBox.Extensibility and the Dataverse SDK).
3. Build (Debug or Release, Any CPU, .NET Framework 4.8).

## Install into XrmToolBox

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

5. Set **Schema** (default `dbo`) and **Batch** (default 40), then pick a folder with **Set
   Output Folder**. The output options row has two sections:

   **Table scripts**
   - Staging and GUID file sets, each with an editable table-name prefix and a *Drop & recreate*
     or *Create if missing* mode.
   - **Index legacyid**: guarded nonclustered index on every `*legacyid` column.

   **Extra outputs**
   - **Truncate script** (`truncate.sql`): truncates all staging tables, guid truncates
     commented out.
   - **Teardown script** (`teardown.sql`): drops all staging tables, guid drops commented out.
   - **Data dictionary** (`data_dictionary.xlsx`): one sheet per table ordered by display name,
     each with an entity info block and per-column logical name, display name, type, lookup
     targets, description, and SQL type. A `~Tables` index sheet shows tier, file number, and
     cycle-dropped dependencies.
   - **Mermaid diagram** (`diagram.mmd`): flowchart of lookup dependencies with one subgraph per
     tier and dashed arrows for cycle-dropped edges. Render at mermaid.live or paste into GitHub
     or Azure DevOps markdown.
   - **Manifest JSON** (`manifest.json`, on by default). See below.

6. **Generate Scripts** retrieves attribute metadata per checked table, sorts by dependency,
   writes `01_create_staging.sql`, `02_create_staging.sql`, and so on through
   `01_create_guid.sql`, then shows a preview per file. Circular dependencies are broken
   automatically and noted in both the file header comment and the warnings panel. The checked
   selection is saved with each successful run.

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
Everything else, including custom audit columns like legacyid fields, is emitted only if it
exists in the table's metadata. Change the block in `Core/ScriptGenerator.cs`.

Skipped attributes: system audit columns (`createdon`, `modifiedby`, and friends), `statuscode`,
`statecode` (re-added as boilerplate), virtual and helper attributes, non-primary
uniqueidentifiers such as `address1_addressid`, file/image/partylist columns, and `_base`
metadata rows (regenerated from the money column instead). Edit `GlobalSkip` in
`Core/MetadataMapper.cs`.

**GUID tables** contain the `<primaryid>` as `VARCHAR(100)`, the primary name column, any
`*legacyid` column the table actually has in Dataverse, and every custom lookup column as
`NVARCHAR(100)`, with polymorphic ones getting their `<name>type` companion. System `ownerid` is
not repeated in guid tables.

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
- The generator is isolated in `Core/ScriptGenerator.cs`. Adding a new output kind (TRUNCATE
  scripts, SELECT column lists, data dictionary, KingswaySoft column maps) means adding one
  method that walks the same `TableModel` list.
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

## License

MIT. See [LICENSE](LICENSE).
