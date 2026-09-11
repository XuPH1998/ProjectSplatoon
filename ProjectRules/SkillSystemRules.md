# Skill System Rules

- `SkillDefinition` is immutable runtime data; editing happens through the Editor assembly.
- Runtime skill playback communicates through `ISkillEventHandler` adapters.
- Editor validation must reject null clips and non-positive durations.
- JSON import/export is an authoring utility and must preserve the definition data.
