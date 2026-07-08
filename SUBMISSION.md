# Publishing Dataverse Migration Scaffolder to the XrmToolBox Tool Library

One-time setup, then repeatable steps for each release.

## One-time setup

### 1. GitHub repo

1. Create the repo **github.com/ryuohk/DataverseMigrationScaffolder** (public).
2. From this folder, push everything (the `.gitignore` already excludes `bin`/`obj`/`.vs`):
   ```
   git init
   git add .
   git commit -m "Dataverse Migration Scaffolder 1.2026.7.1"
   git branch -M main
   git remote add origin https://github.com/ryuohk/DataverseMigrationScaffolder.git
   git push -u origin main
   ```
3. Verify the icon URL resolves in a browser (validation requires it):
   `https://raw.githubusercontent.com/ryuohk/DataverseMigrationScaffolder/main/images/logo.png`

### 2. nuget.org account

Create an account at https://www.nuget.org (Microsoft account sign-in works).

### 3. XrmToolBox portal account

Sign in at https://www.xrmtoolbox.com (needed to register the tool).

## Each release

### 1. Version bump

The **assembly version and package version must match** (Tool Library rule - otherwise the
tool permanently shows "update available"). Two places, same number:

- `DataverseMigrationScaffolder\DataverseMigrationScaffolder.csproj` → `<Version>`
- `DataverseMigrationScaffolder.nuspec` → `<version>` (also update `<releaseNotes>`)

Scheme: `1.<year>.<month>.<build>` (e.g. 1.2026.7.1).

### 2. Build

Visual Studio → Release → Rebuild Solution. Confirm
`DataverseMigrationScaffolder\bin\Release\DataverseMigrationScaffolder.dll` is fresh.

### 3. Pack

From this folder (get nuget.exe from https://www.nuget.org/downloads if needed):

```
nuget pack DataverseMigrationScaffolder.nuspec
```

This produces `DataverseMigrationScaffolder.1.2026.7.1.nupkg`. Rules already handled by the
nuspec: the DLL lands in `lib\net48\Plugins`, ONLY our assembly is included (no SDK/XTB DLLs),
and the dependency is on **XrmToolBox** (not XrmToolBoxPackage).

Optional sanity check: open the .nupkg in NuGet Package Explorer and confirm the single DLL
under `lib/net48/Plugins`.

### 4. Push to nuget.org

Either upload the .nupkg via the nuget.org website (Upload Package), or:

```
nuget push DataverseMigrationScaffolder.1.2026.7.1.nupkg -Source https://api.nuget.org/v3/index.json -ApiKey YOUR_API_KEY
```

(Create an API key at nuget.org → account → API Keys.)

Wait for the package to finish **indexing** (nuget.org shows a "validating" banner; can take
15-60 minutes). Don't register on the portal before indexing completes.

### 5. First release only: register on the XrmToolBox portal

Go to https://www.xrmtoolbox.com/plugins/new/ and submit the package id:
`DataverseMigrationScaffolder`. The portal parses the NuGet metadata. An administrator then
reviews and validates the tool - this can take a few days. Subsequent releases just need
steps 1-4; the Tool Library picks up new versions from nuget.org automatically.

## Validation checklist (how this tool complies)

| Requirement | Status |
|---|---|
| NuGet package has an Icon url | logo.png raw URL in nuspec |
| NuGet package has a Project url | GitHub repo URL in nuspec |
| Tool DLL under a "Plugins" folder in package | `lib\net48\Plugins` |
| Package version == assembly version | both 1.2026.7.1 - keep in sync! |
| Dedicated large + small tool images | BigImageBase64/SmallImageBase64 in Plugin.cs |
| Controls resize with the main window | dock/anchor layout |
| Opens without an organization connected | yes; actions use ExecuteMethod (prompts to connect) |
| Long operations async, UI not frozen | WorkAsync throughout, cancelable |
