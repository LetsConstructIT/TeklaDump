using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TeklaDump.Cli;

/// <summary>What the user asked for. Parsing is separate from doing so it can be tested.</summary>
internal sealed class CommandLine
{
    public string Command { get; private set; } = string.Empty;

    public bool Selected { get; private set; }
    public bool All { get; private set; }
    public IReadOnlyList<string> Guids { get; private set; } = new string[0];
    public IReadOnlyList<string> Types { get; private set; } = new string[0];
    public int? Phase { get; private set; }
    public string? Filter { get; private set; }
    public string? Output { get; private set; }

    public IReadOnlyList<string>? Attributes { get; private set; }
    public string? AttributeScope { get; private set; }
    public string? ComponentAttributesFile { get; private set; }
    public string? Geometry { get; private set; }
    public string? Units { get; private set; }
    public bool NoDerived { get; private set; }
    public bool? SessionDetails { get; private set; }
    public int? ReportJoinThreshold { get; private set; }
    public int? MaxTemplateAttributes { get; private set; }
    public string? TeklaBin { get; private set; }
    public bool Pretty { get; private set; }
    public bool Quiet { get; private set; }
    public bool Progress { get; private set; }
    public bool Help { get; private set; }
    public bool Version { get; private set; }

    /// <summary>For <c>attrs</c>: which content type to list.</summary>
    public string? For { get; private set; }

    public bool List { get; private set; }

    /// <summary>Non-null when the arguments could not be understood; the message is user-facing.</summary>
    public string? Error { get; private set; }

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        if (args.Length == 0)
        {
            result.Help = true;
            return result;
        }

        var guids = new List<string>();
        var types = new List<string>();
        var index = 0;

        if (!args[0].StartsWith("-", StringComparison.Ordinal))
        {
            result.Command = args[0];
            index = 1;
        }

        for (; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "-h":
                case "--help": result.Help = true; break;
                case "--version": result.Version = true; break;
                case "--selected": result.Selected = true; break;
                case "--all": result.All = true; break;
                case "--no-derived": result.NoDerived = true; break;
                case "--session-details": result.SessionDetails = true; break;
                case "--no-session-details": result.SessionDetails = false; break;
                case "--pretty": result.Pretty = true; break;
                case "--quiet": result.Quiet = true; break;
                case "--progress": result.Progress = true; break;
                case "--list": result.List = true; break;

                case "--guid":
                    if (!TryValue(args, ref index, arg, result, out var guid)) return result;
                    guids.AddRange(Split(guid));
                    break;

                case "--type":
                    if (!TryValue(args, ref index, arg, result, out var type)) return result;
                    types.AddRange(Split(type));
                    break;

                case "-o":
                case "--output":
                    if (!TryValue(args, ref index, arg, result, out var output)) return result;
                    result.Output = output;
                    break;

                case "--phase":
                    if (!TryValue(args, ref index, arg, result, out var phase)) return result;
                    if (!int.TryParse(phase, NumberStyles.Integer, CultureInfo.InvariantCulture, out var phaseNumber))
                        return Fail(result, "--phase needs a number, got '" + phase + "'.");
                    result.Phase = phaseNumber;
                    break;

                case "--filter":
                    if (!TryValue(args, ref index, arg, result, out var filter)) return result;
                    result.Filter = filter;
                    break;

                case "--attrs":
                    if (!TryValue(args, ref index, arg, result, out var attrs)) return result;
                    result.Attributes = Split(attrs);
                    break;

                case "--attrs-scope":
                    if (!TryValue(args, ref index, arg, result, out var scope)) return result;
                    if (!IsOneOf(scope, "none", "associated", "full"))
                        return Fail(result, "--attrs-scope must be none, associated or full.");
                    result.AttributeScope = scope.ToLowerInvariant();
                    break;

                case "--component-attrs":
                    if (!TryValue(args, ref index, arg, result, out var componentAttrs)) return result;
                    result.ComponentAttributesFile = componentAttrs;
                    break;

                case "--geometry":
                    if (!TryValue(args, ref index, arg, result, out var geometry)) return result;
                    if (!IsOneOf(geometry, "none", "points", "solids"))
                        return Fail(result, "--geometry must be none, points or solids.");
                    result.Geometry = geometry.ToLowerInvariant();
                    break;

                case "--units":
                    if (!TryValue(args, ref index, arg, result, out var units)) return result;
                    if (!IsOneOf(units, "normalized", "native"))
                        return Fail(result, "--units must be normalized or native.");
                    result.Units = units.ToLowerInvariant();
                    break;

                case "--report-join-threshold":
                    if (!TryValue(args, ref index, arg, result, out var threshold)) return result;
                    if (!int.TryParse(threshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out var thresholdValue))
                        return Fail(result, "--report-join-threshold needs a number.");
                    result.ReportJoinThreshold = thresholdValue;
                    break;

                case "--max-template-attrs":
                    if (!TryValue(args, ref index, arg, result, out var max)) return result;
                    if (!int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxValue))
                        return Fail(result, "--max-template-attrs needs a number.");
                    result.MaxTemplateAttributes = maxValue;
                    break;

                case "--tekla-bin":
                    if (!TryValue(args, ref index, arg, result, out var bin)) return result;
                    result.TeklaBin = bin;
                    break;

                case "--for":
                    if (!TryValue(args, ref index, arg, result, out var contentType)) return result;
                    result.For = contentType.ToUpperInvariant();
                    break;

                default:
                    return Fail(result, "Unknown option '" + arg + "'. Run tekla-dump --help.");
            }
        }

        result.Guids = guids;
        result.Types = types;
        return result;
    }

    private static bool TryValue(string[] args, ref int index, string option, CommandLine result, out string value)
    {
        if (index + 1 >= args.Length)
        {
            Fail(result, option + " needs a value.");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static CommandLine Fail(CommandLine result, string message)
    {
        result.Error = message;
        return result;
    }

    private static bool IsOneOf(string value, params string[] allowed) =>
        allowed.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Comma or semicolon separated, so a shell that mangles one still works.</summary>
    private static string[] Split(string value) =>
        value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(part => part.Trim())
             .Where(part => part.Length > 0)
             .ToArray();

    public const string Usage = @"tekla-dump — serialize Tekla Structures model objects to JSON / NDJSON.

USAGE
  tekla-dump inspect [--selected | --guid <g>[,<g>] | --type <Beam,Bolt>] [-o out.json]
  tekla-dump bulk    [--all | --type <...> | --phase <n> | --filter <name>] -o out.ndjson
  tekla-dump attrs   --list [--for PART|ASSEMBLY|BOLT|WELD|REBAR|CONNECTION]
  tekla-dump doctor

COMMANDS
  inspect   A curated view of a handful of objects: header + one record each. Meant to be
            pasted into a chat, so session details are off by default.
  bulk      Streaming NDJSON for arbitrarily many objects. Header line, then one line per object.
  attrs     What the running environment's template attribute catalog offers.
  doctor    Session, version, assembly resolution, numbering and work plane — the output an
            issue report should start with.

SELECTION (inspect / bulk)
  --selected              What is selected in the Tekla UI right now.
  --guid <g>[,<g>...]     Specific objects by GUID.
  --type <Name>[,<Name>]  By CLR type name: Beam, ContourPlate, BoltArray, Weld, ...
  --all                   Every object in the model (bulk only).
  --phase <n>             Only objects in that phase.
  --filter <name>         A saved Tekla selection filter, by name.

OPTIONS
  -o, --output <path>          Write here instead of stdout.
  --attrs <NAME,NAME>          Read exactly these template attributes. Wins over --attrs-scope.
  --attrs-scope <s>            none (default) | associated | full.
  --component-attrs <file>     Read component attribute names from a file, one per line.
  --geometry <g>               none | points (default) | solids. Solids are very expensive.
  --units <u>                  normalized (default) | native.
  --no-derived                 Emit the create block only.
  --session-details            Include modelPath and the Tekla user in the header.
  --no-session-details         Leave them out (the default for inspect).
  --report-join-threshold <n>  Object count above which attributes come from a report join.
  --max-template-attrs <n>     Ceiling on template attributes per object (default 2000).
  --tekla-bin <dir>            Where to load the Open API from, if the GAC bind fails.
  --pretty                     Indent bulk output records (one per line is still the framing).
  --quiet                      No progress or notices on stderr.
  --progress                   Report progress on stderr during a bulk run.
  -h, --help                   This text.
  --version                    This exe, the TeklaDump.dll beside it, and the schema.

EXIT CODES
  0  ok
  1  unexpected error
  2  no running Tekla Structures session
  3  bad arguments
  4  completed, but some objects were skipped (see the warnings)

The JSON is NOT a round-trip format. It does not recreate a model: components generate their own
output, many properties are derived, and welds and rebar bind to their parents by identifier. The
create block is a starting point for code you write.";
}
