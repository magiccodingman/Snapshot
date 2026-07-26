# Root gateway

The root route (`/`) is the only Snapshot Protocol route that does not map directly over its ordinary web-server `index.html`.

## Output layout

```text
/index.html        Original SPA or WebAssembly loader
/index/index.html  Hydrated snapshot of route /
```

The loader is copied from the published source and remains untouched. This matters for applications such as Blazor WebAssembly because the original loader owns startup UI, framework initialization, and reliable client-side navigation. Replacing it with a rendered homepage can cause stale-page flashes, broken loading behavior, and hydration inconsistencies.

## Default behavior

If route `/` is discovered, Snapshot generates `/index/index.html` by default. The root snapshot still records `/` as its canonical application route. When a browser opens the physical gateway path, the client rewrites browser history to `/` before the application hydrates.

The gateway can be disabled without affecting the rest of the site:

```bash
snapshot build ./wwwroot --no-root-gateway
```

```csharp
RootGateway = new SnapshotRootGatewayOptions
{
    Enabled = false
}
```

Disabling the gateway means no generated snapshot is created for `/`. It never allows Snapshot to replace `/index.html`.

## Crawler discovery

Snapshot does not modify the developer's loader to advertise the gateway. Sites that deliberately want crawlers to discover it can add an optional link to their source `index.html`:

```html
<a href="/index/index.html"
   hidden
   aria-hidden="true"
   tabindex="-1">Snapshot root</a>
```

Exposing the gateway can make `/index/index.html` or `/index/` a crawler-visible entry point. The embedded canonical route lets browser users hydrate at `/`, but site owners should choose this intentionally and keep canonical metadata consistent.

Without the optional link, the site still works normally. A common alternative is to keep `/` lightweight—a short introduction, basic site metadata, and navigation to richer snapshotted pages.
