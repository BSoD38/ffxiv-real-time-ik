# FFXIV real-time foot IK — feasibility, options, roadmap, and spike plan

## Context

FFXIV does not place feet on terrain: on slopes and steps the uphill foot clips into the ground and the downhill foot floats. The idea is a Dalamud plugin that, every frame, reads the animated leg bones, asks the game's collision system where the ground is under each foot, and bends the legs so both soles rest on it. Warcry (`D:\dev\ffxiv-skill-voicelines`) is the reference for project setup and conventions. `D:\dev\ffxiv-real-time-ik` is empty; this session leaves a roadmap document and a buildable spike that proves the two real unknowns (the hook survives to render; collision height is usable) before any IK math is written.

Decisions taken with the user (2026-09-10): local player first, code shaped to extend to other characters later; deliverable is roadmap + spike; post-core priorities are slope-aligned feet, stairs/ledges, sitting/emotes. Plugin compatibility is lower priority but must never crash.

## Feasibility verdict

Feasible. Every building block has a shipping precedent in gameplay, and the novel part is glue plus ~100 lines of math.

| Need | Verified availability (FFXIVClientStructs 7.55 / Dalamud 15.0.3 / .NET 10, checked against the installed dev assemblies) |
|---|---|
| Read/write the animated pose | `hkaPose`: `ModelPose`, `LocalPose`, `ModelInSync`, `LocalInSync`, `SyncModelSpace()`, `AccessBoneModelSpace(idx, PropagateOrNot)`. `hkaSkeleton`: `Bones[].Name`, `ParentIndices`, `ReferencePose`. Path: `Character*` → `DrawObject` (check `GetObjectType()==CharacterBase`) → `Skeleton` (has `Transform` pos/rot/scale, `PartialSkeletonCount`) → `PartialSkeletons[0].GetHavokPose(0)`. |
| Ground under a point | `BGCollisionModule.RaycastMaterialFilter(origin, dir, out RaycastHit, maxDist)` static wrapper, world space, `RaycastHit.Point/Normal/Distance/Material`. `SweepSphereMaterialFilter` exists too (stairs). |
| Move the whole character vertically | `GameObject.DrawOffset` (+ `SetDrawOffset`), the field SimpleHeels uses for heel height. Fallback: CustomizePlus's `GameObject_UpdateVisualPosition` hook (sig verified below). |
| Post-animation, pre-render hook | Not in ClientStructs; signature scan. CustomizePlus `RenderManager::Render` sig (verified from their `main`), proven for every visible character in gameplay. Brio's per-skeleton `UpdateBonePhysics` sig as documented fallback only (GPose-only precedent, may run on job threads). |
| State gating | `Character.Mode` (`CharacterModes`: None=0…Performance=16; Normal, Dead, EmoteLoop, Mounted, Crafting…), `IsJumping()`, `IsMounted()`; Dalamud `ICondition` (Jumping, Mounted, Swimming, Diving, InFlight, WatchingCutscene, BetweenAreas), `IClientState.IsGPosing`. |
| IK solver | Not exposed. Own analytic two-bone solver in System.Numerics (~40 lines). Brio/Ktisis sig the Havok solvers; more fragile than the math. |

Costs that are real:
- **Signature maintenance** each patch. One constants file, hook failure logged and inert (Warcry pattern), CustomizePlus republishes sigs within days.
- **No "foot planted" tag from the game.** Height-above-rest heuristic, tunable. Core placement is vertical only, so a wrong planted guess cannot slide a foot horizontally. The two horizontal moves added later are bounded differently: gathering only moves a foot that is already hanging over an edge (since 2026-09-11 both feet are placed together: rays fan out from each ankle, and the cheapest pair of standable samples a stance apart and uncrossed wins, so no axis heuristic decides side by side against front to back), and the wall push (2026-09-11) moves a planted foot by the smallest amount that takes its footbox out of a wall, recomputed from the animation each frame rather than accumulated, so it cannot drift, and eased over 0.1 s so it cannot pop.
- **Collision ≠ visual mesh.** Stairs are often ramps in collision; some props have none. Stairs get their own milestone.
- **Access violations are not catchable in .NET.** Pointer, type and bounds guards before every dereference; `try/catch` only covers managed math bugs.

## Design

### Where the work runs

Everything (gate, raycasts, draw-offset write, IK) runs inside the `RenderManager::Render` detour on the main thread, once per frame, with a Stopwatch `dt`. Edits are written **before** calling `Original` (as CustomizePlus does). Nothing is done in `Framework.Update` except in the M0 spike (read-only overlay).

```
RenderHookAddress   = "E8 ?? ?? ?? ?? 48 81 C3 ?? ?? ?? ?? BF ?? ?? ?? ?? 33 ED"   // RenderManager::Render
MovementHookAddress = "E8 ?? ?? ?? ?? 84 DB 74 3A"                                  // GameObject_UpdateVisualPosition (fallback only)
delegate nint RenderDelegate(nint a1, nint a2, nint a3, int a4);
```
Resolve with `ISigScanner.TryScanText` (these are `E8` call sites, ScanText resolves the target) and `IGameInteropProvider.HookFromAddress`.

Failure containment: `Original` always called and never inside our try; detour body = `if (armed) try { Tick(); } catch { armed=false; faults++; log once }`; guards before every deref (Character, DrawObject, object type, Skeleton, `PartialSkeletonCount>0`, pose, `pose.Skeleton`, `ModelPose.Length == Bones.Length`, every index in range); `float.IsFinite` on every output before any write, else skip the frame; breaker re-armed only from the UI; `Dispose` unhooks **and** removes our draw-offset delta so the character is not left sunk, and the breaker tripping removes it too (a tripped Tick never reaches the offset write again).

## Feature options and recommendation

| Option | Recommendation | Why |
|---|---|---|
| Local player vs all characters | Local first; `Tick` written over a list of targets from day one | Extension is an object-table filter, a radius, per-character state, and a frame budget |
| Own solver vs game Havok solver | Own analytic two-bone | 40 lines, no sig, no struct-layout risk |
| Foot raise only vs pelvis drop | Both; pelvis drop is its own milestone via `DrawOffset` | Raise-only fixes clipping but not floating |
| Foot rotation to normal | M4, ±30° clamp, weighted by `p·w` | User priority; cheap once write-back works |
| Stairs/ledges | M5: heel/toe rays per foot, hysteresis, sphere sweep if needed | User priority; collision ramps mean partial wins |
| Sitting/emotes | M6: `EmoteLoop` whitelist; ground-sit is a root tilt to the normal, not leg IK | User priority; different problem than standing |
| Other players/NPCs | M7, opt-in radius + budget | Same code path plus per-character state |
| Compat (CustomizePlus, SimpleHeels, Brio/Ktisis) | GPose gate from M1; `DrawOffset` delta-only writes; observe CustomizePlus ordering in M7 | Must not crash; cooperation later |
| Distribution | Self-hosted third-party repo `github.com/BSoD38/dalamud-plugins` (shared with Warcry; the pluginmaster URL is permanent once users add it), csproj is the manifest. Release = `dotnet build -c Release`, then `publish.ps1 FootIk\bin\Release\FootIk` in that repo | No D17 constraints |

Skipped: velocity-based plant detection (add only if M3 shows a pop height gating cannot explain), hand/arm IK, syncing results to other players over IPC, per-character profiles.

## Roadmap

- **M0 — Overlay + raycast, no hook (spike part 1).** Read the chain in `Framework.Update` (read-only; last frame's pose is fine), raycasts, world dots, `DebugDrawOffsetY` slider.
- **M1 — Hook + constant knee bend (spike part 2).** Install the render hook; `DebugKneeBendDeg` rotates one knee in the detour. Proves edits survive to render.
- **M2 — Pelvis drop.** Done (2026-09-10) via `GameObject.SetDrawOffset`, confirmed in game; the movement-hook variant stays as an option.
- **M3 — Two-bone raise.** Done (2026-09-10). Terrain height and animation lift are kept apart: feet shift by the terrain under them, the pelvis follows terrain only (keeps run bounce), legs straighten before the pelvis drops, knee angle cap, penetration clamp. Thresholds are fractions of bind-pose leg length.
- **M4 — Foot alignment.** Done (2026-09-10). Normal derived from the hit triangle (the result's `Normal` field reads zero), additive on the animated ankle, capped, fades with height above rest so heel strike keeps its own pitch.
- **M5 — Stairs/ledges.** Done so far: three probes per foot (heel, mid, toe), median height. Tried and reverted (2026-09-10): "higher hit wins" (floats the foot as the toe probe reaches the next tread) and "prefer flat hits over ramps" (did not help on beveled stairs). Open: many staircases are ramps or bevels in collision; see M8. Open (2026-09-10): on natural slopes the feet land ~10 cm below the visible ground, symmetric uphill/downhill, while flat ground is exact. Ruled out: draw-position sink (skeleton transform equals the logical position), root-bone or n_hara pose shift, probe-vs-ankle reference (fixed by evaluating the hit plane under the ankle). Leading suspicion: the collision surface sits below the visual terrain there. Interim: the "slope lift" knob adds raise proportional to slope tangent, as a fraction of leg length like every other distance. Investigate with M8.
- **M5b — Edges and narrow supports.** Done (2026-09-11), in-game verified: feet hanging over an edge gather onto the support (joint radial search from both ankles, cheapest uncrossed pair with room between the boots, hold while still, re-plant only when a turn crosses the legs), feet kept out of walls by a boot-sized box, body centred over the feet through the movement hook, leg balance across heights, a minimum stance sideways and a boot length lengthwise, shallow water looked through to the bed, no gathering while the gate is closed (a fall is flagged as jumping). Open: on a rail the feet must swap ends once per half turn; unavoidable without crossed legs.
- **M6 — Sitting/emotes.** Done (2026-09-11), awaiting in-game verification. Whitelist: the gate opens for `CharacterModes.EmoteLoop` as a whole, because the `EmoteMode` sheet gives `ConditionMode` 3 to every looping emote except rows 1–3 (ground sit, chair sit, sleep), which get 11 = `InPositionLoop`; the row id is assumed to arrive in `Character.ModeParam` (⚠ read it off the Status tab). Seated (1 or 3): the leg pass fades out through the ordinary blend, and the whole model-space pose, face and hair partials included, turns about the origin until its up matches the ground plane through four probes half a leg length out (front, back, left, right), capped by `MaxSitTiltDeg`, eased by `SitTiltTau`, and weighted by hip height (nothing above half a leg length, full below a quarter) so the sit-down animation settles onto the slope instead of tilting while still upright. A chair (2) closes the gate: the character is on furniture, not the ground. Open: `/playdead`, `/pushups`, `/situps` are `EmoteLoop` and get leg IK while lying down; `/doze` on a bed probes the floor under the bed; whether attached weapons follow the tilt.
- **M6b — Root motion.** Written (2026-09-11), awaiting in-game verification. Dances that carry the root far from the origin (`/tdance`) broke the IK up or down a slope: every step threshold, the pelvis caps and the probe window were relative to the ground at the logical position, while the feet stood on ground far above or below it, so both feet read as a step too tall or as an edge, and the probe start sat below the terrain uphill. Heights are now judged against the ground under the hip midpoint (`Frame.BaseY`, one extra probe per frame; when the window misses, a retry from 1.5 leg lengths up, the character's own headroom, so a bridge overhead is never taken for the floor). The body follows that baseline in full and `LegBalance` shares only the spread between the feet around it; the probe window for every foot and gather probe moves with it. `GroundModelY` stays origin-relative, so world heights and the pelvis arithmetic are unchanged.
- **M7 — Other characters + release.** Object-table iteration, budget, compat observations, `repo.json`.
- **M8 (experimental) — Render-mesh raycasting.** Probe the visual level geometry instead of, or as a refinement over, the collision mesh, to fix staircases that are ramps or bevels in collision. Scope: enumerate placed background objects and their transforms from the zone layout, load `.mdl` through Lumina, extract triangles, build a spatial index, keep it in sync with streaming and housing, and filter out non-walkable decoration (the hard part: collision already encodes walkability, the render mesh does not). A large subsystem with per-patch maintenance; attempt only once the collision-based version is otherwise solid, and prefer a hybrid (collision for gating, render mesh for the final height) if it goes ahead.

## Code layout (2026-09-10)

```
FootIk\FootIk.csproj      # Sdk="Dalamud.NET.Sdk/15.0.0"; the csproj is the manifest; no NuGet refs
FootIk\Plugin.cs          # services, both hooks (sigs + fault breaker), detours, body offset (SetDrawOffset / movement hook), lifecycle
FootIk\Plugin.Tick.cs     # the per-frame pass as named steps over one Frame context: gate → ProbeFoot → GatherFeet → PushFromWalls → ArrangeStance
                          #   → MeasureFoot → pelvis → PlaceFoot → LeanSpine → TiltBody
FootIk\Plugin.Bones.cs    # ResolvePose (all pointer guards), SolveLeg (two-bone + subtree propagation), ApplySpineLean (+ partial re-sync), RotateBody (whole pose about the origin)
FootIk\Plugin.Probes.cs   # Raycast with exposed layer/material filter, TrySupport, GroundAt (plane from the hit triangle)
FootIk\Solver.cs          # pure math: Xf, Compose/Relative, FromTo, TwoBone, Pick, SelfTest
FootIk\LegChain.cs        # bone lookup by name, subtrees, bind-pose rest height / leg length / forward; Havok read/write helpers
FootIk\Settings.cs        # all tunables (fractions of leg length where they are distances); IPluginConfiguration, saved on edit
FootIk\Snapshot.cs        # per-frame readouts for the overlay
FootIk\Overlay.cs         # /ik window: Feet / Ankles / Edges / Lean / Status tabs, Advanced accordions, world markers
.gitignore                # bin/obj + the FootIk.json tripwire
docs\PLAN.md              # this document
```

The spike diagnostics are gone (2026-09-11): `Bypass`, `Run from Framework.Update`, and the two debug offset sliders. They proved the hook ordering and the draw-offset mechanism, and both facts are recorded above. Toggling `Enabled` off is the A/B now.

Build: `dotnet build FootIk/FootIk.csproj -c Release`; dev-load `FootIk\bin\Release\FootIk.dll` via `/xlplugins`.

## Verification in game

M0:
1. `dotnet build` clean; dev-load via `/xlplugins`. Debug tab shows 10 leg bones resolved and the skeleton transform.
2. Green dots sit on the ankles idle and running; red dots on the floor; delta ≈ 0 on flat ground, non-zero with the right sign on a slope and on stairs. Read how collision models the stairs.
3. `DebugDrawOffsetY = -0.2` sinks the whole character including hair, face, weapon and minion; back to 0 restores. If nothing moves, the fallback movement hook is the M2 mechanism.
4. Mount, jump, swim, GPose: gate reads closed.

M1:
5. Hook installed (status green). `DebugKneeBendDeg = 30` visibly bends the knee every frame with no flicker in normal gameplay; the same write from `Framework.Update` is invisible (proves ordering). Hair, cloth and weapon unaffected. `ModelInSync == 1` at hook time.
6. Survive zone change, gear swap, GPose in/out, `/xlplugins` reload, logout, toggling Enabled off. Frame cost under 100 µs.


M6:
7. Status tab while `/groundsit`: `Mode InPositionLoop (1)`, `Sitting True`; `/doze`: `(3)`; on a chair: `(2)`, `Sitting False`, gate closed. A standing looping emote (`/lean`, any dance): `Mode EmoteLoop`, gate open, feet placed as when idle.
8. `/groundsit` on a slope: the body settles onto the slope over the sit-down animation, `body tilt` reads the slope angle up to the cap, seat and knees both on the ground; facing uphill leans back, downhill forward. Standing up eases the tilt back to zero. Flat ground reads 0.
9. Face, hair, sheathed weapon and gear follow the tilt.
10. `/tdance` (or any dance that travels) on a steep slope, uphill and downhill: `Ground under body` on the Status tab follows the terrain under the dancer, the body rides up and down with it, the feet stay on the ground with no leg folded to its limit and no float. Same dance on flat ground: `Ground under body` stays near 0 and nothing changes from before. Idle and running on a slope: unchanged.
## ⚠ Settled during the spike, not before

- Whether the game consumes `GameObject.DrawOffset` every frame in gameplay (M0 step 3) or the movement hook is needed.
- Whether `ModelPose` writes via `AccessBoneModelSpace` are enough (M1 step 5). Fallback: also write `LocalPose[k] = Relative(parentModel, childModel)` for the chain.
- `hkQsTransformf` component layout (translation Vector4 with w=0, rotation xyzw, scale Vector4): print in the Debug tab and eyeball.
- Exact `CharacterModes` member names: settled, `Normal = 1`, `EmoteLoop = 3`, `InPositionLoop = 11` (dumped by reflection 2026-09-11).
- Whether `Character.ModeParam` carries the `EmoteMode` row id while `Mode == InPositionLoop` (M6 step 7). If not, the seated path never triggers.
