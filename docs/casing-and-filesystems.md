# Casing and filesystems

Canonical casing is preserved exactly.

In case-sensitive mode, each segment produces a bounded meaningful family: original, first-letter lowercased, fully lowercase, first-letter uppercase over lowercase, and fully uppercase. Segment families are combined across the path. Arbitrary alternating-letter permutations are not generated.

There is no default alias-count limit. An optional maximum is available for constrained automation.

Missing route prefixes receive tiny `noindex,follow` gateway pages that link to known children and home. Existing source files always take precedence over snapshots, aliases, and gateways.

Windows target mode disables physical case aliases and checks canonical/source entries using case-insensitive path semantics and Windows filename rules.
