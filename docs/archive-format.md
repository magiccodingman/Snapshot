# ZIP archive format

The ZIP root is the deployment root. No wrapper directory is added.

Every generated artifact contains `snapshot-manifest.json`, which records format/protocol versions, target filesystem semantics, route counts, entry roles, lengths, hashes, and canonical relationships.

Entry roles are source, snapshot, case alias, prefix gateway, and hosting artifact.

`SnapshotArchive` can:

- enumerate files and inferred directories;
- open one entry as a decompression stream;
- read small text or byte entries with safety limits;
- stream the complete ZIP unchanged;
- stream files one-by-one as a virtual folder;
- copy through `ISnapshotFileSink`;
- safely extract with traversal and collision checks.
