# Release process

`main` is upstream development. `release` is the protected stable source. A pull request from `main` to `release` runs release guards, and publishing begins only after that promotion is merged.

## npm browser client

The browser package is `@magiccodingman/snapshot-protocol`. Its release version is the `version` field in `client/package.json`.

The release guard requires a client version bump when publishable client inputs change:

- `client/src/**`
- `client/package.json`
- `client/package-lock.json`
- `client/build.mjs`

The npm workflow is triggered only by `client/**` changes on `release`, or manually with `workflow_dispatch`. It does not run for root README, license, or logo changes. Those files are copied into the package during its next intentional client release.

Before publishing, the workflow installs dependencies, tests, and builds the client. It checks whether the exact package version already exists and skips it when present. New versions publish through npm Trusted Publishing and GitHub OIDC; no long-lived npm publish token is stored.

Configure the npm trusted publisher with:

```text
GitHub owner: magiccodingman
Repository: Snapshot
Workflow filename: publish-npm.yml
Environment: release
Allowed action: npm publish
```

The workflow uses Node 24 because npm trusted publishing requires a current Node and npm CLI. After the trusted publisher is verified, package settings may disallow traditional publish tokens without blocking OIDC publishing.

The recommended CDN URL for most applications follows the newest published release:

```text
https://cdn.jsdelivr.net/npm/@magiccodingman/snapshot-protocol@latest/dist/snapshot-protocol.min.js
```

Use an exact package version instead of `@latest` when a deployment must remain permanently pinned.

## NuGet packages

The four .NET packages release independently. Their versions are declared in `eng/Versions.props`:

| Package | Version property |
| --- | --- |
| `Snapshot.Protocol` | `SnapshotProtocolPackageVersion` |
| `Snapshot.Processing` | `SnapshotProcessingPackageVersion` |
| `Snapshot.Playwright` | `SnapshotPlaywrightPackageVersion` |
| `Snapshot.Cli` | `SnapshotCliPackageVersion` |

A source change under one package directory requires only that package's version to be bumped:

```text
src/Snapshot.Protocol/**   -> SnapshotProtocolPackageVersion
src/Snapshot.Processing/** -> SnapshotProcessingPackageVersion
src/Snapshot.Playwright/** -> SnapshotPlaywrightPackageVersion
src/Snapshot.Cli/**        -> SnapshotCliPackageVersion
```

The NuGet workflow still restores, tests, and packs the complete package set so project-reference and CLI installation problems are caught together. Publishing is independent: it checks each package ID and version on NuGet.org and pushes only versions that do not already exist. Bumping one package does not manufacture new releases for the other three.

Changes to `Directory.Build.props`, `Directory.Packages.props`, or other shared build inputs run the workflow for validation, but they do not automatically force every package version to change. Bump only the package versions that should be released. README, license, and logo edits do not trigger NuGet publishing; they are included with the next intentionally versioned release. `workflow_dispatch` is available when the release automation itself needs to be rerun.

`publish-nuget.yml` uses NuGet Trusted Publishing through the `release` GitHub environment. Configure the NuGet.org policy with the repository owner, repository, workflow filename `publish-nuget.yml`, and environment `release`. The `NUGET_USER` GitHub secret contains only the NuGet.org profile name; `NuGet/login@v1` exchanges GitHub OIDC for a short-lived API key during the job.

## Promotion checklist

Before promoting `main` to `release`:

1. Bump `client/package.json` only when publishing a new browser-client release.
2. Bump only the NuGet package version properties whose packages should publish.
3. Update `CHANGELOG.md` for the intended releases.
4. Confirm CI and the release guard pass.
5. Merge the promotion PR into `release`.

GitHub releases and immutable tags remain manually authored archival milestones with curated release notes.
