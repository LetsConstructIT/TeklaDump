# CLAUDE.md

TeklaDump serializes Tekla Structures model objects to a documented JSON/NDJSON schema with an
explicit **settable (`create`) vs derived** split, so an LLM can regenerate Open API code that
assigns only assignable properties. Read [README.md](README.md) for the product surface and
[schema/v1/README.md](schema/v1/README.md) for the field-by-field reference.

## Layout

- [source/TeklaDump/](source/TeklaDump/) — the library (net48, AnyCPU, NuGet package). The public
  surface is fourteen types, pinned in [PublicSurface.txt](source/TeklaDump/PublicSurface.txt):
  `DumpWriter`, `DumpOptions` + its five option enums, `DumpResult`/`DumpWarning`/`DumpProgress`,
  `JsonValue`/`JsonObject`/`JsonArray`, `SchemaVersion`. Everything else is `internal`
  (`InternalsVisibleTo` tests + SchemaGen + `tekla-dump`).
- [source/TeklaDump.Cli/](source/TeklaDump.Cli/) — `tekla-dump.exe` (x64), the only place that
  connects to a session, selects and filters.
- [source/TeklaDump.Macro/](source/TeklaDump.Macro/) — `DumpSelection.cs` macro bundle.
- [source/TeklaDump.SchemaGen/](source/TeklaDump.SchemaGen/) — generates `schema/v1/*.json` from
  the extractors' declared keys.
- [source/TeklaDump.Tests/](source/TeklaDump.Tests/) — xUnit v3 on Microsoft.Testing.Platform.

## Build and gates

```powershell
dotnet build TeklaDump.slnx
dotnet test --project source/TeklaDump.Tests/TeklaDump.Tests.csproj
./scripts/Generate-Schema.ps1                          # after changing an extractor's keys
./scripts/Check-SideEffects.ps1 -Path source -Quiet    # the no-model-writes gate
```

Tests need **no Tekla installation** — the Open API comes from NuGet. Anything that needs a live
model database belongs in the golden-file suite ([fixtures/golden/README.md](fixtures/golden/README.md)),
not here. CI compiles against every supported Tekla (2021→2026) plus the two gates above.

## Rules that are enforced, not just intended

- **No model writes, ever.** No `Insert()`, `Modify()`, `Delete()`, `SetUserProperty()`,
  `CommitChanges()` anywhere. `Check-SideEffects.ps1` fails the build over it.
- **A key is in `create` or `derived`, never both**, and nothing undeclared is written — asserted
  in [ExtractorContractTests.cs](source/TeklaDump.Tests/ExtractorContractTests.cs). Adding a field
  means declaring it in the extractor's `CreateKeys`/`DerivedKeys` and regenerating the schema.
- **The committed schema must match the generator's output** (`Generate-Schema.ps1 -Check`).
- **The public API surface is frozen** against
  [PublicSurface.txt](source/TeklaDump/PublicSurface.txt), asserted in
  [PublicSurfaceTests.cs](source/TeklaDump.Tests/PublicSurfaceTests.cs). A type that drifts to
  `public` fails the build. Widening it is a deliberate act: regenerate with
  `TEKLADUMP_APPROVE_PUBLIC_SURFACE=1`, commit, and record it in the CHANGELOG. Default to
  `internal` — opening a type later is non-breaking, closing one is not.
- **One binary for Tekla 2021–2026.** Release compiles against the 2021 floor; anything added in a
  later version is invoked by reflection via
  [OptionalTeklaApi](source/TeklaDump/Interop/OptionalTeklaApi.cs) and marked `Since` on the
  `DumpKey`. Never raise the floor to reach a newer member.
- **The library never queries.** No enumerating, selecting, filtering, refreshing, or work-plane
  changes — it serializes objects handed to it. `SessionInfo` is the sole exception (header
  metadata). Query logic goes in the CLI.
- [SchemaVersion.Current](source/TeklaDump/SchemaVersion.cs) is hand-bumped and independent of the
  GitVersion package version; MAJOR moves only on a breaking field change.

## Conventions

- `Directory.Build.props` holds the shared TFM/settings and `TeklaOpenApiVersion`; per-project
  csprojs carry long comments explaining *why* a reference is shaped the way it is — preserve and
  extend those rather than trimming them.
- Tekla packages are `ExcludeAssets="runtime"` everywhere except the tests and SchemaGen (which set
  `SkipSystemMemoryInjection`). Don't add runtime assets to the library or CLI.
- Explicit `using` directives (no implicit usings), nullable enabled, file-scoped namespaces.
- Record user-visible changes in [CHANGELOG.md](CHANGELOG.md).
