# Testing

The solution includes:

- pure route/path/diagnostic tests;
- archive generation, streaming, and extraction tests;
- a real Blazor WebAssembly test site;
- a ZIP-backed HTTP test host;
- opt-in Playwright end-to-end tests;
- synthetic alias/load planning.

Run normal tests:

```bash
dotnet test Snapshot.sln
```

Run the real browser test after installing Chromium:

```bash
snapshot browser install --with-deps
SNAPSHOT_E2E=1 dotnet test tests/Snapshot.EndToEndTests
```

The test host can simulate case-sensitive lookup, Windows lookup, duplicate ETags, unique ETags, and absent ETags.
