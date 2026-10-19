# Repository automation

Develop in the checkout, submit a PR, review its current commit, and let the required checks finish. These tools do not install mods, run Stardeus, or publish to Workshop. The live mod folders remain a separate deployment step under the active user's authorization.

## Install local privacy prevention

Prerequisites: Git, PowerShell 7, and the .NET 10 SDK. Run from a clean reviewed checkout connected to this repository:

```powershell
$ReviewedCommit = (git rev-parse HEAD).Trim() # Independently review this exact commit first.
pwsh -NoProfile -File build/RepositoryAutomation/Install-PrivacyHooks.ps1 -ReviewedCommit $ReviewedCommit
```

Installation is local to this checkout/worktree. Repeat it in each new clone or worktree. If Git copies an existing managed hook path into a linked worktree, the guard refuses operations until a dedicated installation rebinds that worktree; the parent installation stays intact. Git does not automatically enable hooks from a clone. The installer preserves unknown existing hooks and refuses conflicting `core.hooksPath` configuration. Resolve such a conflict explicitly rather than disabling another guard.

The installer builds committed source into private Git metadata and enables pre-commit and pre-push hooks. Dirty working files cannot replace the installed checker. Pre-commit reads exact index blobs. Pre-push reads every non-deletion tip supplied by Git and its complete reachable history, including earlier commits whose contents were subsequently removed, commit metadata, and annotated tag messages. Deletion-only pushes do not publish content. A failed or incomplete scan blocks the operation. No remote refs are fetched by the guard; do not fetch old PR refs as a privacy baseline.

When privacy or automation source changes, commit and independently review it, then rerun the installer for that exact commit before pushing. The hook refuses outdated installed source. Retained installation versions make diagnosis/recovery possible; they are private local files, not repository content. To undo installation, restore the recorded previous checkout-specific hook configuration; do not change global Git configuration.

Local hooks can be deliberately bypassed and cannot protect other unconfigured clones or browser uploads. Keep GitHub secret push protection enabled. CI repeats the policy after upload; it cannot undo an exposure that already happened.

## Required PR validation

Every PR targeting `main`, including documentation-only PRs, runs:

- Privacy checker structural self-tests, isolated Git integration tests, and exact-object scans. CI always uses the exact PR base revision's checker for its privacy decision before running candidate checker tests.
- All tracked JSON syntax checks with Core-compatible comments/trailing commas, strict root mod manifests, project XML, and PowerShell syntax.
- Existing isolated logic harnesses for Market Balancing, Missile Overhaul, Planning Overhaul, Improved Ship Pathing, and Skill Cap.

`Repository validation` is the stable required aggregate. It succeeds only if every underlying job succeeds; failure, cancellation, or a skipped prerequisite blocks merging. Actions use GitHub-hosted runners, read-only repository permissions, pinned official actions, and no private game dependencies or repository secrets. Changes to workflow or guard policy still require human/agent review of the exact commit.

These checks do not build all mods or prove Core/gameplay compatibility. Investor's game-dependent harness and AUMF's platform-dependent harness remain separate task-specific checks. Runtime changes still need their applicable installed-game build and validation.

## Reviewed auto-merge and updates

Auto-merge is opt-in for each reviewed PR. Existing PR protection and review-thread resolution remain enforced. A solo maintainer cannot approve their own GitHub PR; the exact-commit independent review is recorded in task evidence rather than replaced with an automatically approving bot.

The helper uses the existing Git Credential Manager sign-in for this repository. Credentials are never committed or printed. It accepts same-repository, open, non-draft PRs into `main` whose head branch matches the current clean checkout.

After independently reviewing the exact local and remote head:

```powershell
$PullRequest = [int](Read-Host 'PR number')
$ReviewedCommit = (git rev-parse HEAD).Trim()
pwsh -NoProfile -File build/RepositoryAutomation/Manage-PullRequest.ps1 `
    -Action EnableAutoMerge -PullRequest $PullRequest -ReviewedCommit $ReviewedCommit
```

GitHub merges when required checks and other rules permit it, then deletes the merged remote branch. If the PR is already mergeable, the helper performs a normal GitHub merge guarded by the reviewed SHA and the same repository protections. This is a squash merge; retain a local branch until its contents are confirmed in `main`. Unmerged branches are not removed automatically.

Before pushing any additional changes to an opted-in PR, use:

```powershell
pwsh -NoProfile -File build/RepositoryAutomation/Manage-PullRequest.ps1 `
    -Action Update -PullRequest $PullRequest
```

This disables and confirms auto-merge is off before pushing through the privacy hook. Independently review the new SHA and enable auto-merge again when ready. A private local marker also blocks accidental direct pushes to a branch armed through this checkout.

GitHub's `expectedHeadOid` prevents an activation-time race; it does not permanently bind queued auto-merge to a reviewed SHA. Other clones, write-enabled users, or browser changes can escape the local marker. Always disable queued auto-merge before updates from any location. Repository administrators retain their normal authority; this workflow is not a remote-enforced independent approval system.

## Recovery

Repair automation through a normal PR. Preserve the existing PR, no-force-push, no-deletion, and secret protection settings. If a broken required check blocks its own repair, document and restore only that check's last known-good configuration, retain PR protections, merge the reviewed repair, then restore the check. Do not force-push or rewrite history as an automation repair. Save configuration backups outside tracked content before changing repository settings.
## Routine maintenance

Dependabot checks GitHub Actions dependencies weekly and groups proposed updates into a workflow dependency PR. It preserves the existing review, privacy, required-check and opt-in merge process. Game and mod package dependencies are outside this update scope.

[MODS.md](MODS.md) and [.github/mod-labels.json](.github/mod-labels.json) are generated from tracked root manifests and Workshop ID files. After changing a manifest, adding/removing a mod, or changing a Workshop ID, stage new/removed input files and run:

```powershell
pwsh -NoProfile -File build/ModMaintenance/Update-ModCatalog.ps1
```

Commit both generated outputs with the change. CI checks freshness without writing; it also exercises catalog generation and the actual label workflow against isolated fixtures. Manifest versions remain authoritative. Catalog entries are not gameplay or compatibility certification.

After each PR check run, a separate default-branch workflow reads GitHub PR file metadata and adds labels for affected mods and shared repository automation. It uses the trusted default-branch map, verifies the current PR head, and never checks out PR code, downloads candidate artifacts, approves or merges. Manual labels remain intact; labels are added only, so an old mod label can remain after a later change removes that mod from the PR.

Repository labels are precreated to keep the workflow token limited to reading contents and labeling PRs. For a new mod, add its generated map through a normal PR and create the corresponding `mod:<folder>` repository label through repository settings. Its first PR may not receive the new label until the trusted map is merged; subsequent PRs are automatic. The workflow does not use `pull_request_target` or change its event policy.