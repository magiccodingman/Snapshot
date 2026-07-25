# Release process

`main` is upstream development. `release` is the protected stable source.

Before promotion, bump `eng/Versions.props` when publishable .NET code changes and bump `client/package.json` when the browser client changes. Update `CHANGELOG.md` in the same promotion work.

A pull request from `main` to `release` runs release guards. After merge, path-aware workflows publish only the affected packages.

- `publish-nuget.yml` uses NuGet trusted publishing with the `release` environment and `NUGET_USER`.
- `publish-npm.yml` uses `NPM_ACCESS_TOKEN` and publishes the built client package.

GitHub releases and tags are created manually when a stable commit deserves an archival milestone and curated release notes.
