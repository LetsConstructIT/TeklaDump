# Golden files

Committed expected output, per schema version. A golden diff in a pull request is a **public
interface change** and must come with a CHANGELOG entry under `### Schema`.

**Not generated yet.** These need a Tekla installation and a model, so they cannot be produced on a
CI runner or by anyone without one. The procedure below is the whole of it.

## What is missing

| File | What it should be |
|---|---|
| `../TeklaDumpFixture.zip` | A ~25-object model, zipped and committed: beam, contour plate, polybeam, bolt group, weld, rebar group, a **numbered connection with non-default attributes**, a **plugin component**, and an assembly. Small enough to commit, wide enough that every extractor has something to say. |
| `inspect.json` | `tekla-dump inspect --all` over the fixture, masked (below). |
| `bulk.ndjson` | `tekla-dump bulk --all` over the fixture, masked. |

## Generating them

```powershell
# 1. Unzip fixtures/TeklaDumpFixture.zip somewhere and open it in Tekla Structures.
# 2. Run numbering — position and mark attributes are empty until you do, and a golden file
#    recorded with stale numbering bakes that staleness in.
# 3. From the repo root:
dotnet build TeklaDump.slnx -c Release
$exe = "source/TeklaDump.Cli/bin/Release/tekla-dump.exe"

& $exe inspect --all --attrs-scope associated -o fixtures/golden/inspect.json
& $exe bulk    --all --attrs-scope associated -o fixtures/golden/bulk.ndjson
```

## The mask

Exactly five header fields may legitimately differ between two runs on the same model:

```
generatedAt   tool   buildNumber   modelPath   user
```

The diff masks those and **nothing else**. Everything else is deterministic by construction — see
`schema/v1/README.md` §8 — and a difference anywhere else is a real change in behaviour, which is
the entire point of having these files.

## What they catch that the unit tests cannot

The unit suite runs against uninserted objects, which are plain CLR objects: no database, no
numbering, no components that have actually built anything. Golden files are the only place where

- a connection's discovered component attributes are real,
- template attributes come back with real values from a real environment,
- `Identifier.GUID` resolution through the model is exercised,
- and the T2 report join runs end to end against Tekla's own report engine.

Also golden-only, and to be recorded per release in the CHANGELOG:

- **T2 vs T1 sampled equality** on the fixture and on a large model.
- **Performance smoke** against a large real model, against the budget below.

## The performance budget

The numbers a release is measured against, none of them verified yet:

| Run | Budget |
|---|---|
| 200k objects, identity and catalog properties | under 3 minutes, under 500 MB |
| the same plus a T2 join of five attributes | under 6 minutes |
| a 25-object inspect dump with associated attributes | under 2 seconds |
