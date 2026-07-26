# CLI reference

Install:

```bash
dotnet tool install --global Snapshot.Cli
```

Main commands:

```text
snapshot build <source>
snapshot validate <artifact.zip>
snapshot validate-host <artifact.zip> <base-url>
snapshot inspect <artifact.zip>
snapshot extract <artifact.zip> <directory>
snapshot browser install
snapshot browser status
```

Run `snapshot help` for the complete option list. Important build overrides include output path, route inputs, discovery mode, case aliases, Windows target mode, concurrency, timeouts, retries, headed mode, custom browser executable, Netlify output, JSON reporting, and partial-artifact preservation.

The root gateway is enabled by default whenever `/` is discovered. Use `--no-root-gateway` to omit `/index/index.html` while preserving the original `/index.html` loader. See [Root gateway](root-gateway.md).
