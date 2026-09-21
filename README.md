# TeklaDump

Serialize Tekla Structures model objects to a **documented JSON schema** with an explicit
**settable-vs-derived split**, so an LLM can turn a real model back into Open API code that
assigns only the properties that can actually be assigned.

A Tekla model is a knowledge layer — beams, welds, rebar, connections that somebody already got
right. Getting that knowledge *out* in a form a model can use is hand-written boilerplate today,
and everybody writes it again. This does it once, with the two things a hand-rolled dump never has:
a stable documented schema, and a per-property statement of what you may assign.

```csharp
// Your project already references Tekla.Structures.Model — TeklaDump declares no Tekla
// dependency of its own, so if you don't have one you get a FileNotFoundException, not a
// NuGet error. That is the trade for one binary that works on Tekla 2021 through 2026.
using Tekla.Structures.Model;
using TeklaDump;

var beam = new Model().SelectModelObject(identifier);
var json = DumpWriter.Inspect(new[] { beam }).ToIndentedString();
```

---

## What this is not

Load-bearing, and above the fold on purpose.

- **Not a round-trip format.** The JSON does not recreate a model. Components generate their own
  output, many properties are derived, position offsets do not round-trip, and welds and rebar bind
  to their parents by identifier — identifiers that name objects in the *source* model. A `create`
  block is a starting point for code you write, not an importer payload.
- **Not an IFC/Speckle replacement.** No tessellation pipeline, no viewer, no federation, no sync
  protocol. That territory is well served and free.
- **Not a query engine.** The library never enumerates, selects, filters, or changes the work plane.
  It serializes objects you hand it. Filtering is the CLI's job, or your own LINQ. (It does read
  session metadata for the header through one class, `SessionInfo`, and nothing else in the library
  holds a `Model`.)
- **Not drawings**, in v1. Model objects only.
- **No model writes.** Ever. No `Insert()`, `Modify()`, `Delete()`, `SetUserProperty()` or
  `CommitChanges()` anywhere in the codebase, gated in CI by `scripts/Check-SideEffects.ps1`. The
  one thing that touches disk on your side is the T2 report join, which writes a temporary `.rpt`
  into the model folder and deletes it in a `finally` — a file, not the model.
- **No telemetry.** Nothing phones home.

---

## Three ways to use it

| Artifact | What it does | For |
|---|---|---|
| **`TeklaDump`** (NuGet, net48, AnyCPU) | `IEnumerable<ModelObject>` to JSON / NDJSON | plugin developers |
| **`tekla-dump.exe`** (net48, x64, standalone) | connects to a running session, selects, filters, writes files | power users, the LLM loop, CI |
| **`DumpSelection.cs` + `TeklaDump.dll`** (macro bundle) | select in Tekla, click, get `dump.json` on the Desktop | everyone else |

One repo, one version number, three release assets.

### Library

```csharp
// A handful of objects, curated, meant to be read: header + one record each.
JsonValue document = DumpWriter.Inspect(objects, options);

// Arbitrarily many, streaming NDJSON, no accumulation and no reflection.
DumpResult result = DumpWriter.Bulk(objects, outputStream, options);
```

That is the entire v1 surface. Fourteen public types in total: the two entry points, `DumpOptions`
and its five option enums, `DumpResult` with `DumpWarning` and `DumpProgress`, `JsonValue` with
`JsonObject` and `JsonArray`, and `SchemaVersion`. Everything else — the extractor contract, the
sinks, the dump context, the session reader, the attribute catalogs — is `internal`, and stays that
way: opening a type later is a non-breaking change, closing one is not.

The exact surface is committed in [`source/TeklaDump/PublicSurface.txt`](source/TeklaDump/PublicSurface.txt)
and a test fails if the assembly stops matching it.

### CLI

```
tekla-dump inspect  [--selected | --guid <g>... | --type <Beam,Bolt>] [-o out.json]
tekla-dump bulk     [--all | --type <...> | --phase <n> | --filter <name>] -o out.ndjson
tekla-dump attrs    --list [--for PART|ASSEMBLY|BOLT|...]
tekla-dump doctor
```

Exit codes: `0` ok, `1` unexpected error, `2` no Tekla session, `3` bad arguments, `4` completed
with warnings. JSON goes to stdout when `-o` is omitted, progress and notices to stderr, so the
tool pipes. `tekla-dump --help` has the full flag list.

Start with `tekla-dump doctor` — it reports the session, the version, how the Open API was
resolved, and the two facts that silently change what a dump means (work plane and numbering).
It works even when the Open API cannot be loaded at all, which is the failure it exists to
diagnose.

### Macro

See [`source/TeklaDump.Macro/README.md`](source/TeklaDump.Macro/README.md). Two files copied into
`macros\modeling\`; no installer, no admin rights.

---

## The output

```json
{
  "objectType": "Beam",
  "guid": "9F2C1A0E-...",
  "create": {
    "$ctor": "new Beam(Beam.BeamTypeEnum.BEAM)",
    "StartPoint": { "x": 0, "y": 0, "z": 0 },
    "EndPoint":   { "x": 6000, "y": 0, "z": 0 },
    "Profile":    "HEA300",
    "Material":   "S355J2",
    "Class":      "3",
    "Position":   { "Depth": "MIDDLE", "Plane": "MIDDLE", "Rotation": "TOP",
                    "DepthOffset": 0, "PlaneOffset": 0, "RotationOffset": 0 }
  },
  "derived": {
    "Type": "BEAM",
    "Assembly": { "objectType": "Assembly", "guid": "..." }
  },
  "userProperties": { "comment": "typ. floor beam" }
}
```

`create` is what you can assign, ordered as you would write the code. `derived` is informational —
`Beam.Type` is there because it is read-only after construction, so a generated script that assigns
it does not compile. **A property appears in exactly one of the two blocks**, declared up front per
extractor and asserted by a test.

The full field-by-field reference, the unit policy, the attribute tiers and the determinism rules
are in [`schema/v1/README.md`](schema/v1/README.md). The JSON Schema itself is generated from the
extractors and committed in [`schema/v1/`](schema/v1/).

---

## Things that will surprise you

Every one of these is a real Tekla behaviour, not a TeklaDump quirk:

- **Coordinates are in the current work plane.** A dump taken while a local plane is set
  regenerates in the wrong place. The header says `workPlane`; the CLI and macro normalize to
  global and restore afterwards.
- **Position and mark attributes are empty until numbering has run**, and there is no API to run
  it. The header says `numberingUpToDate`.
- **A UDA written with a mismatched type reads back wrong.** What you see is what Tekla returned.
- **Report values are not uniformly scaled** — `AREA` in m², `VOLUME` in mm³, same call. Normalized
  mode fixes that and declares the units once in the header; `--units native` gives you the raw
  values.
- **A component attribute the user never set is omitted**, because the component is using its own
  default and a regenerating script should not pin today's default into tomorrow's model.
- **Objects are dumped as handed in.** The library never refreshes them.

---

## Versions

One binary, Tekla **2021 through 2026**. It is compiled against the 2021 Open API floor and
declares no Tekla dependency at all, so your host supplies whatever version it is on — the same
mechanism that keeps old TSEP plugins running on new Tekla. Members added after the floor are
invoked by reflection at run time, so a newer Tekla lights up extra fields and an older one omits
them; the schema marks those fields.

There will never be a `TeklaDump.2024` / `.2025` / `.2026`.

---

## Building

```powershell
dotnet build TeklaDump.slnx
dotnet test  --project source/TeklaDump.Tests/TeklaDump.Tests.csproj

./scripts/Generate-Schema.ps1            # after changing an extractor's declared keys
./scripts/Check-SideEffects.ps1 -Path source -Quiet   # the no-model-writes gate
```

The test suite needs **no Tekla installation** — the Open API assemblies come from NuGet, and
everything that talks to the model database lives in the golden-file suite instead
([`fixtures/golden/README.md`](fixtures/golden/README.md)).

---

## Licence and status

MIT. Free forever, no paid tier, no telemetry.

If you write Tekla Open API code with an LLM, the
[`tekla-open-api` skill](https://github.com/grzegorz-olszewski) is the other half of the loop: this
gets real model data into the conversation, that gets correct code out of it.
