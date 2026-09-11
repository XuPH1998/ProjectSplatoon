# ProjectSplatoon

Unity project built with Unity **6000.3.9f1**.

## Getting started

1. Clone this repository.
2. Add the project folder in Unity Hub.
3. Open it with Unity 6000.3.9f1 and allow Unity to import the assets.

The repository includes `Assets`, `Packages`, and `ProjectSettings`. Unity regenerates local caches and IDE project files when the project is opened.

## Framework entry points

- Boot scene: `Assets/Scenes/Main/Boot.unity`
- Runtime code: `Assets/Splatoon/`
- Addressable runtime content: `Assets/GameResource/`
- Luban source and generators: `Config/Luban/`
- Excel merge tool: `Tools/ExcelMerge/`
- Project conventions: `ProjectRules/`

Open the project in Unity, run **Project Splatoon/Config/Setup Addressables Root**, then let the Package Manager resolve Addressables, NGO and Unity Transport. Run `cmd /c Config\\Luban\\gen_luban.bat` after changing source workbooks.
