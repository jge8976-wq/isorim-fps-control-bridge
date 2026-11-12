# R2: first-person outlines and aiming

Private candidate, package `hsk.fpsbridge.heldmodels.r3`. R1 remains preserved. No Workshop publication.

First-person-only model copies use IsoRim's public `ObjLoader.WithoutSmallOutlines`, a unique model id and a cache per original model. Positions, normals, UVs, colours and materials are retained. Original held/dropped models and IsoRim's global outline setting are not modified. The FPS setting **Dark outlines on first-person held models / 第一人称手持模型显示深色描边** defaults to off. Turning it on permits normal outlines, subject to IsoRim's global switch.

44 ranged weapons: 29 iron observation alignments, 13 optical/holographic templates and two visible-gun reticle fallbacks (sawed-off and Boys rifle). Five melee weapons and two throwables retain their poses. Authored IronRear/IronFront, ScopeEye and OpticEye anchors take priority. Named OBJ components provide missing Uzi/Judge sights and P90/laser optical points. A central mesh-plane audit raises observation lines just above solid sight housings without editing geometry; solid hoods use their upper silhouette rather than a nonexistent hole. The Boys rifle's authored line intersects large solid geometry, so its unusable irons explicitly fall back to the reticle. Alignment does not establish ballistic zeroing at arbitrary ranges.

Optical profiles: Henry Big Boy 2.5x VintageFine; M1903 3x VintageFine; SRS/SR25/Tac50 4x ModernPrecision; Kord/RPK16/MG338/M7/M250 2x ModernCombat; CP33/P90 1.5x ModernCombat; laser carbine 1.5x SpacerHolo. FOV limits apply. Optical mode uses existing FPS scope templates rather than rendering through model glass.

API v2 adds optional `scopeEye`, `scopeForward`, `scopeZoom`. `scope="Auto"` uses known weapon rules or the supplied eye and weapon category. Explicit `None` selects irons/reticle. Registration remains compatible. XML uses `hasScopeEye`, `scopeEye`, `scopeForward`, `scopeZoom`. Coordinates/directions are raw model space. Invalidate when a live provider changes its model or sights.

Native verification lives under `Playtests/FirstPerson-Held-Models-20261104-R3`. It checks original geometry/material references, unique outline ids, zero outline batches for the view copy, unchanged world/global settings, and centred iron/optical axes. Screenshots include outline before/after, iron poses, optical masks/zoom and reticle fallback. Automated game frames do not certify physical input or sustained combat.

Native2 passed 159 checks and produced 12 complete game PNGs. Native1 is preserved: core checks passed but an asynchronous screenshot write collided with the helper's reader. The reader now retries IOException. Native2 includes the final mesh-clearance calibration. API/provider and core-model fallbacks passed inside the CE scene; a separate NoCE game and full CE combat were not rerun. Original dark material values remain unchanged.
