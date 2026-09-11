# Inverse Kinematics — FFXIV Dalamud Plugin

## Project Context

Inverse Kinematics (assembly and folder `FootIk`) is a Dalamud plugin for Final Fantasy XIV that plants the local player's feet on level geometry. Every frame it reads the animated leg bones, raycasts the game's collision under each foot, and bends the legs — plus drops or raises the body and leans the spine — so both soles rest on the ground instead of clipping into a slope or floating above it.

`docs/PLAN.md` is the verified technical plan: feasibility table, hook signatures, milestone log, and **what has already been tried and reverted**. **Read it before re-researching anything.** Items marked ⚠ in it are genuinely unverified.

Product decisions that bound the design space (do not re-litigate):

- **Local player first, shaped for more.** `Tick` reads `Objects.LocalPlayer`, but per-foot state is arrays and the steps take a `Frame`; extending to other characters (M7) is an object-table filter, a radius and a budget — not a rewrite.
- **Own analytic two-bone solver**, not the game's Havok solvers. Forty lines of `System.Numerics` beats another signature to maintain.
- **Distribution: self-hosted third-party repo**, csproj as the manifest. DalamudPluginsD17 review constraints are not binding.
- **Never crash, at any cost in features.** Compatibility with CustomizePlus / SimpleHeels / Brio is a lower priority than not taking the client down.

Milestone state (2026-09-11): M0–M4 and M5b done and in-game verified. M5 (stairs/ledges) partial — three probes per foot, median height; the slope sink is open and has a `SlopeLift` interim knob. M6 (sitting/emotes) is written and awaits in-game verification: `EmoteLoop` opens the gate, ground sit and sleep (`InPositionLoop`, `ModeParam` 1 and 3) tilt the whole pose to the ground plane. M7 (other characters + release) and M8 (render-mesh raycasting, experimental) are not started.

Load-bearing invariants:

- **All work happens in the `RenderManager::Render` detour, before `Original`.** Edits made from `Framework.Update` do not survive to the rendered frame; this was demonstrated in game with a diagnostic toggle that has since been removed.
- **Guards before every dereference.** An access violation is not catchable in .NET; `try/catch` only covers managed math bugs. `ResolvePose` owns the pointer, object-type and bounds checks, and nothing else dereferences game memory unchecked.
- **`Original` is always called and never inside the `try`.** The fault breaker counts managed faults, trips inert after 5, and is re-armed only from the UI.
- **`DrawOffset` writes are delta-only.** SimpleHeels and friends write the same field; we track `written` and only ever add or remove our own delta. **`Dispose` removes it**, so the character is never left sunk.
- **Y through `SetDrawOffset`, X/Z through the movement hook.** The game only honours the Y component of the draw offset.
- **Distances are fractions of the bind-pose leg length** (`*Frac` settings), so every race behaves alike. No hardcoded metres.
- **`float.IsFinite` before any write to the pose** — skip the frame rather than write a NaN into the skeleton.
- **`RaycastHit.Normal` reads zero in game.** Ground height and normal come from the hit triangle (`V1/V2/V3`) via `GroundAt`.
- **Settings save when an edit finishes, never while it is in progress.** `SavePluginConfig` writes the file synchronously on the draw thread, so sliders save on `IsItemDeactivatedAfterEdit`, not on change. `Settings.Repair()` replaces non-finite floats at load: a NaN read from a hand-edited file passes every `<`/`>` guard downstream.

## Tech Stack

- **.NET 10 / C# 14** via **`Dalamud.NET.Sdk/15.0.0`** — the SDK supplies `net10.0-windows`, LangVersion 14, x64, nullable, unsafe blocks, the lock file, DalamudPackager and the Dalamud references. **Do not set any of these in the csproj.**
- **FFXIVClientStructs** (via the SDK) — `hkaPose`/`hkaSkeleton`, `BGCollisionModule`, `Character`/`GameObject`.
- **ImGui** via Dalamud windowing for the `/ik` overlay.
- **Zero NuGet references.** No database, no auth, no web stack, no test project — verification happens **in game** (see Workflow). `Solver.SelfTest()` is the only automated check and it runs at load.

## Architecture

Single project, single namespace. One `partial class Plugin` split by concern, plus pure helpers:

```
FootIk/FootIk.csproj      # Sdk="Dalamud.NET.Sdk/15.0.0"; the csproj IS the manifest
FootIk/Plugin.cs          # services, both hook signatures + install, detours, fault breaker,
                          #   ApplyPelvis (draw offset / movement hook), lifecycle
FootIk/Plugin.Dispatch.cs # who gets ticked: local player, object-table sweep, eligibility, retirement
FootIk/Plugin.Tick.cs     # the per-frame pass: a ref struct Frame threaded through named steps
FootIk/Plugin.Gather.cs   # GatherFeet and its search: sampling, picking, holding, recentring
FootIk/Plugin.Bones.cs    # ResolvePose (the guard chain), SolveLeg, ApplySpineLean, RotateBody
FootIk/Plugin.Probes.cs   # Raycast (explicit layer/material filter), TrySupport, GroundAt
FootIk/Solver.cs          # pure math: Xf, Compose/Relative, FromTo, TwoBone, Pick, SelfTest
FootIk/LegChain.cs        # bone lookup by name, subtrees, bind-pose rest / leg length / forward,
                          #   Havok transform read/write
FootIk/Settings.cs        # all tunables; IPluginConfiguration, saved when a widget edit completes
FootIk/Snapshot.cs        # per-frame readouts, written once per tick, read on the draw thread
FootIk/Overlay.cs         # /ik window + world markers
docs/PLAN.md              # the source of truth for research, offsets and milestones
```

Per-frame pipeline in `Plugin.Tick.cs` (`TickOne`), reached once per character from `Plugin.Dispatch.cs`:

```
gate (Mode / jump / conditions / GPose) → ResolvePose → ReadTransform
  → ProbeFoot → GatherFeet → PushFromWalls → ArrangeStance → MeasureFoot
  → ApplyPelvis → PlaceFoot → LeanSpine → TiltBody
```

Dependency direction: `Dispatch` picks the characters and `TickOne` orchestrates each, and every game-memory read in it (character fields, skeleton transform, bone translations via `Bones.Pos`) sits behind `ResolvePose` or the local-player check; `Bones`/`Probes` own the pointer walks, pose writes and raycasts; `Solver` and `LegChain` are pure and hold no plugin state; `Overlay` reads `Snap` and `Settings` and owns nothing.

### Manifest rule (critical)

The csproj **is** the plugin manifest (Name, Punchline, Description, Tags…). **NEVER create `FootIk/FootIk.json` or `FootIk/FootIk.yaml`** — DalamudPackager takes json > yaml > csproj, first hit wins with no merge, so either file silently replaces every manifest property. Both are in `.gitignore` as a tripwire. (`bin/*/FootIk.json` is generated output; that one is fine.)

## Coding Standards

- **File-scoped namespace, single root namespace `FootIk`**, no folders.
- **`this.` on every instance member access**, braces on every block including one-line `if`s. The codebase is uniform on this; keep it.
- **The column alignment of the `[PluginService]` block in `Plugin.cs` is deliberate.** It is the only thing `dotnet format` disputes in the whole project (14 WHITESPACE errors, lines 27–34, all in that block). Do not let a blanket format pass collapse it, and do not "fix" those errors.
- **Explicit usings**, ordered System → Dalamud → FFXIVClientStructs. No global usings.
- **`sealed`** on concrete classes; `Plugin` is `sealed unsafe partial`.
- **Plain mutable structs for per-frame data** (`Frame`, `Snapshot`, `FootSnapshot`, `Xf`, `LegChain`) — scratch state read by the overlay, not value objects. Don't convert them to records.
- **Comments are rare and state a constraint**, never narration: a game-client fact (`[PluginService]` is `AttributeTargets.Property`; the allocator reuses skeleton addresses), a trap, a reverted experiment, or a `docs/PLAN.md` reference. Prefer a clearer name.
- **Signature strings live only in the consts at the top of `Plugin.cs`**, each with its provenance comment.
- **Dispose discipline** — hooks disposed, offsets undone, command and UiBuilder handlers removed, in reverse order.
- **Thread discipline** — game reads and pose writes only inside the render detour; ImGui only in `Draw`; the overlay reads the `Snap` written once per tick. No I/O anywhere on that path.

## Skills

Load these dotnet-claude-kit skills when relevant:

- `modern-csharp` — C# 14 idioms (baseline for all code)
- `code-review` — before a milestone lands
- `build-fix` — autonomous loop when a refactor breaks the build
- `instinct-system` — capture in-game discoveries and corrections

Web-stack skills (ef-core, minimal-api, authentication, caching, messaging, aspire, docker…) do not apply to this project.

## MCP Tools

`cwm-roslyn-navigator` comes from the dotnet-claude-kit plugin itself — no project `.mcp.json` is needed.

- **Before modifying a type** — `find_symbol`, then `get_symbol_source`
- **Before changing a signature** — `find_references` / `find_callers` (partial classes make grep unreliable)
- **After changes** — `get_diagnostics` instead of a full rebuild

## Commands

```bash
# Release build (also produces the DalamudPackager output)
dotnet build FootIk/FootIk.csproj -c Release

# There is no `dotnet test` — there is no test project.
```

Dev-load `FootIk\bin\Release\FootIk.dll` via `/xlplugins`.

**In-game verification loop:** build → `/xlplugins` dev-reload → `/ik` → the Feet / Ankles / Edges / Lean / Emotes / Performance / Status tabs. Toggling `Enabled` is the A/B. Anything touching hooks, collision or bone writes **can only be verified in game** — say so explicitly rather than claiming verification from a green build.

## Workflow

- **Read `docs/PLAN.md` first.** Offsets, sigs and the reverted-experiment list are already researched; do not re-derive them.
- **Plan first** for anything touching the hooks, the guard chain, or pose writes.
- **Update `docs/PLAN.md`** when a milestone lands or an experiment is reverted. That record is what stops a failed heuristic coming back.
- **Verify before done** — clean build + `get_diagnostics`, then state what still needs an in-game check and how to run it.
- **Capture non-obvious client behaviour** in memory/instincts — it is unGoogleable.

## Anti-patterns

Do NOT generate code that:

- **Hand-authors `FootIk.json` or `FootIk.yaml`** beside the csproj — silently kills the manifest
- **Sets SDK-provided csproj properties** (TFM, LangVersion, platform, nullable, unsafe) or adds NuGet references
- **Dereferences a game pointer outside the `ResolvePose` guard chain**, or wraps a pointer walk in `try/catch` and calls that safe
- **Does pose work in `Framework.Update`** — the edits do not survive to render
- **Calls `Original` inside the `try`, or conditionally** — it runs every frame, always
- **Writes an absolute `DrawOffset`** instead of adjusting our own tracked delta, or leaves the delta in place on `Dispose`
- **Hardcodes a distance in metres** instead of a `*Frac` of leg length
- **Uses `RaycastHit.Normal`** — it reads zero; derive the plane from the hit triangle
- **Reintroduces the reverted stair heuristics** ("higher hit wins", "prefer flat hits over ramps") — see `docs/PLAN.md` M5
- **Writes a non-finite value into the pose** instead of skipping the frame
- **Blocks the render thread** with file I/O or allocation-heavy work
- **Uses `async void`**, `.Result`/`.Wait()`, or static mutable state outside `Plugin`'s `[PluginService]` properties
