# Camera / reticle baseline fixture

`baseline-camera.json` records the nine presentation profiles from commit
`561085918c39d8f8dd08cc0e593600b98049c0d0`. It is historical evidence, not live tuning.
The historical floor-range tests intentionally use these camera inputs; current
weapon parity is checked by `WeaponImpactPredictionTests` for all nine heroes.

Editor commands / request files:

- Menu: 喷墨对战 / 相机 / 校准全部角色构图. Writes measured profile values and `Reports/CameraReticle/camera-parameters.csv`.
- `Temp/CameraReticle/capture`: capture current training-room states/resolutions and isolated baseline reconstruction. See `After/status.txt` for completion, not just PNG existence.
- `Temp/CameraReticle/build`: temporary development Player in `Temp/CameraReticle/Player`, not a release package.
- `Tools/CombatGirls/run_sploosh_acceptance.ps1 -PlayerPath Temp/CameraReticle/Player/InkLan.exe -OutputDirectory Reports/CameraReticle/Network -CameraAcceptance`: two local processes, actual commands/projectiles/feedback and optional camera probe.

Baseline screenshots reconstruct the old camera, FOV 65 and reticle only. The
other HUD is intentionally omitted in that isolated reconstruction. They are
not original archived screenshots of an old executable. New screenshots render
the current game and HUD. Captured state poses are not an input-recorded video.
