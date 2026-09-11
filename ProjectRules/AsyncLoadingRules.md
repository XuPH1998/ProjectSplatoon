# Async Loading Rules

- Boot initializes Addressables before reading configuration or loading gameplay scenes.
- Every load supports cancellation, progress, failure reporting and deterministic release.
- The caller owns each Addressables handle until unload completes.
- Normal runtime code does not use `Resources.Load`, `AssetDatabase` or direct filesystem reads.
