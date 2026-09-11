# ProjectSplatoon Folder Structure Standard

- Project code lives under `Assets/Splatoon/`; runtime and Editor assemblies are separate.
- Addressable runtime assets live under `Assets/GameResource/`; only `Assets/Scenes/Main/Boot.unity` is a Build Settings entry.
- `Art/_Incoming` is temporary staging and must not be referenced by Prefabs or scenes.
- `Config/Luban/source` is the Excel/schema source of truth. Generated C#/JSON is rebuild-only output.
- Third-party packages keep their vendor layout under `Assets/ThirdParty` or `Assets/Plugins`.
- New asmdefs must have one-way dependencies and no circular references.
