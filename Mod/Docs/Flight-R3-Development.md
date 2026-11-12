# Air Flight R3

Private successor to the user-accepted R2 helicopter prototype. The R2 candidate and production FPS DLL remain untouched.

The private launcher automatically loads Flight-Ready-R3, or loads the isolated template and creates the five-aircraft scene on first run. No debug action is required. Scene preparation is protected by a command-line flag, a dedicated profile path and marker; ordinary saves do not execute it.

Controls: Space ascend / pitch up, left Ctrl descend / pitch down, double-tap left Ctrl within 0.34 s requests assisted landing. An optional landing binding is in the flight key category. Release held inputs after requesting landing; a new movement input returns control. Landed engines stop normal fuel burn through the original VF fuel component. Damage leaks remain native.

Rotorcraft use continuous yaw and hovering. Pods use lower-speed lateral thrusters without banking. Fixed-wing aircraft use persistent throttle, takeoff speed, forward flight and unpowered glide; assisted landing requires a clear runway. This is a simplified flight prototype, not a full aerodynamics model.

Mosquito's separate rotor mesh rotates around its scaled local pivot. Exhaust uses the established IsoRim material/draw path and a small procedural cone with explicit white vertex colors. No source vehicle models are replaced. All effects are cosmetic.

Architecture: per-vehicle saved pose and throttle; true-space body/mount/camera/hit geometry; horizontal native map projection with R2 discontinuity guard. Landing checks hull clearance and support, slabs/roofs, buildings, trunks, nearby actors and runway/approach samples. Keyboard and simulation clocks are separate; native regression drives the same Advance method as normal controls.

Validation: build and native API signature compilation; 36 pure air/landing/gesture/geometry cases; 2847 legacy control/ballistics contracts; existing CE rule runner. Native runtime receipts are under Evidence and include the exact candidate hash. Only claims supported by the final receipt are accepted: simulated movement is not real keyboard acceptance, render submissions are not pixel acceptance, and vanilla air checks do not establish CE flight compatibility.

References inspected for reuse:
- https://github.com/SmashPhil/Vehicle-Framework/blob/develop/Source/Vehicles/Comps/Flyer/CompVehicleLauncher.cs
- https://github.com/Skylon3/Aerocraft-framework-1.6fix-unfork/blob/master/CompProperties_MoveToTargetAndHover.cs

Reuses Vehicle Framework vehicle roles, fuel ownership and world launch behavior; does not bundle upstream DLLs or transplant a second flight framework.
