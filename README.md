# Dataverse Migration Scaffolder (XrmToolBox tool)

Generates the SQL DDL for a data-migration harness directly from Dataverse metadata:

- **Staging tables** (`stage_<Table>`): `DROP TABLE IF EXISTS` + `CREATE TABLE`, one column per
  (filtered) Dataverse attribute, typed per harness conventions, plus the fixed audit boilerplate.
- **GUID mapping tables** (`guid_<Table>`): created only if missing (`IF OBJECT_ID(...) IS NULL`),
  containing the unique identifier column, primary name column, legacyid, and all lookup columns.

Scripts are split into separate files for staging vs guid, with **strictly one dependency
tier per file** (tier 0 = no lookup dependencies within the selection, tier n = deepest
dependency chain of length n) — matching SSIS packages organized by dependency layer. A tier
larger than the batch size (default 40) is split into parts, but tiers are never mixed within
one file. Cycles are broken by dropping the offending dependency edges (warned in the file
header), then tiers are computed on the clean graph, so cycle members merge into their natural
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

Tip for debugging: in the project's Debug settings, set the start program to `XrmToolBox.exe`
and a post-build event to copy the DLL into the plugins folder.

## Usage

1. Open the tool and connect to an environment (XrmToolBox connection manager).
2. **Load Tables** — retrieves the table list and the solution list. Nothing is checked by
   default; tables you checked in a previous session are re-checked automatically.
3. Pick a **Solution** (toolbar). Default = everything; any other solution filters both the
   table grid and the generated columns to that solution's components (entities added with
   subcomponents include all their attributes; otherwise only explicitly added attributes are
   emitted — primary id/name are always kept). The choice is remembered per environment.
4. Check the tables to include. The filter box and Category dropdown narrow the grid, and the
   checkbox in the Include column header checks/unchecks everything currently shown by the
   filter.
4. Set **Schema** (default `dbo`) and **Batch** (default 40), pick a folder via
   **Set Output Folder**.
   The output options row has two sections. **Table scripts**: Staging / GUID file sets, each
   with an editable table-name prefix and a *Drop & recreate* or *Create if missing* mode, plus
   **Index legacyid** (guarded nonclustered index on every `*legacyid` column). **Extra
   outputs**: **Truncate script** (`truncate.sql` truncating all staging tables, guid truncates
   commented out), **Teardown script** (`teardown.sql` dropping all staging tables, guid drops
   commented out), **Data dictionary** (`data_dictionary.xlsx`: one sheet per table ordered
   by display name — entity info block plus per-column logical name, display name, type, lookup
   targets, description, and SQL type — with a `~Tables` index sheet showing tier, file number,
   and cycle-dropped dependencies), and **Mermaid diagram** (`diagram.mmd`: flowchart of lookup
   dependencies with one subgraph per tier, dashed arrows for cycle-dropped edges; render at
   mermaid.live or paste into GitHub/Azure DevOps markdown).
5. **Generate Scripts** — retrieves attribute metadata per checked table, sorts by dependency,
   writes `01_create_staging.sql`, `02_create_staging.sql`, …, `01_create_guid.sql`, … and shows a
   preview per file. Circular dependencies are broken automatically and noted in the file header
   comment and warnings panel. The checked selection is saved with each successful run.

## Conventions baked in (and where to change them)

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

Fixed staging boilerplate (always appended, in this order): `overriddencreatedon`, `ownerid`,
`owneridtype`, `statecode INT` — standard Dataverse concepts valid for any table. Everything
else (including custom audit columns like legacyid fields) is emitted only if it exists in the
table's metadata. Change the block in `Core/ScriptGenerator.cs`.

Skipped attributes: system audit columns (`createdon`, `modifiedby`, …), `statuscode`,
`statecode` (re-added as boilerplate), virtual/helper attributes, non-primary uniqueidentifiers
(`address1_addressid`, …), file/image/partylist columns, and `_base` metadata rows (regenerated
from the money column instead). Edit `GlobalSkip` in `Core/MetadataMapper.cs`.

**GUID tables** include: `<primaryid>` `VARCHAR(100)`, primary name column, any `*legacyid`
column the table actually has in Dataverse, and every custom lookup column (`NVARCHAR(100)`,
polymorphic ones with their `<name>type` companion). System `ownerid` is not repeated in guid
tables (matches the existing harness).

**Column inclusion rule (all tables):** with the Default solution selected, every non-system
attribute is included. With a specific solution selected, only that solution's components are
included (see Usage). The primary id and primary name columns are always kept. The Category
column in the grid is simply the publisher prefix parsed from the logical name ("oob" when
there is no prefix).

## Quality-of-life features

- **Metadata cache**: attribute metadata is cached per session, so regenerating after a settings
  tweak is near-instant. Load Tables or switching connection clears the cache.
- **Cancelable generation**: the progress overlay has a Cancel button.
- **Per-environment selections**: checked tables are remembered separately for each connected
  org and restored when you switch back.
- **Checked only** checkbox next to the Category filter shows just the checked tables; the
  status bar shows the live checked count, connected org, output folder, and last run summary.
- **Custom prefixes**: the staging and guid table name prefixes are editable in the output
  options row (e.g. `custom_` gives `custom_Account`).

## Genericizing beyond this project

- Prefixes (`stage_`, `guid_`) and the prefixes/tables used for Category labelling live in
  `Core/ToolSettings.cs` — point them at any publisher prefix.
- The generator is isolated in `Core/ScriptGenerator.cs`; adding new output kinds
  (TRUNCATE scripts, SELECT column lists, data dictionary, KingswaySoft column maps) means adding
  one method that walks the same `TableModel` list.
- No project-specific logic lives in metadata retrieval or dependency sorting.

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
    ScriptGenerator.cs             staging + guid DDL emission, batching
```
