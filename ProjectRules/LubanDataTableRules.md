# Luban Data Rules

- Excel in `Config/Luban/source` is the sole data authority.
- Run `cmd /c Config\\Luban\\gen_luban.bat` after source changes.
- Never hand-edit `Assets/Splatoon/Config/Generated` or `Assets/GameResource/Bootstrap/Config/Luban`.
- Runtime accesses the generated table cache only after the centralized config service is ready.
