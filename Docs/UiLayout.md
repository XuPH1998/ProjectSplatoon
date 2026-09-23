# Combat and loadout UI

The September 23 reference images are the visual target: full portrait cards, a selected-character banner, navy translucent gradient panels, cyan selection glow, ink accents, pink/blue slanted score wings, outlined keycaps and a thick special-charge ring. Gameplay backgrounds and character models are unchanged. Text, equipment, territory, health, ink and ability state remain live data, not baked screenshot content.

## Rendering and layout

- `PrototypeApp.UiTheme.cs` owns reusable rounded gradients, shadows, keycaps, artwork treatment, HUD framing and uniform screen mapping. `PrototypeApp.UiGlyphs.cs` draws atlas icons, ring progress and small geometric symbols.
- The 1280x720 layout scales uniformly. Edge HUD follows the viewport edges; modal UI remains centered. World name/mark positions use the inverse centered transform. The existing reticle keeps its separate screen projection and round aspect.
- All nine heroes fit in the portrait viewport. More rows scroll inside it, leaving the right panel and footer fixed. Preview/current badges are separate from the actual server-approved loadout. Sub/special tabs preserve selections and only the confirmation button submits.
- No network schema, balance, weapon simulation or Luban files change.

## Generated component atlas

`Assets/Splatoon/Resources/CompetitiveUi/Icons.png` is a 1402x1122 RGBA texture generated with the built-in imagegen tool, using the approved hero-selection reference for icon style. Its outside/gutter alpha is zero. It is imported without mipmaps or lossy compression and sampled as five columns by four rows.

Rows: (1) splat, suction, burst, curling, autobomb; (2) fizzy, torpedo, mine, sensor, mist; (3) marker, sprinkler, wall, trizooka, triple tornado; (4) sonar, rain, shark, cyan ink splash, pink ink splash. The compact HUD grenade has its reference-style pink fill; other skill icons use this atlas.

Generation prompt: Create a production game UI component sprite atlas with exactly five equal columns and four equal rows, one centered icon in each cell, generous padding, no frames, backgrounds, words or numbers. Use bright off-white stylized ink-shooter silhouettes with subtle dark navy edging, clean thick vector-like contours matching the reference's white weapon icons. In row-major order: triangular splat grenade; suction-cup hourglass bomb; round fuse bomb; curling stone with handle; walking robot bomb; fizzy soda-can bomb; flying fish torpedo; disk proximity ink mine; circular crosshair sensor; poison gas cloud; diagonal marker projectile; sprinkler; grid ink wall; triple-barrel launcher; three tornado swirls; sonar pole with waves; rain cloud; shark ride; cyan paint splash; pink paint splash. Require a genuinely transparent alpha background, distinct silhouettes, no checkerboard, no labels, no characters. Reference is for icon style only.

## Validation

Run **Splatoon > Validation > Combat and loadout UI** in an idle editor with saved scenes. The runner protects unsaved scenes, executes focused geometry/portrait checks, then real IMGUI and Input System interactions at 1280x720, 1920x1080 and 2560x1080. Reports are written to `Reports/UiLayout/results.xml` and `Reports/HeroSelection/ui-*.txt`; screenshots are adjacent to the latter. Static geometry, compilation, actual input assertions and visual screenshot review are separate acceptance checks.

Final run on 2026-09-23: **9 passed, 0 failed, 0 skipped**. All three resolution logs report `passed=True` with no error. Coverage includes all nine portraits, thirteen sub-weapons and five specials, preview/confirmation/cancel, tab persistence, UI click isolation, dead-state disable, warmup/spawn/debug access, low ink, special failure/activation, held/released Splatling fire, friendly rescue/enemy execution and downed/death/respawn HUD. Editor compilation and a separate player-assembly compile passed; `git diff --check` passed.

Reviewed screenshots include all three resolutions. Final 1920x1080 snapshots are retained at [Combat](UiLayout/Combat-1920x1080.png) and [Loadout](UiLayout/Loadout-1920x1080.png); the corresponding Unity result is [Validation.xml](UiLayout/Validation.xml). These are real runtime captures with the project's existing scene and portraits, not the concept-image background or characters. The checks establish functionality and layout; visual resemblance is shown by the screenshots, not by an automated pixel-equality claim.
