# Changelog

All notable changes to this project are documented here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

**Two version numbers, deliberately decoupled.** The package version follows GitVersion and moves
on every fix. `schemaVersion` is hand-bumped and its major moves only on a breaking field change.
Every release records the exact Tekla Open API floor package it was compiled against, and any
schema movement gets its own `### Schema` subsection.

## [Unreleased]

### Added

- The library: `DumpWriter.Inspect` (curated document) and `DumpWriter.Bulk` (streaming NDJSON),
  over one set of extractors and two sinks, so the two modes cannot drift apart in what they emit.
- Extractors for Part, Beam, ContourPlate, PolyBeam, BoltGroup, BaseWeld, PolygonWeld, RebarGroup,
  SingleRebar, RebarMesh, BaseComponent, Connection, Detail, Seam and Assembly, each declaring its
  `create` and `derived` keys up front.
- `ExtractorRegistry`, which walks the CLR hierarchy so an unregistered subtype still produces a
  correct base-level record rather than nothing.
- Reflection fallback for unregistered types — **inspect mode only**, everything it emits landing
  under `derived`.
- Template attribute reading in three tiers: T0 (none), T1 (batched per object), T2 (whole-model
  report join above `ReportJoinThreshold`, with a 200-object sampled cross-check against T1).
- Runtime catalog discovery: `contentattributes*.lst` for template attributes,
  `<setname>.j<Number>` / `<setname>.<PluginName>` for component attributes. Nothing is baked in.
- `tekla-dump.exe` with `inspect`, `bulk`, `attrs` and `doctor`, documented exit codes, work-plane
  normalization, and a three-layer Open API binding story (GAC, running-process fallback,
  `--tekla-bin`).
- The macro bundle: `DumpSelection.cs` + `DumpSelection.png`, reflection-loading `TeklaDump.dll`,
  plus a `LangVersion 5` compile harness so CI catches what Tekla's macro compiler would fail on
  silently.
- `tekla-dump.exe` could not connect at all on a machine where the Open API was not in the GAC.
  `TeklaAssemblyResolver` only resolved names starting with `Tekla.`, but the remoting stack also
  needs BCL-shaped assemblies at the versions Tekla ships and redirects to in its own config
  (`System.Runtime.CompilerServices.Unsafe`, `System.ValueTuple`, Grpc). It now matches on the
  **simple name at any version** and probes Tekla's private paths (`ExternalDeps/Grpc`, `Teigha`,
  `OpenCascade`) as well as `bin`, because the bin's copy is by definition the one the running
  Tekla loaded. Copying Tekla's `.exe.config` instead does not work: its `codeBase` hrefs assume
  the process `ApplicationBase` is Tekla's `bin`. Verified against a live 2025 session —
  `doctor` reports `connected: True`, and `inspect` dumps 6235 beams.
- Generated JSON Schema in `schema/v1/`, with a staleness check in the test suite and in CI.

### Schema

- `1.0` — first published version. Field-by-field reference in `schema/v1/README.md`.

### Notes

- Compiled against Tekla Open API floor **2021.0.0** in Release.
- **`System.Memory` is 4.6.3, not the 4.5.5 named in older write-ups.** Both `Tekla.Structures`
  2021.0.0 and 2026.0.3 now declare a dependency on `System.Memory >= 4.6.3` (checked 9 Sep 2026),
  so 4.5.5 is a package downgrade that NuGet fails the build over on every matrix leg, floor
  included. The assembly version moves from 4.0.1.2 to 4.0.2.0 with it, which is why the CLI turns
  on `AutoGenerateBindingRedirects`.
- **Template attribute units are inferred from the attribute name, not read from the catalog.**
  `contentattributes*.lst` declares a datatype and no unit, so there is nothing to read. An
  unrecognised name is left untouched rather than converted on a guess; `--units native` switches
  the mechanism off entirely. See `schema/v1/README.md` §5.
- **Component attribute files are `<owner>_attributes.<NAME> <value>`**, whitespace separated —
  not `name=value` as earlier notes assumed. Checked against a Tekla 2026.0 installation; the two
  fixture files in `fixtures/component-attrs/` are real ones.

### Not done yet

- The three binding spikes (plugin host, CLI host, macro host) need a running Tekla and a clean
  machine, and gate the release rather than the code.
- Golden files need a Tekla installation to generate. `fixtures/golden/README.md` has the harness
  and the procedure; `fixtures/TeklaDumpFixture.zip` is not committed yet.
- The performance budget (200k objects under 3 minutes) is a hypothesis until it is measured
  against a real model. `fixtures/golden/README.md` records the full budget table.
