# Candidate validation

Version 0.4.0, 2026-10-07. Gameplay acceptance is in progress.

## Completed

- Build against the installed Game.dll and external AUMF DLL: zero warnings
  and zero errors.
- Isolated defense policy suite: 2,603 passing checks. Includes independent
  exhaustive comparison of randomized eligibility graphs, surplus assignment
  balance, empty inputs, disabled target types, missiles already in flight,
  intercept geometry, clock conversion, and immutable single-use shot targets.
  Role checks cover the full responsibility table, mixed building groups,
  separate targets within each role, no cross-role fallback, in-flight coverage,
  stable assignments, native unaligned hit chances, guaranteed hits for
  Tractor-Beam-aligned trajectories, player Automatic-mode ownership and
  allied or hostile ship-AI ownership for both supported weapon definitions.
  Storage Capsule checks cover the five/six trigger boundary, retention of the
  remaining group below six, reset at zero, and the Rocket Pod role.
- Targeted decompilation confirms the relevant behavior in the installed DLL.
- Patch audit passes for all 13 installed patch and behavior targets, including
  the non-player weapon lifecycle, clock timing API, Breach Capsule evasion,
  Tractor Beam alignment, Storage Capsule trajectory and native opening path,
  the invincibility damage gate, target-definition loader and warning API. This
  is a decompilation check, not an in-game patch-install check.
- Manifest, project, assembly and file version: 0.4.0 / 0.4.0.0 as appropriate.
  Product version: 0.4.0. Libraries contains only MissileOverhaul.dll. The
  custom target definition and English translation use Core's current mod data
  paths and four-column translation format.
- Final installed DLL matches the compiled output by SHA256:
  `A9011E910E6E31545B84CD217BB7BB0BE5E6169D96A6BE5870CCA0D1F67B747F`.

These checks establish build and isolated policy correctness. They do not
establish successful patch installation inside Unity or a measured interception
rate. A standalone patch-install attempt was unsuitable: the installed Harmony
detour runtime failed under modern .NET, and Unity dependencies with default
interface implementations could not be patched under desktop .NET Framework.
No game process was involved. The retained patch audit checks exact installed
method signatures and clock metadata through decompilation, without detours.

## Gameplay evidence: 2026-09-16

- During a Hullpiercer raid, armed Interceptor Turrets launched immediately.
  This confirms automatic readiness and immediate response for this scenario.
- The Hullpiercers reached strong Chromanite walls before the interceptor darts.
  Their trajectories ended just beyond the walls when they registered impact.
  The arriving darts then caused no delayed damage because the original targets
  were no longer active. This confirms safe stale-target handling, but it does
  not establish successful Hullpiercer interception.
- Weapon placement and dart travel distance remain possible causes. This single
  raid does not justify eight-direction perimeter groups or a balance change.
  Meteoroids and hostile ship missiles should be tested before drawing that
  conclusion.
- The fresh Player.log loads Missile Overhaul 0.2.0, records successful
  initialization, and contains the Hullpiercer raid activity. A focused search
  found no exception, Harmony failure, load error, or other runtime failure tied
  to Missile Overhaul. Other unrelated gameplay failures remain in the log.
- At the start of ship-to-ship combat, all four available Rocket Pods fired as
  expected. This confirms that their automatic readiness and native ship-combat
  targeting remain functional. The resulting damage appeared negligible.
  Missile Overhaul does not change ship-combat damage, evasion or repair. The
  installed Core definition gives each Rocket Pod shot 500 explosive damage,
  85% aim accuracy and full shield penetration; the target ship's evasion still
  determines whether a shot connects. One four-pod volley is insufficient to
  establish a damage or balance defect, so no tuning change is justified yet.

These observations came from the 0.2.0 player-only candidate. Its player weapon
behavior is retained in 0.4.0, but the complete candidate still requires a new
restart and log review. Hullpiercer interception effectiveness remains
inconclusive. Rocket Pod ship-combat launch timing passed in 0.2.0, while its
damage effectiveness remains observational. Meteoroid, allied and hostile ship
readiness, and hostile incoming ship-missile behavior remain pending.

- Version 0.3.0 loaded successfully and applied the role split and armed
  readiness. During a two-capsule ordinary Breach Capsule raid, Rocket Pods
  acquired and fired at the correct separate targets. Both capsules had been
  redirected by Tractor Beams onto stable paths, but the rockets visibly missed.
- Installed Core gives Breach Capsules 50% turret evasion. Combined with the
  Rocket Pod's 85% accuracy, version 0.3.0 allowed only a 42.5% hit chance even
  after a Tractor Beam completed alignment. Version 0.3.1 now uses Core's saved
  `WasAligned` trajectory flag to guarantee the hit in that state. Damage remains
  500 explosive per Rocket Pod shot and is intentionally unchanged.
- Version 0.3.1 then passed the ordinary Breach Capsule retest. The Rocket Pod
  acquired the Tractor-Beam-aligned capsule, hit it and destroyed it. This
  gameplay-confirms the aligned-trajectory accuracy correction; damage was
  sufficient in this observed case.
- The fresh Player.log records two successful Missile Overhaul 0.3.1
  initializations and no focused missing-method, Harmony, load or runtime error
  tied to the mod.

## Gameplay acceptance

1. Enable version 0.4.0 and restart Stardeus. Confirm a Missile Overhaul 0.4.0
   initialization line and no missing-method, Harmony or consumer-load errors
   in a fresh Player.log.
2. Use a test save with at least two powered buildings of each type, native
   ammunition supply, and the required Rocket Pod battle-station availability.
   Use Automatic mode and allow startup to finish before encountering threats.
3. Present simultaneous meteoroids and Hullpiercers alongside asteroids or
   ordinary breach capsules. Confirm Interceptors handle the dedicated defense
   threats and Rocket Pods handle the others. Confirm distinct targets receive
   shots within each role before surplus support. Repeat with late arrivals
   and existing shots in flight. Remove one building type and confirm the
   other does not take its responsibility.
4. Compare visible lead and impact at normal, fast and fastest ordinary speeds.
   Redirect an ordinary Breach Capsule until Core reports Tractor Beam success,
   then confirm the Rocket Pod shot hits. Also fire while the trajectory is still
   changing to confirm the native probability remains. Check that damage remains
   on the originally selected object.
5. Remove or destroy a target during flight. Confirm no damage moves to the
   replacement assignment. An already landed target must receive no delayed
   shot damage.
6. Let all threats disappear. Confirm loaded, armed launchers finish recharge
   and remain Ready with the native active power demand. Switch a launcher off,
   empty its ammunition, lose supply power, remove its station or enter Manual
   mode; confirm native restrictions still apply.
7. Test flux threshold and overload. Confirm the mod does not fire through
   those limits. Launch several hostile ship missiles; confirm Core assigns
   separate ready Interceptors and consumes their normal ammunition. Disable
   or empty a defender and confirm interception respects that restriction.
   Present meteoroids during ship combat and verify Interceptors still defend.
   Check Rocket Pods retain native ship combat for their remaining targets.
   In Manual mode, verify allowed shots work and cross-role shots spend no ammo.
8. Let an allied or hostile ship with Rocket Pods or Interceptors remain present
   long enough to finish normal startup before combat. Confirm supported weapons
   begin combat ready, Rocket Pods can attack immediately, and Interceptors can
   defend that ship against incoming missiles. Fire several missiles and confirm
   separate ready Interceptors are consumed in sequence. End combat while the
   ship remains present, allow recharge, and confirm a second combat does not
   impose another startup delay. A newly arrived ship attacked before startup
   finishes should retain the normal delay. Confirm Core still chooses opponents
   and no allied or hostile weapon attacks its own side.
9. Observe ordinary Storage Capsule arrivals with one through five capsules.
   Confirm there is no suspected-raid warning and Rocket Pods do not target
   them. Then present six flying Storage Capsules simultaneously. Confirm the
   exact warning **Incoming Storage Capsules are suspected raid transports.**
   appears once and Rocket Pods begin targeting separate capsules. After the
   first interception leaves five, confirm the remaining group stays eligible.
   Confirm the incident clears only after no eligible capsule remains in flight,
   and that a later six-capsule group can warn again.
10. Destroy a suspected Storage Capsule carrying a Being and one carrying an
   item. Confirm the native contents appear at the capsule's destruction
   position rather than being deleted or delivered at the planned landing point.
   Also verify a hostile raid payload retains its faction and equipment. A miss
   or a landed capsule must receive no delayed damage. Review any native capsule
   popup or story event for errors.
11. Save normally during a suspected incident and during missile flight; then
   test save-and-exit and reload. Confirm no duplicated cargo or damage, stale
   coverage or settings changes. Specifically record behavior if fewer than six
   capsules remain after reload, because the incident latch is transient.
   Disable the mod and restart to verify normal Rocket Pod behavior returns.

The complete maintained 0.3.1, 0.3.0, 0.2.0 and 0.1.0 candidates are preserved under
`Work/Backups`. No Core, other mod, or saved setting files were modified. No
Workshop publication was made.

Review fresh Player.log after these scenarios. Record observed launches and
successful interceptions separately from the configured probability. A
successful build alone does not establish gameplay or performance acceptance.
