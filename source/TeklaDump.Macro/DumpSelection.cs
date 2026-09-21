// TeklaDump — one-click dump of the current selection.
//
// Install: copy this file and DumpSelection.png into <environment>\macros\modeling\, and put
// TeklaDump.dll in a TeklaDump\ subfolder next to them. Then: select objects in the model, run
// "DumpSelection" from Applications & components, and a JSON file lands on your Desktop.
//
// WHY THE REFLECTION. Tekla compiles a macro at run time against a FIXED reference set —
// Tekla.Structures.*, the BCL, and (not reliably) WPF. A macro cannot reference TeklaDump.dll at
// compile time, and an AssemblyResolve hook does not help: it fixes loading, not compiling. So the
// DLL is located on disk, loaded with Assembly.LoadFrom, and DumpWriter.Inspect is invoked through
// the ten-line shim below. The ModelObjects handed to it come from the Tekla assemblies already
// loaded in this process, so there is no type-identity problem, and the DLL's own
// Tekla.Structures.Model references bind exactly as they do inside a plugin.
//
// The skeleton is mandatory and unforgiving: namespace Tekla.Technology.Akit.UserScript, class
// Script, entry point public static void Run(Tekla.Technology.Akit.IScript). An uncaught exception
// in a macro fails SILENTLY — nothing happens, no message, no log — which is why everything below
// is wrapped and why every failure ends in a MessageBox.
//
// WinForms MessageBox, not WPF: PresentationFramework is often missing from the macro compiler's
// reference set, and a macro that will not compile is indistinguishable from one that did nothing.
//
// WHY THE PRAGMAS. The "fixed reference set" above is fixed per VERSION AND ENVIRONMENT, not fixed
// across them. Tekla 2026's host list no longer carries Tekla.Structures.dll, which 2024's does —
// so TeklaStructuresSettings.GetAdvancedOption below stopped compiling on 2026 while every other
// version stayed fine, and by the rule above that reaches the user as a macro that does nothing.
// #pragma reference makes the file carry its own list instead of inheriting one: the macro
// compiler starts from System + System.Core only, and each directive adds one assembly, by partial
// name (resolved out of Tekla's bin) or by full display name (the BCL). With these four the macro
// compiles with NO host list at all, on every supported version, so the host list can change again
// without anyone noticing at the wrong moment.

#pragma warning disable 1633
#pragma reference "Akit5"
#pragma reference "Tekla.Structures"
#pragma reference "Tekla.Structures.Model"
#pragma reference "System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
#pragma warning restore 1633

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Tekla.Structures;
using Tekla.Structures.Model;

// Tekla.Structures.Model has an Assembly class of its own, so the reflection one needs an alias.
// The macro compiler reports this as CS0104 and then the macro simply does not run.
using ReflectionAssembly = System.Reflection.Assembly;

// ModelObjectSelector exists in BOTH Tekla.Structures.Model and .Model.UI, and the UI one is the
// one that reads the user's current selection. Aliased rather than imported so the ambiguity
// cannot come back.
using UiSelector = Tekla.Structures.Model.UI.ModelObjectSelector;

namespace Tekla.Technology.Akit.UserScript
{
    public class Script
    {
        public static void Run(Tekla.Technology.Akit.IScript akit)
        {
            try
            {
                Dump();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "TeklaDump failed:" + Environment.NewLine + Environment.NewLine + exception.Message,
                    "TeklaDump",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static void Dump()
        {
            var model = new Model();
            if (!model.GetConnectionStatus())
            {
                MessageBox.Show("No model is open.", "TeklaDump");
                return;
            }

            var selected = GetSelectedObjects();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select some objects first, then run TeklaDump.", "TeklaDump");
                return;
            }

            var assembly = LoadTeklaDump();
            if (assembly == null)
            {
                MessageBox.Show(
                    "TeklaDump.dll was not found." + Environment.NewLine + Environment.NewLine +
                    "It should sit in a TeklaDump\\ folder next to this macro. Searched:" +
                    Environment.NewLine + string.Join(Environment.NewLine, SearchedPaths().ToArray()),
                    "TeklaDump");
                return;
            }

            // Open API coordinates are expressed in the CURRENT work plane, so a dump taken on a
            // local plane regenerates in the wrong place. Normalize, and restore in the finally
            // whatever happens — leaving a user on a plane they did not choose is worse than
            // failing.
            var workPlaneHandler = model.GetWorkPlaneHandler();
            var previousPlane = workPlaneHandler.GetCurrentTransformationPlane();
            string text;
            try
            {
                workPlaneHandler.SetCurrentTransformationPlane(new TransformationPlane());
                text = InvokeInspect(assembly, selected);
            }
            finally
            {
                workPlaneHandler.SetCurrentTransformationPlane(previousPlane);
            }

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "tekla-dump-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, text);

            MessageBox.Show(
                selected.Count + " object(s) written to:" + Environment.NewLine + Environment.NewLine + path,
                "TeklaDump",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static ArrayList GetSelectedObjects()
        {
            var objects = new ArrayList();
            var enumerator = new UiSelector().GetSelectedObjects();
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is ModelObject) objects.Add(enumerator.Current);
            }
            return objects;
        }

        /// <summary>
        /// The reflection shim. Calls DumpWriter.Inspect(IEnumerable&lt;ModelObject&gt;, DumpOptions)
        /// and renders the result with JsonValue.ToIndentedString(), all by name.
        /// </summary>
        private static string InvokeInspect(ReflectionAssembly assembly, ArrayList selected)
        {
            var writerType = assembly.GetType("TeklaDump.DumpWriter", true);
            var inspect = writerType.GetMethod("Inspect", BindingFlags.Public | BindingFlags.Static);
            if (inspect == null) throw new InvalidOperationException("TeklaDump.DumpWriter.Inspect was not found.");

            // The parameter is IEnumerable<ModelObject>; an ArrayList is not one, so the selection
            // is copied into a typed list.
            var objects = new List<ModelObject>(selected.Count);
            foreach (var item in selected)
            {
                var modelObject = item as ModelObject;
                if (modelObject != null) objects.Add(modelObject);
            }

            // null options = defaults: inspect mode, UDAs on, template attributes off, session
            // details off. The macro takes no arguments on purpose.
            var document = inspect.Invoke(null, new object[] { objects, null });
            if (document == null) throw new InvalidOperationException("Inspect returned nothing.");

            var toString = document.GetType().GetMethod("ToIndentedString", Type.EmptyTypes);
            if (toString == null) throw new InvalidOperationException("JsonValue.ToIndentedString was not found.");

            return (string)toString.Invoke(document, null);
        }

        private static ReflectionAssembly LoadTeklaDump()
        {
            foreach (var candidate in SearchedPaths())
            {
                try
                {
                    if (File.Exists(candidate)) return ReflectionAssembly.LoadFrom(candidate);
                }
                catch (Exception)
                {
                    // Unreadable or blocked (downloaded-file zone) — try the next location.
                }
            }

            return null;
        }

        /// <summary>
        /// Where the DLL may sit, most specific first. XS_MACRO_DIRECTORY is the advanced option
        /// that names the macro folder — its exact name is the one thing here that is UNVERIFIED
        /// across versions, so a failure to answer is not fatal and the file's own folder (found
        /// from this assembly) is tried as well.
        /// </summary>
        private static List<string> SearchedPaths()
        {
            var paths = new List<string>();

            foreach (var root in MacroDirectories())
            {
                if (string.IsNullOrEmpty(root)) continue;
                paths.Add(Path.Combine(root, "TeklaDump\\TeklaDump.dll"));
                paths.Add(Path.Combine(root, "modeling\\TeklaDump\\TeklaDump.dll"));
                paths.Add(Path.Combine(root, "TeklaDump.dll"));
            }

            return paths;
        }

        private static List<string> MacroDirectories()
        {
            var directories = new List<string>();

            var option = "";
            try
            {
                if (TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref option) &&
                    !string.IsNullOrEmpty(option))
                {
                    // The option can hold several semicolon-separated folders.
                    foreach (var part in option.Split(';'))
                    {
                        if (part.Trim().Length > 0) directories.Add(part.Trim());
                    }
                }
            }
            catch (Exception)
            {
                // The option name is unverified across versions; not answering is a normal outcome.
            }

            // The compiled macro's own location, which is the folder this file was compiled from.
            try
            {
                var location = ReflectionAssembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(location))
                {
                    var directory = Path.GetDirectoryName(location);
                    if (!string.IsNullOrEmpty(directory)) directories.Add(directory);
                }
            }
            catch (Exception)
            {
                // A macro compiled in memory has no location. Fall through to the model folder.
            }

            try
            {
                var modelPath = new Model().GetInfo().ModelPath;
                if (!string.IsNullOrEmpty(modelPath))
                {
                    directories.Add(Path.Combine(modelPath, "macros"));
                    directories.Add(modelPath);
                }
            }
            catch (Exception)
            {
                // No model, no model folder — the earlier candidates are all there is.
            }

            return directories;
        }
    }
}
