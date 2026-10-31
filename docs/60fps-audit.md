# OpenHuey 60 Hz simulation audit

Status: research only. This document does not change `openhuey-src/`, `native/`, or
top-level `src/`. `native/` and `src/` are used only as the 30 Hz behavioral reference,
as required by the project documentation (`openhuey-src/docs/design.md:7`).

## 1. Architecture map

### Current time flow

* `TICKS_PER_SECOND` is 30 (`openhuey-src/src/game/engine.h:19`). The host accumulates
  wall-clock nanoseconds, polls SDL once, and calls `engine_tick` once per fixed 1/30 s,
  with a four-tick catch-up cap (`openhuey-src/src/main.c:791`, `openhuey-src/src/main.c:871`,
  `openhuey-src/src/main.c:904`). The picture is also only drawn after a tick
  (`openhuey-src/src/main.c:917`).
* `engine_tick` increments `gEngine.ticks`, advances the movie, derives synthetic
  held-key edges, runs ordinary Forth tasks and C hooks, then advances models, delivers
  actor `tick`/`frame-end`, ticks effects/rumble/room, and updates the camera
  (`openhuey-src/src/forth/bind_engine.c:3348`, `openhuey-src/src/forth/bind_engine.c:3361`,
  `openhuey-src/src/forth/bind_engine.c:3390`, `openhuey-src/src/forth/bind_engine.c:3398`).
  The exact order is part of the baseline: model motion is advanced before actor logic,
  while pose/cloth/feet work follows actor delivery (`openhuey-src/src/game/actor.c:383`,
  `openhuey-src/src/game/actor.c:406`).
* The actor kernel runs ready waits first, broadcasts `tick`, delivers bounded FIFO rounds,
  broadcasts `frame-end`, delivers again, and increments its frame counter
  (`openhuey-src/external/clankforth/src/actors.c:570`,
  `openhuey-src/external/clankforth/src/actors.c:585`,
  `openhuey-src/external/clankforth/src/actors.c:601`).
  `tick`, `frame-end`, and `held-tick` are deliberately not queued for busy actors
  (`openhuey-src/external/clankforth/src/actors.c:151`,
  `openhuey-src/external/clankforth/src/actors.c:460`).
* Forth task waits are integer actor-frame waits. `await-within` stores
  `f->frame + max(frames,1)` and wakes at that frame (`openhuey-src/external/clankforth/src/actors.c:770`).
  The OpenHuey `seconds` word is literally `30 *` (`openhuey-src/scripts/base.fs:4`).
  `dt` currently always returns `1/30` (`openhuey-src/src/forth/bind_engine.c:3264`).
* Messages are the gameplay API. The documented frame is
  `input -> tasks -> tick -> deliver -> frame-end -> deliver -> figures -> draw`
  (`openhuey-src/docs/design.md:39`), and the actor kernel's public body facts are read
  by acoustics, camera, navigation, and characters (`openhuey-src/docs/actors.md:12`).

### Subsystem map

| Area | Current entry points and time-sensitive boundary |
|---|---|
| Host/vsync | `main` accumulator and `SDL_GL_SetSwapInterval(1)` (`openhuey-src/src/main.c:791`, `openhuey-src/src/platform/platform.c:208`) |
| Input | `platform_poll`, SDL event edges, gamepad threshold-to-key edges (`openhuey-src/src/platform/platform.c:140`, `openhuey-src/src/platform/platform.c:174`); Forth reads `key-down?`, `key-pressed?`, sticks and mouse (`openhuey-src/src/forth/bind_engine.c:2877`, `openhuey-src/src/forth/bind_engine.c:2960`) |
| Actors/messages | `hactor_frame`/`forth_actors_frame`, per-actor `tick` and `frame-end` (`openhuey-src/src/hactor/hactor.c:19`, `openhuey-src/external/clankforth/src/actors.c:570`) |
| Forth scheduler | `forth_run_tasks`, `yield`, `wait`, `await`, `await-within` (`openhuey-src/src/forth/bind_engine.c:3369`, `openhuey-src/external/clankforth/src/actors.c:770`) |
| Models/animation | `actor_advance`, `advance`, `step_time`, root deltas, motion tracks and events (`openhuey-src/src/game/actor.c:214`, `openhuey-src/src/game/actor.c:428`, `openhuey-src/src/forth/bind_engine.c:2718`) |
| Camera | Forth camera actor's tick/ease and frame-end tracking (`openhuey-src/scripts/camera.fs:52`, `openhuey-src/scripts/camera.fs:68`); C `camdir_ease` has a 60-frame ease (`openhuey-src/src/game/camdirector.c:626`) |
| Audio | SDL audio callback pulls sample frames independently; music resamples by source-rate/SOUND_RATE (`openhuey-src/src/platform/sound.c:15`, `openhuey-src/src/platform/music.c:35`). Gameplay changes volumes/fades on ticks, but audio output is not tick-count driven. |
| Movies | `movie_update` currently receives one tick and advances a game-clock movie by `ticks/30`; real-time movies use SDL nanoseconds (`openhuey-src/src/platform/movie.c:565`, `openhuey-src/src/platform/movie.c:701`). |
| Cutscenes | `cutscene.c` stores integer authored shot frames, marks and frame ranges (`openhuey-src/src/game/cutscene.c:33`, `openhuey-src/src/game/cutscene.c:157`); cutscene camera ownership is exposed by `cutscene_camera` and `cam-scene?` (`openhuey-src/scripts/camera.fs:52`). |
| Room/world | `room_tick`, doors, navigation and body movement run once per simulation tick (`openhuey-src/src/forth/bind_engine.c:3406`; documented body ownership at `openhuey-src/docs/actors.md:12`). |

### Target clock contract

Use one fixed-step path with a runtime `dt` and a reference rate `R=30`. `dt=1/30`
must execute the same logical step and produce the current 30 Hz behavior exactly.
`dt=1/60` executes the same step twice per authored 30 Hz interval. The authored
30 Hz preset (“as on the PS2”) must force `dt=1/30`, regardless of display refresh.
Audio remains sample-clocked and must not consume simulation ticks.

## 2. Inventory of frame-dependent values and conversions

Notation: `s = dt * 30` is the number of old 30 Hz steps represented by one new
simulation step. For a timer measured in old frames, `N' = N / s` is only valid if
the timer is represented as continuous time; the safer implementation is a seconds
or fixed-point accumulator. For a 30 Hz baseline, `s=1`, so all formulas below are
identity operations.

### TIMER

| Location | Value/behavior | Classification and required conversion |
|---|---|---|
| `openhuey-src/src/main.c:791`, `openhuey-src/src/game/engine.h:19` | Host tick period and `ticks` | TIMER. Store `dt`/clock mode in the engine; do not make scripts infer 60 Hz from a global integer. |
| `openhuey-src/external/clankforth/src/actors.c:101`, `openhuey-src/external/clankforth/src/actors.c:770` | Kernel frame and `await-within` wake frame | SCRIPT WAIT/TIMER. Replace frame arithmetic with elapsed simulation time or preserve a 30 Hz logical wait domain explicitly; `N` old frames means `N/30` seconds. |
| `openhuey-src/scripts/base.fs:4` | `seconds = 30 *` | SCRIPT WAIT. Make seconds a time word, not a frame multiplication; `seconds N` must wait `N` seconds under either dt. |
| `openhuey-src/scripts/avoid.fs:10`, `openhuey-src/scripts/avoid.fs:14`, `openhuey-src/scripts/avoid.fs:28` | Prompt lifetime and decrement each tick | TIMER/INPUT. `left_seconds -= dt`; edge-triggered dismissal remains discrete. |
| `openhuey-src/scripts/screen.fs:23`, `openhuey-src/scripts/screen.fs:73`, `openhuey-src/scripts/screen.fs:87` | Fade steps, `steps`, trailing/lingering state | TIMER/ANIMATION. Advance `t += s` or use seconds; preserve first-step and post-fade discrete script transitions at crossed boundaries. Linear level is `level(t)=from+(to-from)*clamp(t/T,0,1)`. |
| `openhuey-src/scripts/gameover.fs:32`, `openhuey-src/scripts/gameover.fs:86`, `openhuey-src/scripts/gameover.fs:142` | Game-over sequence timer | TIMER. Convert countdowns to seconds; sequence state changes occur once when the boundary is crossed. |
| `openhuey-src/scripts/panic.fs:20`, `openhuey-src/scripts/panic.fs:39`, `openhuey-src/scripts/panic.fs:244` | Breath beat, 450-frame panic, shorten operation | TIMER. Store seconds/continuous remaining time; preserve panic state transitions and “first breath now” as discrete events. |
| `openhuey-src/scripts/summoner.fs:23`, `openhuey-src/scripts/summoner.fs:37`, `openhuey-src/scripts/summoner.fs:171`, `openhuey-src/scripts/summoner.fs:218` | Away/cooldown/age and random delay | TIMER. Convert all stored frame durations to seconds; random delay distribution must be in seconds, not “more random rolls” at 60 Hz. |
| `openhuey-src/scripts/creatures/state.fs:10`, `openhuey-src/scripts/creatures/state.fs:14`, `openhuey-src/scripts/creatures/trip.fs:12` | Creature age/wait and minutes-to-frames | TIMER. `minutes(n)=60*n` seconds; count down by `dt`. |
| `openhuey-src/scripts/things.fs:31`, `openhuey-src/scripts/things.fs:59`, `openhuey-src/scripts/things.fs:254` | Stalker report age and item lifetimes | TIMER. Convert lifetimes and report windows to seconds. |
| `openhuey-src/scripts/obstacles.fs:6`, `openhuey-src/scripts/obstacles.fs:63`, `openhuey-src/scripts/obstacles.fs:71` | 30-frame push and per-tick move frames | TIMER/INTEGRATOR. Push duration is one second; sample movement along that duration continuously. |
| `openhuey-src/src/game/rumble.c:11`, `openhuey-src/src/game/rumble.c:94` | Rumble duration and sequence countdown | AUDIO/SYNC. Use seconds or sample/time scheduling; never let a 60 Hz tick halve rumble durations. |

### INTEGRATOR

| Location | Value/behavior | Required conversion |
|---|---|---|
| `openhuey-src/src/game/actor.c:604`, `openhuey-src/src/game/actor.c:617` | Animation root turn/translation | `position += velocity * s`; root deltas must be sampled over the crossed animation interval, not applied as a 30 Hz delta twice. |
| `openhuey-src/src/forth/bind_engine.c:2738`, `openhuey-src/src/forth/bind_engine.c:2755` | Track/foot sampling at a future frame offset | Pass an animation-time offset in old-frame units (`offset = dt*30`), and sample events/tracks over `[old,new]`. |
| `openhuey-src/scripts/common.fs:11`, `openhuey-src/scripts/cast.fs:107` | Turn toward target by degrees per old frame | `step' = step * s`; for clamped turning, consume up to `step'` and preserve overshoot/arrival discrete behavior. |
| `openhuey-src/scripts/fiona/ladder.fs:68`, `openhuey-src/scripts/fiona/ladder.fs:70` | Falling step accelerates by 0.5 each frame | Integrate in seconds: `v += acceleration * dt`, `x += v * dt`; do not simply run the old recurrence twice unless exact baseline mode is intended. |
| `openhuey-src/src/game/camdirector.c:292`, `openhuey-src/src/game/camdirector.c:378` | Camera path parameter step | `path_t += step_look * s`; 60-frame easing becomes one second. |

### DECAY/DAMPING

| Location | Value/behavior | Required conversion |
|---|---|---|
| `openhuey-src/src/main.c:264`, `openhuey-src/src/main.c:308` | Shadow share and shown-shadow approach | If `x += (target-x)*a` is one old tick, use `a' = 1 - pow(1-a,s)`. The fixed 30 Hz path must retain the old arithmetic ordering. |
| `openhuey-src/src/render/render.c:31`, `openhuey-src/src/render/render.c:41` | Bloom/colour phase steps | Treat as time-based phase/decay; `v' = v * pow(k,s)` for multiplicative decay, or scale additive rates by `s`. |
| `openhuey-src/scripts/music.fs:46`, `openhuey-src/scripts/panic.fs:100` | Volume/fade and breath counters | Volume ramps use continuous elapsed time; countdowns use seconds. Any multiplicative gain uses `k' = pow(k,s)`. |
| `openhuey-src/scripts/things.fs:203`, `openhuey-src/docs/subsystems/things.md:9` | Ball roll damping and bounce coefficients | Bounce coefficient is per collision and remains discrete; rolling slowdown per old tick needs `k' = pow(k,s)` if it is damping, while slope acceleration is an integrator. |

### PROBABILITY

| Location | Value/behavior | Required conversion |
|---|---|---|
| `openhuey-src/src/game/actor.c:289`, `openhuey-src/src/game/actor.c:306` | Blink chance after 30 frames, guaranteed by 150 | ANIMATION/PROBABILITY. Preserve the authored hazard function in seconds; if per-step probability is `p`, use `p' = 1 - pow(1-p,s)`. Do not roll twice at 60 Hz with unchanged `p`. |
| `openhuey-src/scripts/common.fs:7`, `openhuey-src/scripts/common.fs:9` | RNG and uniform random source | PROBABILITY. RNG state advances only at logical random decisions; do not add extra rolls merely because there are more simulation steps. |
| `openhuey-src/scripts/summoner.fs:187`, `openhuey-src/scripts/stalker/attack.fs:25`, `openhuey-src/scripts/stalker/attack.fs:167` | Summoning, stumble, attack chance rolls | PROBABILITY. Convert per-old-frame hazard to `1-(1-p)^s`; one-shot event rolls remain one-shot. |
| `openhuey-src/scripts/hewie/actions.fs:18`, `openhuey-src/scripts/creatures/homunculus.fs:98` | Random idle/action and tie choices | DISCRETE/PROBABILITY. These are decisions, not rates: execute once per logical decision, with no duplicated 60 Hz roll. |

### ANIMATION

| Location | Value/behavior | Required conversion |
|---|---|---|
| `openhuey-src/src/game/actor.c:428`, `openhuey-src/src/game/actor.c:440`, `openhuey-src/src/game/actor.c:452` | Motion, variant, previous-motion frames and blend fade | ANIMATION. Advance animation time by `s` old frames; blend remaining time decreases by `s`. |
| `openhuey-src/src/game/actor.c:214`, `openhuey-src/src/game/actor.c:232` | Loop/clamp at motion boundaries | ANIMATION/DISCRETE. Detect every crossed integer event/boundary in `(old_frame,new_frame]`; a non-looping end fires once, not once per render tick. |
| `openhuey-src/src/game/actor.c:306`, `openhuey-src/src/game/actor.c:360` | Blink and motion extra channels | ANIMATION. Sample by continuous animation time and fire event tracks across the full crossed interval. |
| `openhuey-src/src/forth/bind_engine.c:2738`, `openhuey-src/src/forth/bind_engine.c:2759` | Foot/contact tracks and root events | ANIMATION. Event sounds/foot contacts must be range-crossing events; interpolation is continuous. |
| `openhuey-src/scripts/cast.fs:94`, `openhuey-src/scripts/cast.fs:298`, `openhuey-src/scripts/fiona/voice.fs:18` | Scripted animation starts, blends, root movement and key-frame sounds | ANIMATION/AUDIO-SYNC. Blend durations use seconds; motion event sounds fire once per crossed authored event. |
| `openhuey-src/src/game/cutscene.c:157`, `openhuey-src/src/game/cutscene.c:198` | Cutscene marks, shot frame ranges and integer scene frame | ANIMATION/DISCRETE. Keep authored cutscenes at 30 Hz; do not reinterpret their integer frame numbers as gameplay 60 Hz. |

### SCRIPT WAIT

`wait`, `yield`, actor `await`, script fades, text typing, room scripts, and all
message handlers that count frames are in this class. The kernel behavior is at
`openhuey-src/external/clankforth/src/actors.c:570` and
`openhuey-src/external/clankforth/src/actors.c:770`; text typing explicitly uses
glyph frame delays at `openhuey-src/docs/subsystems/window.md:41`. Convert authored
durations through a time-aware wait primitive. A wait of zero/one old frame must
remain a minimum one logical step so it cannot disappear at 60 Hz.

### INPUT

* SDL events and gamepad state are polled before the accumulator consumes ticks
  (`openhuey-src/src/main.c:874`, `openhuey-src/src/platform/platform.c:174`).
  Pressed bits are cleared after each simulation tick (`openhuey-src/src/main.c:909`).
* Synthetic held-key edges are computed once per tick (`openhuey-src/src/forth/bind_engine.c:3361`);
  gamepad stick-to-key edges are also edge-triggered (`openhuey-src/src/platform/platform.c:174`).
  At 60 Hz this otherwise doubles edge opportunities and button-mash counts.
* Continuous stick values are safe to sample per step, but movement must multiply speed
  by `s`/`dt`; `stick@` and the run button are read in `openhuey-src/scripts/keys.fs:24`
  and `openhuey-src/scripts/fiona/controls.fs:8`.
* Fiona’s struggle counters (`shakes-now`, `shook-from-rest`) are per-tick input
  opportunities (`openhuey-src/scripts/fiona/controls.fs:54`, `openhuey-src/scripts/fiona/controls.fs:70`).
  Keep button/gesture edges in an input accumulator or logical 30 Hz decision domain;
  never count the same physical press twice.
* UI repeat, menu navigation, mouse deltas, and buffered windows are UNSURE until every
  consumer is enumerated; current polling exposes raw per-tick pressed/mouse values
  (`openhuey-src/src/forth/bind_engine.c:2943`, `openhuey-src/src/forth/bind_engine.c:2993`).

### AUDIO/SYNC

* The SDL audio callback pulls sample frames independently (`openhuey-src/src/platform/sound.c:15`,
  `openhuey-src/src/platform/sound.c:27`). Music advances by source sample rate, not game
  ticks (`openhuey-src/src/platform/music.c:35`); preserve that separation.
* Movie audio is likewise mixed as samples, but video scheduling currently receives one
  game tick (`openhuey-src/src/platform/movie.c:701`). In gameplay mode, decouple movie
  playback from the simulation; in locked-30 mode, use an explicit authored movie clock.
* Volume fades, music stage ramps, rumble, animation event sounds, acoustics’ “this frame”
  buffer, and lip/voice event timing are coupled today. Acoustics intentionally collects
  noises and dispatches at `frame-end` (`openhuey-src/scripts/acoustics.fs:95`,
  `openhuey-src/scripts/acoustics.fs:106`); at 60 Hz either define a 60 Hz acoustic
  window or preserve a 30 Hz logical window, and verify stalker hearing.
* A per-old-frame linear ramp `x += d` becomes `x += d*s`; a per-frame probability
  uses `1-(1-p)^s`; an audio clock must not use either formula.

### DISCRETE LOGIC that should remain per-tick

Actor message order, FIFO delivery rounds, subscriptions, state-machine transitions,
collision/door/navmesh queries, “first crossed event”, room-enter/leave broadcasts,
save/load message ordering, and one-shot RNG decisions are discrete. Evidence:
kernel round/order rules (`openhuey-src/external/clankforth/src/actors.c:531`),
room transition ordering (`openhuey-src/docs/subsystems/rooms.md:46`), and door state
queries (`openhuey-src/src/game/world.c:145`). At 60 Hz, do not silently change
message ordering or execute a one-shot transition twice.

## 3. Risk list

1. **Integer/frame assumptions:** `TICKS_PER_SECOND`, `Engine.ticks`, Forth `frame`,
   `seconds`, `await-within`, text glyph delays, and all literal durations are spread
   across C and scripts (`openhuey-src/src/game/engine.h:19`, `openhuey-src/scripts/base.fs:4`,
   `openhuey-src/docs/subsystems/window.md:41`).
2. **Animation quantization:** motion lengths are integer authored frames, `step_time`
   clamps/wraps, and event tracks are sampled by frame (`openhuey-src/src/game/actor.c:214`,
   `openhuey-src/src/game/actor.c:360`). A naïve “tick twice” can skip or duplicate
   key-frame sounds, hand poses, foot contacts, lip/face channels, or root deltas.
3. **Fixed-size histories/buffers:** actor recent messages are capped at 256 and delivery
   rounds at 64 (`openhuey-src/external/clankforth/src/actors.c:13`,
   `openhuey-src/external/clankforth/src/actors.c:32`); post-processing retains last-frame
   images/trails (`openhuey-src/src/render/post.c:10`). More ticks can change overflow
   behavior and visual blend duration.
4. **Frame parity/modulo:** particle/effect code contains frame counters and modulo
   conditions (`openhuey-src/src/game/particles.c:4377`); audit every `%`, bit test, and
   “every N frames” in `src/` and scripts before enabling 60 Hz.
5. **Save/load:** saves serialize progress and subsystem state through broadcast messages,
   while some records are explicitly play-frame based (`openhuey-src/docs/subsystems/ending.md:14`,
   `openhuey-src/docs/subsystems.md:82`). Decide whether persisted durations
   are seconds, authored 30 Hz frames, or both, and version the format if needed.
6. **Input repeat and edge windows:** pressed bits are cleared once per tick
   (`openhuey-src/src/main.c:909`), so 60 Hz doubles button-mash/struggle opportunities
   unless physical edges are buffered and consumed once.
7. **Stalker pathing/turning:** walk root motion, 4-degree turns, look-at 8-degree turns,
   route replanning and “arrive this frame” logic are all per-frame in the cast/stalker
   rules (`openhuey-src/docs/subsystems/cast.md:91`, `openhuey-src/scripts/cast.fs:107`).
8. **Fiona struggle/escape:** `shakes-now`, `shook-from-rest`, avoid prompts, and
   seized/struggle state are tick-counted (`openhuey-src/scripts/fiona/controls.fs:54`,
   `openhuey-src/scripts/avoid.fs:28`).
9. **Doors/room transitions:** door swing events, exit triggers, `leaving-room`/`arrived`/
   `entered-room` order, and camera reset must happen once at a boundary
   (`openhuey-src/scripts/doors.fs:146`, `openhuey-src/docs/subsystems/rooms.md:46`).
10. **Hewie mini-game:** it changes who is controlled and uses its own actions, timers,
    trust, commands, fights, and restart path (`openhuey-src/docs/subsystems/hewie.md:1`,
    `openhuey-src/scripts/hewie/states.fs:17`). It must not inherit a doubled input or
    animation-event rate accidentally.
11. **Effects/render:** particles contain integer frames/timers and some modulo cadence
    (`openhuey-src/src/game/particles.c:23`, `openhuey-src/src/game/particles.c:4377`);
    frame blend and glow trails intentionally depend on the previous displayed frame
    (`openhuey-src/src/render/post.c:627`).
12. **Hidden C constants:** camera ease is explicitly 60 frames
    (`openhuey-src/src/game/camdirector.c:644`), animation fade is decremented by one
    (`openhuey-src/src/game/actor.c:463`), and shadow smoothing uses a fixed coefficient
    (`openhuey-src/src/main.c:264`). These are easy to miss in a script-only audit.

## 4. Cutscene and mode-switch plan

1. Add an engine clock mode with `GAMEPLAY` and `LOCKED_30`. The mode owns `dt`,
   authored-frame scale, and whether the movie clock is simulation-driven. Do not infer
   mode from `TICKS_PER_SECOND`.
2. Switch only at a simulation tick boundary, before the next task/model/actor step.
   The cutscene owner is already explicit (`cutscene_camera`/`cam-scene?`), and movie
   ownership is explicit in the movie API (`openhuey-src/src/game/cutscene.c:33`,
   `openhuey-src/scripts/camera.fs:52`).
3. In gameplay, run the common fixed-step path at `dt=1/60`; movies/FMV playback use
   real/sample time or their own authored 30 Hz presentation clock. In a cutscene,
   scripted sequence, or movie that is required to remain authored, select `dt=1/30`
   and advance integer scene/movie frames once per locked step.
4. On entry, flush or explicitly hand off crossed animation/event ranges, input edges,
   camera ownership, audio fade state, rumble, and acoustic noise buffers. On exit,
   reset the same boundaries before gameplay resumes. Never switch in the middle of
   actor message delivery.
5. Keep `as on the PS2` permanently in `LOCKED_30`; enhanced/default gameplay may use
   60 Hz. The setting must be serialized with the existing options and tested through
   the settings path, not only a command-line flag.

Hand-off risks are duplicate cutscene marks, a movie frame advancing twice, stale
`frame-end` noises, a camera ease whose remaining 30 Hz frames were interpreted as
60 Hz, and Forth waits that cross the mode transition. These must be explicit tests.

## 5. Verification plan

### 30 Hz identity

* Add a test/headless clock mode that uses the new dt plumbing at exactly `1/30`.
  Capture a per-tick hash after the same phase as the current `engine_tick`:
  engine clock/tick, RNG states, actor state/body/model motion and frame, Forth task
  state/wakes, mailboxes/queue, room/door state, camera, effects, and script-owned
  fields. The actor kernel already has frame/message trace data (`openhuey-src/external/clankforth/src/actors.c:1232`).
* Run identical input/event scripts against the old implementation and the new
  `dt=1/30` path. Require identical hashes at every tick, identical random draws,
  identical messages/order, and identical final save state. A final-image comparison
  alone is insufficient.
* Extend existing headless ctests where their tests already assert frame behavior:
  acoustics, camera, doors/rooms, panic, music, obstacles, things, summoner, window,
  pause, gameover and ending (the test inventory is described in
  `openhuey-src/README.md:20` and subsystem status pages such as
  `openhuey-src/docs/subsystems/panic.md:101`).

### 60 Hz comparison

* Run the same deterministic input at 30 Hz and 60 Hz. Compare every second 60 Hz
  simulation state to the 30 Hz state.
* Require exact equality for discrete state: actor behaviour/state enums, message
  sequence, room/door transitions, script instruction/wait state at equivalent
  boundaries, RNG stream/decision outcomes, animation event IDs, item ownership,
  stalker modes, Hewie commands, and save-visible flags.
* Require epsilon bounds for continuous state: bodies/velocities, camera transform,
  animation phase, fades, panic level, music/rumbler control values. Record the
  epsilon and explain each exception; do not hide divergence by comparing only
  screenshots.
* Test audio by elapsed sample/time position and event identity, not tick count. Test
  FMV by authored frame number and audio sample position.

### Headless scenarios

Add deterministic scenarios for: title-to-room start; Fiona walking, running,
turning and ladder fall; door open/transition/back transition; room scripts/fades;
Hewie command, fetch, fight, mini-game restart; stalker off-stage summon, hearing,
search/chase/attack/escape; panic onset/recovery and struggle; obstacle push; thrown
things and timed magnesia; camera cut/path/shot; animation blends and foot/voice
events; pause/unpause; save/load during timers; each cutscene/FMV enter/exit; gameover
and ending. The scenario set should use the existing `--hidden --frames` and
`--test` interfaces (`openhuey-src/README.md:17`).

## 6. Ordered implementation plan

1. **dt plumbing at 1/30 with hashes.** Add a clock object and pass dt through the
   common tick path without changing behavior. Success: old/new `dt=1/30` hashes,
   message traces, RNG draws and ctest results are identical.
2. **Timers and waits.** Convert engine/Forth waits, seconds, fades, text, rumble and
   gameplay countdowns to elapsed-time representations while preserving a minimum
   logical step. Success: all timed headless scenarios finish at the same real
   seconds in both modes and the 30 Hz hash remains identical.
3. **Movement/integration.** Scale body/root movement, turns, ladder/falling, rolling
   and navigation-facing rates by dt. Success: 60 Hz positions match 30 Hz every
   second within documented epsilon; collision and arrival decisions match.
4. **Animation.** Make motion/variant/blend time continuous; fire all crossed events
   exactly once. Success: no missing/duplicate foot, hand, voice, face or root events;
   30 Hz state hashes stay identical.
5. **Camera.** Convert path/ease/turn/shake rates and preserve camera ownership/cuts.
   Success: equivalent camera transforms at one-second checkpoints and exact cut
   sequence.
6. **Hewie and stalkers.** Convert commands, trust/struggle, senses/hearing, summoner
   timers, pathing, attacks and escape probabilities. Success: deterministic Hewie
   mini-game and stalker scenarios produce identical discrete outcomes and bounded
   continuous divergence.
7. **Effects and presentation.** Convert particles, shadows, fades, post trails,
   rumble and music control ramps; leave SDL audio sample mixing independent.
   Success: effect lifetimes/audio event timestamps remain real-time correct and
   `as on the PS2` remains locked at 30 Hz.
8. **Forth/script waits and authored sequences.** Audit every remaining `wait`,
   `await-within`, literal frame count, modulo cadence and save-visible tick counter.
   Success: full headless suite and save/load scenarios pass with no timing drift.
9. **Enable 60 Hz and UI setting.** Add the default gameplay 60 Hz option and make
   the PS2 preset permanently 30 Hz; add mode-switch tests for every cutscene/FMV.
   Success: UI setting persists, gameplay is true 60 Hz, authored movies/cutscenes
   remain 30 Hz, and all verification gates pass.

## 7. Open questions

1. Should Forth `wait`/`await-within` be globally continuous-time, or should some
   script waits intentionally remain 30 Hz logical waits for exact authored behavior?
2. Is acoustics’ “this frame” window intended to become 1/60 s, or should it remain
   a 1/30 s gameplay window with two internal substeps?
3. Which save fields are externally visible as 30 Hz play frames, and can their
   serialized representation change to seconds without invalidating existing saves?
4. Should input be sampled once per render poll and buffered into the fixed-step loop,
   or sampled separately for every simulation step during catch-up?
5. Are all particle modulo/frame-parity effects authored gameplay behavior, or are
   some presentation-only and safe to run on a render clock? The current C code does
   not make that boundary obvious (`openhuey-src/src/game/particles.c:4377`).
6. Does the movie player’s `sGameClock` need a third explicit clock (authored movie
   time) separate from real time and simulation time? Current game-clock behavior is
   hard-coded to `ticks/30` (`openhuey-src/src/platform/movie.c:565`).
7. Which continuous-value epsilon is acceptable for navmesh projection, camera paths,
   animation root motion and floating-point fade/panic state?
8. Are any scripts relying on the actor kernel’s “busy actors miss per-frame messages”
   rule (`openhuey-src/external/clankforth/src/actors.c:460`) as a discrete synchronization
   mechanism? If so, changing wait wake timing may alter behavior even when seconds match.
9. Is lip sync entirely motion-track/event driven, or are there additional audio/movie
   coupling points outside the current OpenHuey source scan? Marked UNSURE until an
   asset/event trace confirms it.
