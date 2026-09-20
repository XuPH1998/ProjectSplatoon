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


## 2026-09-20: nine distinct hero portraits

All nine selectable heroes now have independently redrawn portraits, generated using the built-in imagegen tool. Existing portraits were identity/style references; SplooshGirl used RifleGirl as the shared identity reference with explicit chibi proportions. Original outputs remain in the Codex generated-images folder; installed PNGs are normalized to 1024 x 1024 without cropping. Existing asset GUIDs and import settings are preserved. BubbleShotgunGirl has a new portrait asset and Addressables entry; TbHero.xlsx is the authoritative source of its new address.

### Prompt template

```text
Use case: stylized-concept. Create ONE replacement hero-selection portrait, exactly 1024x1024 square PNG. Reference image is identity and illustration style reference, NOT a pose template. Keep this character's hair color, hairstyle, eyes, headwear, clothing, costume colors and recognizable identity. Redraw into this NEW pose and expression: {pose} Polished Japanese anime cel-shaded illustration, clean line art and rich restrained shading consistent across a nine-hero set. Upper torso/chest-up portrait, face prominent and readable at 100px thumbnail size. Keep entire head/hair/hat and all headgear inside canvas with at least 7% clear top margin, shoulders and relevant gesture visible. Opaque simple deep blue-gray background, muted lavender halo, soft rim lighting. One character only, no text, letters, names, numbers, watermark, UI, border, or weapons. Hands anatomically correct. Do not merely mirror old portrait.
```

### Per-hero pose substitutions

- **紫苑 (RifleGirl)**: Upright composed leader pose, torso turned 30 degrees to image LEFT, chin level, steady direct gaze and subtle confident closed-mouth smile, one hand resting over upper chest.
- **夜雀 (DualPistolGirl)**: Low camera angle, torso turned 40 degrees to image RIGHT, chin raised with a cocky asymmetric grin, one gloved hand touching the brim of her cap, sunglasses stay on.
- **白凛 (ShotgunGirl)**: Cool stern expression with closed unsmiling lips, torso facing image LEFT at 50 degrees, eyes glancing back at viewer, shoulders square, arms folded visibly at bottom. Dramatic disciplined silhouette.
- **隼音 (PistolGirl)**: Alert scout, almost side profile facing image RIGHT at 60 degrees, focused serious eyes looking off-frame right, slight forward lean, one gloved hand raised beside helmet as a listening gesture.
- **月兔 (RocketLauncherGirl)**: Thoughtful quiet expression, head tilted slightly, three-quarter toward image LEFT, eyes gazing upward left, one gloved index finger gently under chin. Both tall rabbit-ear antennas entirely visible.
- **焰橙 (MachineGunGirl)**: Energetic broad open-mouth laughing smile, front-facing head tilted back slightly, torso twisting right, one bent arm with celebratory clenched fist beside shoulder; bright determined eyes.
- **沫澜 (BubbleGirl)**: Gentle playful personality, torso turned toward image RIGHT at 35 degrees, head inclined toward viewer, eyes open soft cheerful smile, open upturned palm at lower left with two small translucent cyan bubbles above it, no face overlap. Clearly different from a stern crossed-arms soldier.
- **铃芽 (SplooshGirl)**: Distinct super-deformed CHIBI version of this same character: large round head, tiny shoulders, 2.5-head overall proportions implied by close bust framing, big purple eyes, playful one-eye wink and open happy smile, head tilted toward image RIGHT, small V-sign hand beside cheek without hiding face. Keep pink beret, taupe hair side bun, purple jacket.
- **泡霰 (BubbleShotgunGirl)**: Bold mischievous personality: body turned away toward image RIGHT, face looking BACK over shoulder toward viewer, chin slightly lowered, one eyebrow raised and a cheeky toothy grin, high ponytail swinging LEFT, one gloved hand gripping shoulder strap. Strong over-shoulder silhouette, no folded arms, no bubbles.

### Validation and maintenance

- Reviewed all nine full images and a 100px thumbnail strip: distinct expressions/poses, complete headwear, readable faces, no text in assets.
- Source workbook changed only Hero!AH12 in cell values; Luban generation updates only the corresponding portraitAddress in tbhero.json.
- Verified nine unique addresses, GUID mappings, PNG hashes and 1024 x 1024 dimensions.
- Runtime uses HeroContentService to load Texture2D by HeroConfig.PortraitAddress; hero selection draws the loaded portrait with ScaleToFit.
- SplooshGirlBuilder writes calibration renders under Reports/SplooshGirl, preserving the authored UI portrait on rebuild.
- Overview: Reports/HeroPortraitRefresh/overview.png; thumbnail strip: Reports/HeroPortraitRefresh/thumbnails.png.
- Unity Play Mode and packaged-build visual acceptance were not run in this pass.
