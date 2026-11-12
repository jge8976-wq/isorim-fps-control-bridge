# R4 readable black iron

The R3 desert palette collapsed near-black steel and polymer into one yellow colour. R4 restores neutral black-iron/graphite tones by lifting near-black colours while retaining source differences and material roles. Wood, brass, cartridge, glass, lens, emissive and textured materials are preserved. Readable black-iron exposure defaults on and can be disabled to see the exact source colours. The old desert setting is no longer used.

The first-person-only renderer keeps the stable R3.1 view batches and cell lighting, with directional vertex shading to distinguish faces. World meshes, source material objects, global outlines and world AO are not changed. This remains a bounded first-person rendering path, not a general renderer fix.

P90 receives a dedicated held-only asset from the existing local art group. Its scale/origin, grip, muzzle and scope anchors are retained. Use the frozen art manifest and Evidence/p90-import.json as provenance; development source files and previews are not copied into the player mod.

The public API remains version 3. Registered providers precede dedicated held definitions and automatic world-model fallback. XML/raw file anchors are mapped once to the OBJ loader's Z-reflected coordinate system; C# providers default to engine coordinates. Dedicated held assets do not modify world ModelDefs or add an FPS dependency to their source model pack.

This candidate is separate from R3.1. Do not substitute structural test results for actual screenshot review, physical input acceptance or sustained CE combat. Existing startup errors and untested conditions must be reported in the final receipt. No Workshop publication is included.

Reused upstream model/material interfaces: https://github.com/peren717/IsoRim-1.7 .
