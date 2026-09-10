# TeklaDump schema v1

Two documents, one record shape:

| File | Produced by | Shape |
|---|---|---|
| `inspect.schema.json` | `DumpWriter.Inspect`, `tekla-dump inspect` | One JSON document: `header`, `objects[]`, and `warnings[]` when there were any. |
| `bulk.schema.json` | `DumpWriter.Bulk`, `tekla-dump bulk` | NDJSON. Line 1 is the header; every later line is one record. |

**Both files are generated** from the extractors' declared keys by `scripts/Generate-Schema.ps1`.
Do not hand-edit them: edit the extractor, regenerate, and add a CHANGELOG entry under `### Schema`.
The test suite fails if the committed files are stale.

`schemaVersion` is hand-bumped and decoupled from the package version. Its **major** moves only on
a breaking field change; the package version moves on every fix. Pin behaviour to `schemaVersion`.

---

## 1. What this is not

Read this before writing anything that consumes the output.

**It is not a round-trip format.** The JSON does not recreate a model. Components generate their
own output, many properties are derived, position offsets do not round-trip, and welds and rebar
bind to their parents by identifier — identifiers that name objects in the **source** model and
mean nothing in the model your generated code runs against. A `create` block is a *starting point
for code you write*, never an importer payload.

**It is not a query result.** The library serializes the objects it is handed, in the order it is
handed them. It never enumerates, selects or filters.

**Objects are dumped as handed in.** The library never calls `Select()` to refresh them. A caller
holding stale objects gets stale values.

---

## 2. Casing

An LLM reading the file can tell instantly which keys map to API members and which are ours:

| Kind | Casing | Examples |
|---|---|---|
| Envelope and tool keys | `camelCase` | `objectType`, `schemaVersion`, `create`, `derived`, `userProperties`, `templateAttributes`, `componentAttributes` |
| Tekla-derived keys | verbatim `PascalCase` | `StartPoint`, `Profile`, `AssemblyNumber` |
| Attribute names | verbatim, whatever Tekla calls them | `ASSEMBLY.WEIGHT`, `USER_FIELD_1`, `bolt_size` |

`$ctor` and `$fallback` are the two `$`-prefixed keys; both are hints for a human or an LLM to
read, and no tool should parse them.

---

## 3. The header

```json
{"schemaVersion":"1.0","domain":"model","tool":"tekla-dump 1.0.0","generatedAt":"2026-09-09T14:12:03Z",
 "teklaVersion":"2026.0","buildNumber":"...","environment":"default","role":"steel",
 "modelName":"BridgeX","currentPhase":1,"sharedModel":false,
 "workPlane":"global","numberingUpToDate":true,
 "unitPolicy":"normalized","units":{"length":"mm","area":"mm2","volume":"mm3","mass":"kg","angle":"deg"},
 "attributeTier":"T2","attributeSet":["ASSEMBLY.WEIGHT","PART_POS"],"objectCount":214883}
```

| Field | Meaning |
|---|---|
| `schemaVersion` | This document. Pin to it. |
| `domain` | Always `"model"` in v1. Reserves room for a drawing dump as a separate document type without inviting one. |
| `tool` | `tekla-dump <package version>`. |
| `generatedAt` | UTC, second precision. |
| `teklaVersion`, `buildNumber` | From `TeklaStructuresInfo`. |
| `environment`, `role` | **Best effort.** The advanced-option names for these are unverified; several candidates are tried and the field is omitted when none answers, rather than a guess being stamped as fact. |
| `modelName` | From `Model.GetInfo()`. There is no model GUID on `ModelInfo`, so there is no `modelGuid`. |
| `modelPath`, `user` | **Only when `IncludeSessionDetails` is on.** Off by default for `inspect`, on for CLI `bulk`. An inspect file is meant to be pasted into a chat, and a customer's folder path and a Tekla user name do not belong in one. |
| `currentPhase`, `sharedModel` | From `Model.GetInfo()`. |
| `workPlane` | `global`, `custom` or `unknown`. When `custom`, `workPlaneOrigin` and `workPlaneAxes` follow. **This one matters:** Open API coordinates are expressed in the current work plane, so a dump taken on a local plane regenerates in the wrong place. The CLI and the macro switch to global first and restore afterwards; the library only reports what it saw. |
| `numberingUpToDate` | When `false`, position and mark attributes (`PART_POS`, `ASSEMBLY_POS`, the bolt fields) are empty or stale. There is no API to run numbering. |
| `unitPolicy`, `units` | See §5. |
| `attributeTier` | `T0`, `T1` or `T2` — how template attributes were read. See §6. |
| `attributeSet` | The attribute names the run asked for, sorted. |
| `objectCount` | Present only when the count was known before writing started. A streamed bulk run over a lazy sequence longer than the report-join threshold does not know it, and omits the field rather than guessing. |

---

## 4. A record

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
    "Name":       "BEAM",
    "Position":   { "Depth": "MIDDLE", "Plane": "MIDDLE", "Rotation": "TOP",
                    "DepthOffset": 0, "PlaneOffset": 0, "RotationOffset": 0 },
    "PartNumber":     { "Prefix": "P", "StartNumber": 1 },
    "AssemblyNumber": { "Prefix": "A", "StartNumber": 1 }
  },
  "derived": {
    "Type": "BEAM",
    "Assembly": { "objectType": "Assembly", "guid": "..." },
    "Phase": 1
  },
  "userProperties": { "comment": "typ. floor beam", "USER_FIELD_1": "PH2" },
  "templateAttributes": { "ASSEMBLY.WEIGHT": 1204.5 }
}
```

### create vs derived — the whole point

**A property appears in exactly one of the two blocks.** Which one is decided per extractor,
declared up front in `CreateKeys` / `DerivedKeys`, and asserted by a test that fails on any overlap
and on any emitted key that was never declared.

- `create` — properties you can actually assign when recreating the object, in the order you would
  write the code: the constructor (`$ctor`), then what the object cannot exist without (a beam's
  two points), then the catalog properties any part has.
- `derived` — strictly informational. Assigning any of it is either impossible or a mistake.
  `Beam.Type` is the canonical example: it is read-only after construction, so a script that
  assigns it does not compile — which is why it lives in `derived` while the constructor hint in
  `create` carries the same fact in usable form.

That split is the anti-round-trip-trap mechanism. It is why generated code assigns only what can
be assigned.

### Keys every record can carry

| Key | Notes |
|---|---|
| `objectType` | The CLR simple name (`Beam`, `ContourPlate`, `BoltArray`). Always present. |
| `guid` | Stable identity, from `Identifier.GUID`, resolved through the model when the identifier is ID-based. **Absent** for an uninserted object, which has none. |
| `create`, `derived` | Above. `derived` is absent under `--no-derived`. |
| `userProperties` | UDAs from `GetAllUserProperties`, sorted ordinally by name. |
| `templateAttributes` | Template (report) attributes, verbatim names, sorted. Absent at tier T0. |
| `componentAttributes` | Components only. The attributes the user actually **set** — see §7. |

An empty section never appears: no user properties means no `userProperties` key, not an empty
object.

### References

A related object is emitted as `{ "objectType": ..., "guid": ... }` and **never followed**. That is
what keeps a dump of one weld from dragging in the whole model, and why cycles are a non-issue
rather than something a cycle detector has to catch.

### The reflection fallback

Inspect mode only. When no extractor is registered for a type, its public members are read by
reflection and written to `derived` alongside a `$fallback` note naming the type. Everything from
that path is derived by definition: a reflected value has not been checked for whether it can be
assigned back. **Bulk mode never does this** — a reflection walker calls every public getter it
finds, and on a Tekla object some of those compute a solid or walk the model graph.

---

## 5. Units

Report and template values are **not** uniformly scaled in Tekla: `AREA` comes back in m² while
`VOLUME` comes back in mm³, in the same environment, in the same call. Two policies:

**`normalized`** (default) — mm, mm², mm³, kg, degrees, declared **once** in the header's `units`
block. Values are bare numbers everywhere. The only per-value stamp is the object form:

```json
"DENSITY": { "value": 7850, "unit": "kg/m3" }
```

used **only** for an attribute whose unit is known but is not one of the five normalizable kinds,
so it cannot be mistaken for one of them. Bare numbers are also used for genuinely unitless values
(class, counts, enum ordinals).

**`native`** — untouched Tekla values, `"unitPolicy":"native"`, no `units` block, no stamps.

### Known limitation, stated rather than papered over

The unit *kind* of a template attribute is inferred from its **name** (`WEIGHT` → mass,
`ASSEMBLY.WEIGHT` → mass, `AREA_GROSS` → area). It is not read from the catalog, because the
catalog does not carry it: `contentattributes*.lst` declares a datatype (CHARACTER / FLOAT /
INTEGER) and no unit. The inference is right for the stock catalog and can be wrong for a
hand-authored attribute whose name resembles a stock one. Two consequences, both deliberate:

- an unrecognised name normalizes **nothing** — bare number, no claim made;
- `--units native` switches the whole mechanism off.

Conversion factors live in one table in `Units.cs` with the source of each commented.

---

## 6. Attribute tiers

Stamped in the header and in `DumpResult.AttributeTier`, so output is explainable after the fact.

| Tier | What it does | When |
|---|---|---|
| **T0** | No template attributes. | Default. |
| **T1** | Batched per object: one `GetAllReportProperties` call with the full name list — **3 interop calls per object, not 3×N**. Report reads cannot batch *across* objects, only across names. | Attributes requested, object count at or below `ReportJoinThreshold` (default 5000). |
| **T2** | One whole-model report, joined on GUID. Orders of magnitude faster than per-object interop at scale. | Attributes requested, count above the threshold. |

### What T2 actually does

Generates a uniquely named `.rpt` in the **model folder**, confirms Tekla resolves it, runs
`Operation.CreateReportFromAll` to a temp file, parses the delimited output, joins on GUID, and
deletes both files in a `finally`.

Things worth knowing before you turn it on:

- **It is always whole-model.** The report covers every object in the model even if 5,001 of
  300,000 were handed in; rows outside the input set are never looked up. The alternative,
  `CreateReportFromSelected`, needs a UI selection and is therefore off-limits to the library.
- **It writes a file into the customer's model folder.** A file, not the model. Uniquely named,
  deleted in a `finally`; a cleanup failure is a warning naming the leftover file, not an exception.
- **One row per content type.** Each requested attribute must be valid for that content type, which
  the `.lst` catalog knows. An attribute that does not apply is left out of that row rather than
  failing the whole report.
- **It is checked against itself.** After the join, 200 sampled objects are re-read through T1 and
  compared; a mismatch is a warning carrying both values. This is what turns "the report path is
  environment dependent" into something visible rather than silent.
- **Every failure falls back to T1**, warns, and still produces a dump.

---

## 7. Where attribute names come from

Nothing is baked in. A shipped catalog would be wrong in US / DE / custom environments and would
need maintaining every release.

**Template attributes** — parsed from the running environment's own `contentattributes*.lst`,
following `[INCLUDE]` directives, in Tekla's override order (model, project, firm, system, Template
Editor settings). The datatype column is what makes the batched read possible at all.

**UDAs** — `GetAllUserProperties` needs no `objects.inp` declaration and returns everything set on
the object, in one call. Note the classic Tekla trap: a UDA *written* with a mismatched type reads
back wrong. What appears here is what Tekla returned.

**Component attributes** — the gap, and the largest open risk in this schema. `BaseComponent`
exposes `GetAttribute(name, ref int|double|string)` and nothing that lists names, so the universe is
discovered from the environment's saved attribute files: `<setname>.j<Number>` for a numbered system
component, `<setname>.<PluginName>` for a plugin. The real format, checked against a 2026
installation, is `<owner>_attributes.<NAME> <value>` per line — whitespace separated, and the
queryable name is the part after the dot.

`GetAttribute` returning `false` means the user never set that attribute and the component is using
its internal default. **Those are omitted on purpose**: they are exactly the set a regenerating
script should not assign, because assigning them pins today's default into tomorrow's model.

When discovery finds nothing, the component still dumps (identity, input, name and number); only
the attribute block is missing, a warning names the component, and `--component-attrs <file>` is
the escape hatch.

---

## 8. Determinism

What makes golden files work:

- Key order within a record is the extractor's **declared** order. `userProperties`,
  `templateAttributes` and `componentAttributes` are sorted ordinally by key.
- Objects are written in the order handed in. The CLI sorts its selection by GUID before calling
  the library, so two runs of the same command diff clean.
- Doubles are rounded to 6 decimals and written invariant-culture. Tekla's own internal precision
  is coarser than that, and unrounded doubles are what make two runs differ in the last mantissa bit.
- `generatedAt`, `tool`, `buildNumber`, `modelPath` and `user` are the **only** header fields that
  may differ between two runs on the same model. The golden diff masks exactly those and nothing
  else.
- Nothing in a record depends on the session `Identifier.ID`. Only GUIDs are emitted.

---

## 9. Warning codes

Stable and greppable. In inspect mode they ride in the document under `warnings`; in bulk mode they
come back in `DumpResult.Warnings` and are printed to stderr.

| Code | Meaning |
|---|---|
| `object-skipped` | One object's extractor threw. That record is skipped; the run continues. |
| `uda-read-failed` | `GetAllUserProperties` failed for one object. |
| `template-read-failed` | The batched report read failed for one object. |
| `template-catalog-empty` | No `contentattributes*.lst` was found, so no attribute can be named. |
| `template-attributes-truncated` | `MaxTemplateAttributes` cut related-object attributes for a content type. Direct attributes are never cut. |
| `component-attributes-not-discovered` | No saved attribute file was found for a component or plugin. |
| `numbering-not-up-to-date` | A position or mark attribute was requested while numbering is stale. |
| `report-join-*` | The T2 path: `name-collision`, `template-unresolved`, `failed`, `output-missing`, `malformed-rows`, `empty`, `error`, `cleanup-failed`, `mismatch`. All but `mismatch` and `cleanup-failed` mean the run fell back to T1. |
| `report-join-mismatch` | A sampled object's joined value differs from a direct read. Treat that attribute as suspect. |
| `no-extractor` | Bulk mode met a type with no extractor and reflection off. Not reachable with the shipped registry. |
