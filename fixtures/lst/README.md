# Template attribute fixtures

Real `contentattributes*.lst` files, so the parser is tested against what Tekla actually ships
rather than against a reconstruction of it.

| File | What it is |
|---|---|
| `contentattributes.lst` | Verbatim from a Tekla 2026.0 installation. A **container**: a handful of `[INCLUDE]` lines and an empty `[BINDINGS]` section. Several stock environments ship exactly this, and a parser that ignores `[INCLUDE]` resolves the whole environment to nothing. |
| `contentattributes_global_excerpt.lst` | Real lines from the 2026.0 `contentattributes_global.lst`, trimmed: the definition lines for the attributes bound below, then a slice of the PART / BOLT / ASSEMBLY bindings — the ones a dump actually asks for (`PROFILE`, `WEIGHT`, `PART_POS`, ...) plus enough neighbours to exercise grouping and tier classification. The full file is 1.2 MB and 24k lines, which is more than a fixture needs. |

The excerpt keeps at least one of each shape that matters:

- **direct** attributes (`WEIGHT`, `PROFILE`) — cheap, always read;
- **constituent** groups (`NUT.`, `WASHER.`, `PROFILE.`, `MATERIAL.`) — read in the `associated`
  scope, because they cost about what reading the object itself costs;
- **object-traversal** groups (`ASSEMBLY.`) — held back for `full`, because each one re-exposes
  another object's entire attribute set.

Tests that need a shape these files do not contain (a circular `[INCLUDE]`, an ANSI-encoded file, a
binding with no datatype anywhere) build it in a temp folder instead: those shapes are a few lines
each, and a suite that only passes on a machine with the right environment installed is not a suite
anyone can rely on.
