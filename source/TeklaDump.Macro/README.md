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
deliberately old dialect and why the macro must be smoke-tested in a real Tekla before a release.
`LangVersion 5` is this harness enforcing that dialect.

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
