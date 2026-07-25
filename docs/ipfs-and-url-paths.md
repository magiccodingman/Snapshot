# IPFS and URL paths

The ZIP can be streamed unchanged as one object or streamed entry-by-entry into UnixFS/IPFS. Archive paths use forward slashes and preserve canonical casing.

For ordinary root-hosted SPAs, use `<base href="/">` and root-relative assets. A relative reference such as `scripts/app.js` resolves beneath the current snapshot folder.

IPFS subdomain gateways and DNSLink provide a real origin root. Legacy `/ipfs/CID/...` path gateways cause `/asset` references to escape the CID prefix, so they are not recommended for application hosting.
