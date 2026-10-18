# Quantum Storage — object-count performance results (1.3.2, retained in 1.3.3)

## Recorded post-update results — 2026-10-11

| Session | Final-delivery calls | Average per delivery | Calls >= 1 ms |
| --- | ---: | ---: | ---: |
| Original handoff baseline | 735 | 2.985 ms | 732 |
| 1.3.2 Player.log | 1,068 | 0.101169 ms | 1 |
| 1.3.3 Player.log | 995 | 0.10003 ms | 1 |

Both post-update profiles reported zero exceptions in this branch. These are different sessions and workloads, not a controlled same-duration benchmark or a promise of a particular FPS increase. The player reported a small overall FPS improvement with 1.3.2, and subsequently accepted 1.3.3's open-material-panel FPS behavior. See [UI-PERFORMANCE-TESTING.md](UI-PERFORMANCE-TESTING.md).

The release is ready for the author's performance-update upload. Detailed object identity, full-state, relocation and save/reload scenarios below remain available for targeted regression testing; the timing evidence does not claim those were all individually exercised in game.

## Baseline supplied by the player

Optimization Mod 0.1.32, rc327, approximately 105 seconds, 912 AI agents and 350 working Nanobot Hives:

- AIActionStoreObj: 2,255.432 ms / 82,814 calls.
- Final delivery: 2,193.942 ms / 735 calls, average 2.985 ms; 732 calls exceeded 1 ms.
- Destination selection: average 0.007 ms, eight candidates.

The optimization removes the per-successful-deposit global recount; it does not change source/destination routing.

## Controlled external check (not the player's save)

An in-memory fixture runs Core's actual private StoreObj with eight endpoints, 100,000 unrelated loose records and 735 timed deposits. The pre-patch path uses its existing RefreshFor after each store; the new path invokes the compiled prefix/postfix/finalizer. Endpoint counts and full flags must match at completion. A warmed final-build comparison observed 735 global queries versus zero, with averages of 0.10380 ms versus 0.00160 ms. Timing is environment-dependent and excludes Unity rendering, gameplay detours and AI delivery work. These numbers must not be compared directly with 2.985 ms as if they were the same measurement.

External lifecycle tests also exercise four network types, native deposit rejection, membership/counter discrepancies, partial failed ejections, destruction outcomes, nested callbacks and bulk scopes, exception recovery, topology rehoming/removal/relocation and cache reconstruction. Core object identity/reservation/save-record tests remain in place. Actual Unity spawning, full save loading and live Harmony detours remain gameplay checks; unchanged Hive notification after topology work requires Unity and is not executed by the external fixture.

## Optional controlled comparison and targeted regressions

1. After changing the DLL, restart and confirm the installed release (currently Quantum Storage 1.3.3) in a fresh Player.log, with no new Quantum/Relay exceptions. Keep an untouched original save; no save migration is required.
2. Use the same save, game speed, profiler setup and approximately 105-second observation window. Keep other mod/build/settings changes stable where possible. Compare final-delivery average, call count, total time and calls over 1 ms. Target: a meaningful reduction from 2.985 ms; no specific frame-rate increase is claimed in advance.
3. For Quantum Weapons Locker, Wardrobe, Organ Storage and Parts Storage, compare unique Core/global inventory totals with each network's used/capacity display. Deposit, withdraw through a different endpoint, reach capacity, confirm rejection when full, then remove one item and confirm a new deposit is allowed.
4. Exercise Eject All and disposal on a separate save copy. Contents must leave the active Quantum network once; counters and full flags must agree across peers. Reservations and Core job cancellation remain Core-owned.
5. Remove a non-final endpoint: its stored records must be rehomed without losing identity or changing total count. Remove the final endpoint: contents eject normally. Test relocation and a Relay-to-Quantum replacement; only the appropriate share transfers.
6. Save/reload and compare original object IDs, StorageId ownership, quality/condition and mind-bearing parts, not merely aggregate numbers. Check one ordinary storage and one object Relay chain for isolation from the new counting path.
7. Share the fresh profile and log if investigating a regression or running a controlled comparison. The results above and implementation checks do not establish exhaustive gameplay coverage of this checklist.

The counter is derived and never saved. A discrepancy triggers an authoritative recount; unavailable authoritative data blocks new intake instead of assuming the inventory is empty. Recounts remain at load/topology, recovery and explicit inspection/bulk boundaries, not every normal deposit. No added periodic global scan or per-tick profiler logging is installed.
