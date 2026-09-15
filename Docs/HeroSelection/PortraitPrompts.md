# Hero selection portraits

Generated with the built-in imagegen tool on 2026-09-15. Each hero was generated independently from its own checked-in CombatGirls face reference. Source output remains in the Codex generated-images directory; the selected images are stored as 1024 × 1024 PNGs in `Assets/GameResource/UI/HeroPortraits`, with a 512-pixel Unity import limit.

## Shared generation direction

One independent square hero selection portrait. Japanese anime cel-shaded illustration, polished clean line art, restrained rich shading, readable unobstructed face. Chest-up bust, front slightly three-quarter, looking at the viewer. Preserve the reference character's hair, eyes, costume and headgear. Opaque simple deep blue-gray background with a soft muted lavender glow and subtle rim light. One character only. No gun, foreground hands, lettering, labels, UI, border or watermark.

RifleGirl was generated first to establish the painting style. Other heroes used their own model screenshot as the identity reference and that first portrait only as the painting/background reference.

| Asset | Identity specification | Name |
| --- | --- | --- |
| RifleGirlPortrait | Taupe-gray long hair with side bun, pink beret with dark cross insignia, purple-pink eyes, small cheek beauty mark, purple uniform jacket with black trim. Confident gentle smile. | 紫苑 |
| DualPistolGirlPortrait | Black cap with red brim, dark sunglasses, black hair tied back, black tactical jacket with red collar and details. Cool confident smirk. | 夜雀 |
| ShotgunGirlPortrait | Silver-white long high ponytail, large black tactical visor raised on forehead, brown-rose eyes, white collared shirt, black necktie, tactical shoulder straps. Calm confident smile. | 白凛 |
| PistolGirlPortrait | Dark short hair and side fringe, khaki tactical helmet, pale blue goggles resting on helmet above eyes, pink-gray eyes, olive-black tactical uniform. Focused composed expression. No cheek beauty mark. | 隼音 |
| RocketLauncherGirlPortrait | Silver pale-blue bob, gray-teal eyes, tactical earphones with tall angular rabbit-ear antennas and gray patterned insets, muted olive tactical uniform. Quiet confident smile. | 月兔 |
| MachineGunGirlPortrait | Orange-red spiky high ponytail, dark forehead goggles, blue-teal eyes, yellow-orange open jacket and raised collar, white high-neck shirt with dark chest panel. Energetic confident smile. | 焰橙 |

## Final framing correction

RifleGirl, DualPistolGirl, ShotgunGirl and MachineGunGirl received this targeted imagegen edit:

> Edit this exact portrait only to ZOOM OUT the composition by 25 percent, revealing the COMPLETE top of the head, all hair, hat and accessories and at least 7 percent empty background margin ABOVE the highest point. Add the missing top content naturally. Preserve this exact character identity, hairstyle, costume, expression, style, color scheme and background. IMPORTANT no headwear or hair cut off at top. Keep square canvas, resize final output to exactly 1024 x 1024 pixels. Chest-up bust, one character, no text. Do not invent a cheek beauty mark if the current character is not the pink beret girl; remove cheek dot for silver-white ponytail girl.

The returned files were technically normalized to the requested 1024 × 1024 pixel size without changing their composition. The originals were retained. Visual checks covered headgear cropping, identity, text absence and the actual 100-pixel in-game card presentation.
