# The macro bundle

One-click dump of the current selection, for people who will never open a terminal.

## What ships

```
<environment>\macros\modeling\
├─ DumpSelection.cs        the macro (source; Tekla compiles it at run time)
├─ DumpSelection.png       96x96 catalog thumbnail — Tekla pairs a macro with a same-named PNG
└─ TeklaDump\
   └─ TeklaDump.dll        the library, reflection-loaded by the macro
```

Select objects, run **DumpSelection** from Applications & components, get
`tekla-dump-<timestamp>.json` on the Desktop. No arguments, no dialog, default options: inspect
mode, UDAs on, template attributes off, session details off.

## Why the macro cannot just reference the DLL

Tekla compiles a macro at run time against a **fixed reference set** — `Tekla.Structures.*`, the
BCL, and (not reliably) WPF. `TeklaDump.dll` is not in it and cannot be added, and an
`AssemblyResolve` hook does not help: that fixes *loading*, not *compiling*. So the macro finds the
DLL on disk, `Assembly.LoadFrom`s it, and calls `DumpWriter.Inspect` through a ten-line reflection
shim. The `ModelObject`s it passes come from the Tekla assemblies already loaded in the process, so
there is no type-identity problem.

Two constraints follow, and both are load-bearing:

- **WinForms `MessageBox`, never WPF.** `PresentationFramework` is often missing from the macro
  compiler's reference set, and a macro that will not compile behaves exactly like one that did
  nothing.
- **Everything is inside a `try`.** An uncaught exception in a macro fails *silently* — no message,
  no log, no window. Every failure path here ends in a message box that names the problem.

## The compile harness

`TeklaDump.Macro.csproj` compiles `DumpSelection.cs` as a library at **`LangVersion 5`**. Nothing
it produces is shipped. It exists so CI fails on a syntax error or a renamed Tekla member, instead
of the macro failing silently on a user's machine — it has already caught two `CS0104` ambiguities
(`Assembly` and `ModelObjectSelector` each exist twice in the namespaces a macro imports).

What it **cannot** tell you: whether Tekla's own macro compiler accepts the file. That compiler's
language level is undocumented and differs by version, which is why the source is written in a
deliberately old dialect. `LangVersion 5` is this harness enforcing that dialect.

## Release checks

Two things close that gap, and both should run before a release:

```powershell
# 1. Compile with Tekla's OWN macro compiler, per version, without starting Tekla.
#    Run it STRICT (no -LikeTekla): the macro carries its own #pragma reference list, so it must
#    compile against no host list at all. That is what keeps it working when a host list changes
#    - as 2026's did, dropping Tekla.Structures.dll and with it TeklaStructuresSettings.
pwsh <tekla-open-api-skill>/scripts/Test-TeklaMacro.ps1 -Macro ./DumpSelection.cs -TeklaVersion 2026.0

# 2. Smoke-test it INSIDE a running Tekla, unattended.
pwsh <tekla-open-api-skill>/scripts/Invoke-TeklaMacro.ps1 -Macro ./DumpSelection.cs `
     -TeklaVersion 2025.0 -OutFile ./dump.json
```

The second is possible because of `TEKLA_MACRO_OUT` (see `Run`): when that variable is set the
macro writes its JSON to that path instead of the Desktop and **opens no dialogs at all** —
failures go into the file with an `ERROR: ` prefix. Without it a macro whose every outcome is a
`MessageBox` cannot be driven by a script, because the first modal box blocks the caller and
Tekla with it. That is what kept this a manual step.

Verified on Tekla 2025 against the sample model: three selected beams in, 4.6 KB of JSON out,
1.26 s, no interaction.

## The two alternatives, and why this one

| Option | Trade |
|---|---|
| **A. Reflection-loaded DLL** *(this one)* | Two files copied together. No installer, no admin rights. The macro and the DLL are released as one bundle and must stay in step. |
| B. Source-merged single file | Genuinely one file — but only if Tekla's macro compiler accepts the language level the library is written in. If it is C# 5 on any supported version, B is dead, and the library is not going to be written in C# 5 for the sake of one artifact. |
| C. TSEP with a ribbon button | Zero macro constraints, but an installer and admin rights — which is the friction the macro exists to avoid. |

## Unverified

`XS_MACRO_DIRECTORY` as the name of the advanced option that holds the macro folder. The macro
treats it as a hint rather than a fact: it tries that option, then the compiled macro's own
location, then the model folder, and the "not found" message lists every path it looked at.

## Trademarks

Tekla and Tekla Structures are trademarks of Trimble Inc. TeklaDump is an independent project. It
isn't affiliated with or endorsed by Trimble.
