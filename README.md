# Drawing Studio for Revit

A standalone Windows app that drives Revit to produce drawing sets to ISO 19650:
batch plans and sections, ISO-numbered sheets with automatic view layout, grid
dimensions and tags across many views, and drawing QA with one-click navigation.

Supports **Revit 2022, 2023, 2024, 2025, 2026 and 2027**.

## How it works

```
 DrawingStudio.exe  (WPF, .NET 8)                 Revit.exe
 ┌──────────────────────────┐   named pipe    ┌──────────────────────────────┐
 │ Plans & sections tab     │  JSON per line  │ DrawingStudio.Bridge.dll     │
 │ Sheets tab               │ ──────────────► │  pipe thread → queue         │
 │ Annotation tab           │ ◄────────────── │  ExternalEvent → API thread  │
 │ QA & numbering tab       │                 │  one Transaction per command │
 │ Standards tab (JSON)     │                 │  (single Ctrl+Z undoes it)   │
 └──────────────────────────┘                 └──────────────────────────────┘
```

The Revit API only works inside Revit, so a small add-in (the *bridge*) opens a
local named pipe `DrawingStudio.Bridge.<pid>` when Revit starts and runs each
request on Revit's thread through an `ExternalEvent`. The app finds running
sessions from `%LOCALAPPDATA%\DrawingStudio\sessions\`, so it can connect to
any of several open Revit versions. The pipe is restricted to the current
Windows user.

## Install (for users)

Run **`DrawingStudio-Setup-<version>.exe`** with Revit closed. It:

- installs the app to `C:\Program Files\Drawing Studio\App` (self-contained, no .NET install needed),
- detects every Revit 2022–2027 on the PC and installs the matching bridge add-in,
- registers it for all Windows users in `C:\ProgramData\Autodesk\Revit\Addins\20xx`,
- adds a Start-menu shortcut; uninstall from *Settings › Apps* removes everything.

Then start Revit. The first time, Revit asks whether to load **Drawing Studio Bridge**: choose
*Always Load*. From then on the bridge starts with Revit by itself.

Connecting is automatic:
- Click **Drawing Studio** on the *Drawing Studio* ribbon tab: the app opens already linked to that Revit.
- Or open the app from the Start menu first: it waits and connects as soon as Revit starts, reads the
  model once a project is open, and reconnects if Revit is restarted.
- With several Revit sessions open, pick one at the top and click Connect.

## Build the installer (for developers)

**Without a Windows build PC:** push this folder to a GitHub repository, open *Actions › Build installer ›
Run workflow*, and download `DrawingStudio-Setup` from the run's Artifacts. Pushing a tag like `v1.0.0`
also attaches the .exe to a GitHub Release.

**On a Windows PC** with the .NET 8 SDK (plus .NET 10 SDK for Revit 2027):

```powershell
.\build-installer.ps1                  # all Revit versions -> dist\DrawingStudio-Setup-1.0.0.exe
.\build-installer.ps1 -Version 1.1.0
```
Inno Setup 6 is installed with winget if missing.

`install.ps1` is still there for a quick per-user developer install without the setup wizard.

Build details: the bridge is one project built per version with `-p:RevitVersion=20xx`.
2022–2024 target .NET Framework 4.8, 2025–2026 target .NET 8, 2027 targets .NET 10.
Revit API references come from the `Nice3point.Revit.Api.*` NuGet packages, so Revit
itself is not needed to compile.

## Tabs

### Plans & sections
- **Plans per level** – tick levels, choose structural / floor / ceiling plan, view template, scale, and name pattern.
- **Sections** – three modes:
  - building sections along ticked grids (height from lowest level to top level + 3 m),
  - long sections along beams, walls, columns or footings selected in Revit,
  - cross sections through the middle of the selected elements.
  Far clip, cut offset, extent padding and looking direction are adjustable.

### Sheets
- Tick views (filter to *not yet on a sheet*), choose one sheet per view or all on one sheet, then **Add to list**.
- Numbers are generated as ISO 19650 container names, e.g. `PRJ-ORG-ZZ-01-DR-S-0001`.
  The level field is read from each view's level (`Ground` → `00`, `Level 3` → `03`, `Basement 1` → `B1`,
  `Roof` → `RF`; `ZZ` when a sheet mixes levels). Every cell can be edited before creating.
- **Create sheets** applies the title block, writes Drawn/Checked/Approved by, suitability code and revision,
  and places the views.
- Layout: views are packed left→right, top→bottom inside the ISO 5457 border (20 mm binding margin, 10 mm others)
  clear of the title strip, with detail numbers assigned in reading order.
- **Existing sheet** – add views to a sheet, or re-arrange everything already on it.

### Annotation
- **Grid dimensions** (plans): parallel grids get a chain string and an overall string just inside the grid heads,
  on one or both ends. Offsets are in paper mm and scale with the view.
- **Grid heads** at both ends.
- **Tags** for columns, beams, foundations, walls, floors, doors, windows and stair runs, skipping elements already
  tagged in that view. A tag family must be loaded for each category.

### QA & numbering
Checks: ISO 19650 sheet numbers, empty title block fields, empty sheets, view naming rule, placed views without a
template, non-ISO 5455 scales, views not on sheets, and untagged elements. Double-click any issue to open that view
or select that element in Revit.
Renumbering re-sequences ticked sheets to the standard (keeping each one's level code, with a preview first) and
resets detail numbers in reading order.

### Standards
All rules are editable and saved to `%APPDATA%\DrawingStudio\standards.json`:
container-name fields and pattern, number digits/start/step, name patterns for plans and sections, allowed scales,
sheet margins and title strip, title block values **and the Revit parameter names that receive them**.

> Set the parameter names to match your title block family. Defaults are `Drawn By`, `Checked By`, `Approved By`
> (built-in sheet parameters) and `Suitability Code`, `Revision Code` (shared parameters you may need to add).
> Anything not found is reported in the activity log rather than failing the batch.

## Name pattern tokens

| Where | Tokens |
|---|---|
| Sheet number | `{Project}` `{Originator}` `{Volume}` `{Level}` `{Type}` `{Role}` `{Number}` |
| Plan name | `{Role}` `{PlanKind}` `{LevelName}` `{LevelCode}` `{Scale}` |
| Section name | `{Role}` `{Kind}` `{Ref}` `{Category}` `{Index}` `{Scale}` |

## Project layout

```
src/
  Shared/                      linked into both projects
    Protocol.cs                commands, request/response, session discovery
    Dtos.cs                    payloads
    Standards.cs               ISO defaults and title block mapping
    Naming.cs                  tokens, level codes, ISO 19650 numbering and validation
    Json.cs                    DataContract JSON (no third-party DLLs inside Revit)
  DrawingStudio.Bridge/        Revit add-in
    BridgeApp.cs               startup, ribbon button, session file
    PipeServer.cs              pipe server, command queue, ExternalEvent handler
    Dispatcher.cs, RevitUtil.cs
    Handlers/                  ModelInfo, Views, Sheets (+ layout), Annotation, QA
  DrawingStudio.App/           WPF app (MVVM, one partial view-model per tab)
installer/DrawingStudio.iss    Inno Setup script for Setup.exe
build-installer.ps1           builds app + bridges + Setup.exe
.github/workflows/            builds Setup.exe on GitHub
install.ps1                   developer install without the wizard
```

## Known limits

- Grid dimensioning handles straight grids in plan views; arcs are skipped.
- Element sections use the location line (curved elements use their chord) or, for columns, the family's hand orientation.
- Tagging uses the default tag for each category; load the tag types you want first.
- Every command is one Revit transaction, so **Ctrl+Z in Revit undoes a whole batch**.
