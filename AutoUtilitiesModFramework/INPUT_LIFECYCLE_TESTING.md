# Pooled input callback repair - 2026-09-11

Status: implemented for local testing. Build/static checks and 15 isolated
regression tests pass. Fresh Player.log review and in-game acceptance are
pending. Version remains 1.4.0 (assembly/file 1.4.0.0). The demonstration UI
remains disabled. No consumer, Core, save, preference, or Workshop changes were
made by this repair.

## Confirmed defect and ownership

The installed pre-repair AUMF DLL matches the 2026-09-05 release. Its `AddInput`
adds a new `CommitOnce` closure to `TMP_InputField.onEndEdit` on every binding,
without removing the previous listener. Each closure captures a consumer
callback and has its own frame guard; the guards do not deduplicate different
generations. A reused physical widget can therefore call several logical rows.

The installed Core assemblies confirm that `WInput.SetOnSubmit` replaces a
single `Action<string>`, while `WInput.Load` does not clear end-edit listeners.
`WBase.Dispose` returns the widget to `PrefabPool`; pooling and TMP deactivation
do not clear those additional listeners. Core's `Lifetime.Dispose` marks the
lifetime disposed before deactivating its widgets and invokes `OnDispose`
callbacks afterward. These contracts were checked against compiled assemblies,
not only reference source.

This is distinct from the earlier global text-focus repair. That repair and
its user acceptance remain valid; they did not address listener ownership.

## Repair

`AddInput` delegates binding to the internal `PooledInputCommitBinding` class.
Each binding:

- Uses the existing Core submit setter and adds one owned end-edit listener.
- Commits once per frame while its content lifetime remains active.
- Rejects events from disposed content, including events during deactivation
  before the disposal callback runs and already captured event-dispatch lists.
- Removes only its own end-edit listener when the lifetime ends.
- Clears its consumer callback, input-field reference, and lifetime reference.

Core has no getter or conditional removal for the submit slot. Cleanup does
not call `SetOnSubmit(null)`, because that could erase a consumer replacement.
An AUMF delegate remaining there is inert and retains only its empty binding;
there is one slot, not an accumulating list. The next binding replaces it.

No `RemoveAllListeners`, global patch, reflection-based runtime cleanup, polling,
new public API, consumer version gate, or game-state change was introduced.
Consumers still own any additional listeners they attach directly.

## Read-only installed-consumer audit

Source was cross-checked with actual call instructions in installed DLLs.
There are six `AddInput` call sites across five consumer DLLs. No consumer
usage change is required to receive the AUMF-owned cleanup.

| Consumer | Current input use | Effect / test target |
| --- | --- | --- |
| AACU | `AddInput` for pulse interval | Receives repair automatically; test repeated edits and reopen. |
| Auto Hauling | `AddInput` for item filter | Receives repair automatically; test filters and repeated refreshes. |
| TACC | `AddInput` for colonist search | Receives repair automatically; test search and tab switching. |
| Auto Factory | `AddInput` for Overview factory priority and Goals search | Overview receives repair directly. Search replaces the handlers; its existing replacement remains untouched. Product rows use consumer-owned bindings. |
| Planning Overhaul | Calls `AddInput`, then replaces submit/end-edit handlers | AUMF retires only its original binding. Consumer-installed callbacks and distinct submit semantics remain consumer-owned. |
| Stock Market Auto Investor | Currently uses fresh native inputs with `ScopedInputBinding`, not `AddInput` | Its workaround bypasses this repair. Migration back to AUMF is an optional owner task after framework acceptance, not a prerequisite. |
| CARAU | Custom `InputCommitHandler` use, no `AddInput` call | That separate helper is unchanged. |

The other installed AUMF consumer DLLs (Bot Auto Hack, Intelligent Recycling,
Part Restoration Unit, Reconnect Teleporter Connections, Super Cleaning,
Tractor Beam Auto Connect, Tractor Beam Overhaul) do not call `AddInput`.

Planning Overhaul still adds its own end-edit closure without a corresponding
lifetime removal in the inspected helper. Its replacement clears listeners on
rebinding within that helper, but AUMF does not remove that consumer-owned
listener if the pooled field later moves elsewhere. This is a separate
consumer cleanup review, not covered by the framework-owned repair. Existing
Auto Factory and Stock Market Auto Investor workarounds were not removed or
revised. Do not treat a test of a bypassed/custom input as acceptance of
`AddInput` itself.

## Verification

- Final release build: 0 errors, 5 established game-reference warnings.
- 329 public API entries preserved; all 522 AUMF member references in 14
  installed consumer DLLs resolve against the candidate.
- 436 existing method bodies unchanged, including panel focus/refresh/teardown,
  themes, layout, shortcuts, and top-bar behavior. Only `AddInput` changes;
  its old private commit closure is replaced by four internal binding methods.
- Installed metadata remains 1.4.0 / 1.4.0.0; test UI remains disabled.
- 15 isolated tests compile the actual production binding source against
  small Core/Unity event doubles. They are not a running Unity simulation.
  Coverage includes a negative control reproducing the old accumulation,
  1,000 reuse cycles, both submit/end-edit orders, independent fields,
  unrelated listeners, consumer replacements, disposal-time events, stale
  dispatch snapshots, delayed old cleanup, reentrant reuse, null callbacks,
  and release of consumer/lifetime references while the inert submit slot lives.

The test source is outside both `Code` and the `.vscode` project directory so
the mod build cannot include its doubles. An intermediate build caught this
placement issue before installation; after isolation, the final rebuild and
all tests passed. No test doubles are present in the installed mod DLL.

Run isolated tests with `.vscode/RunPooledInputTests.ps1 -OutputDirectory
<temporary-test-directory>`. Compiler/reference paths are parameters.
`.vscode/VerifyThemePreview.ps1` also checks public API compatibility, consumer
member resolution, version metadata, and retained theme contracts at 1.4.0.

## Runtime acceptance checklist

1. Fully restart Stardeus. Replacing the DLL does not repair the old assembly
   or callbacks in an already-running game.
2. In a direct `AddInput` consumer, edit a value, press Enter, then test leaving
   the field by clicking elsewhere. Each interaction must update only its row.
3. Repeat edits, panel refreshes, tab changes, and close/reopen cycles. Include
   two different direct consumers to exercise shared native-pool reuse.
4. Confirm values do not copy into unrelated fields and callback effects do
   not multiply. Verify the final edit still commits before normal focus loss.
5. Review a fresh Player.log for exceptions and the prior stale-focus warning.
   This build adds no new diagnostic logging. A clean log alone does not prove
   the absence of unintended successful callbacks; visible state must be checked.

## Backup and build identity

Complete pre-repair package backup (47 files, hashes verified):
`C:\Users\jleas\AppData\Local\Temp\aumf-input-lifecycle-20260911-233056\baseline`.
Build, test executables, and final build log are in sibling `build`, `tests`,
and `build-final.log` paths. The pre-repair DLL SHA-256 is:
`1A1664605F4BCBB69BAA588E81D1868C5815DBA70D1455B866549D74D51904BD`.

Repaired build / installed DLL SHA-256:
`6E76464569AAE84CEACCF961CE62FDE12B29E901C88390437C017004FF8B42E8`.

Core `Game.dll` inspected during this repair, SHA-256:
`43F72D1242F1EE4323E839DF31984C754FCAF88C0B8E505A28C1C83372845162`.

Rollback, if required, must use the retained baseline with the game stopped.
Workshop publication and a future version increment remain separate decisions.
