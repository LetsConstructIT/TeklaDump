<#
.SYNOPSIS
    Deterministic check for whether Tekla Open API source code modifies the model
    (or its drawings / files) versus being strictly read-only.

.DESCRIPTION
    Scans C# (.cs) source for the Tekla Open API calls that PERSIST changes, and
    reports each one with file:line evidence plus a single verdict:

        READ-ONLY          - no model-mutating API calls found
        HAS SIDE EFFECTS   - one or more mutating calls found

    This is a static, regex-based check. It strips block comments, line comments,
    string literals and char literals before matching, so calls that only appear
    in comments or strings are NOT flagged. It cannot resolve types, so it relies
    on the distinctiveness of the Tekla method names (Insert/Modify/Delete are
    matched only in their parameterless Tekla form to avoid colliding with
    List<T>.Insert and similar).

    CORE ruleset (always on) - the database-committing surface:
        .Insert()             create object in the model database
        .Modify()             write a changed object back to the database
        .Delete()             remove an object from the database
        .CommitChanges()      commit the open model transaction
        .SetUserProperty(..)  write a user-defined attribute (UDA)
        .SetUserProperties(..)

    EXTENDED ruleset (-Extended) - broader model / drawing / filesystem writes:
        .SetPhase(..)                              re-phase an object
        .Create*Drawing(..) / .UpdateDrawing(..)   create or regenerate drawings
        .SaveActiveDrawing(..)
        Operation.Combine/Convert/CopyObject/      static model mutators
                  MoveObject/Split*/Create*Plate/
                  CreateContourMarking/Delete*
        Operation.CreateNCFiles*/CreateReport*/    [file write] export to disk
                  CreateMISFile*/CreateIFC*/CreateDGN*

.PARAMETER Path
    One or more files or directories to scan. Directories are searched
    recursively for *.cs files. Defaults to the current directory.
    Anything under \bin\ or \obj\ is skipped.

.PARAMETER Extended
    Also apply the EXTENDED ruleset (drawings, phase changes, Operation static
    mutators and file exports). Off by default to keep the core verdict precise.

.PARAMETER Quiet
    Suppress the per-call evidence; print only the verdict line. Useful in CI
    together with the exit code.

.OUTPUTS
    Exit code 0 - READ-ONLY (no findings)
    Exit code 1 - HAS SIDE EFFECTS (findings present)
    Exit code 2 - usage / no source files found

.EXAMPLE
    pwsh ./Check-SideEffects.ps1 -Path ..\..\MyPlugin\src

.EXAMPLE
    # CI gate: fail the build if a "read-only" tool grows a write call
    pwsh ./Check-SideEffects.ps1 -Path .\src -Quiet
    if ($LASTEXITCODE -eq 1) { throw "Tool is no longer read-only" }

.EXAMPLE
    pwsh ./Check-SideEffects.ps1 -Path .\src -Extended
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string[]] $Path = @('.'),

    [switch] $Extended,

    [switch] $Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Rule table -------------------------------------------------------------
# Tier: 'core' always applies; 'ext' only with -Extended.
$rules = @(
    [pscustomobject]@{ Tier = 'core'; Category = 'DB create'; Regex = '\.Insert\s*\(\s*\)' }
    [pscustomobject]@{ Tier = 'core'; Category = 'DB modify'; Regex = '\.Modify\s*\(\s*\)' }
    [pscustomobject]@{ Tier = 'core'; Category = 'DB delete'; Regex = '\.Delete\s*\(\s*\)' }
    [pscustomobject]@{ Tier = 'core'; Category = 'DB commit'; Regex = '\.CommitChanges\s*\(\s*\)' }
    [pscustomobject]@{ Tier = 'core'; Category = 'UDA write'; Regex = '\.SetUserPropert(?:y|ies)\s*\(' }

    [pscustomobject]@{ Tier = 'ext';  Category = 'Phase write';   Regex = '\.SetPhase\s*\(' }
    [pscustomobject]@{ Tier = 'ext';  Category = 'Drawing write'; Regex = '\.Create(?:GA|Multi|Assembly|CastUnit|SinglePart)Drawing\s*\(' }
    [pscustomobject]@{ Tier = 'ext';  Category = 'Drawing write'; Regex = '\.(?:Update|SaveActive)Drawing\s*\(' }
    [pscustomobject]@{ Tier = 'ext';  Category = 'Model op';      Regex = '\bOperation\.(?:Combine|Convert|ConvertPartToItem|CopyObject(?:Mirror)?|MoveObject(?:Mirror)?|Split\w*|Create(?:Bent|Conical)\w*Plate\w*|CreateContourMarking|CreatePopMarks|CreateShapeFromGeometry|Delete\w*)\b' }
    [pscustomobject]@{ Tier = 'ext';  Category = 'File write';    Regex = '\bOperation\.(?:CreateNCFiles\w*|CreateMISFile\w*|CreateReport\w*|CreateIFC\w*|CreateDGN\w*)\b' }
)
$activeRules = $rules | Where-Object { $_.Tier -eq 'core' -or $Extended }

# --- Collect target files ---------------------------------------------------
$files = [System.Collections.Generic.List[string]]::new()
foreach ($p in $Path) {
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Warning "Path not found: $p"
        continue
    }
    $item = Get-Item -LiteralPath $p
    if ($item.PSIsContainer) {
        Get-ChildItem -LiteralPath $p -Recurse -File -Filter *.cs |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { $files.Add($_.FullName) }
    }
    elseif ($item.Extension -eq '.cs') {
        $files.Add($item.FullName)
    }
}
$files = @($files | Sort-Object -Unique)

if ($files.Count -eq 0) {
    Write-Host "No .cs files found under: $($Path -join ', ')" -ForegroundColor Yellow
    exit 2
}

# --- Comment / string blanking (single tokenizing pass) ---------------------
# Replace comments and string/char literals with same-length blanks, keeping
# every newline so line numbers are preserved. One left-to-right alternation
# over the whole file means each construct is consumed by the FIRST branch that
# matches at a given position, which makes the cases that broke a two-pass
# approach behave correctly:
#   * a '/*' or '//' inside a string is eaten by the string branch first, so it
#     can never blank out real code that follows (no false negatives);
#   * a multi-line verbatim string @"...." is blanked as a single token, so a
#     Tekla call quoted inside it is not flagged (no false positives).
# Branch order is significant: verbatim string, regular string, char literal,
# line comment, block comment.
$tokenizer = [regex]::new(
    '@"(?:[^"]|"")*"' +     # verbatim string (may span lines)
    '|"(?:\\.|[^"\\])*"' +  # regular string
    "|'(?:\\.|[^'\\])*'" +  # char literal
    '|//[^\r\n]*' +         # line comment
    '|/\*.*?\*/',           # block comment (may span lines)
    [System.Text.RegularExpressions.RegexOptions]::Singleline)

$blankToken = { param($m) [regex]::Replace($m.Value, '[^\r\n]', ' ') }

# --- Scan -------------------------------------------------------------------
$findings = [System.Collections.Generic.List[object]]::new()

foreach ($file in $files) {
    $raw = Get-Content -LiteralPath $file -Raw
    if ([string]::IsNullOrEmpty($raw)) { continue }

    $clean = $tokenizer.Replace($raw, $blankToken)

    $origLines = $raw   -split "`n"
    $scanLines = $clean -split "`n"

    for ($i = 0; $i -lt $scanLines.Count; $i++) {
        foreach ($rule in $activeRules) {
            # -cmatch: C# identifiers are case-sensitive, so match them that way.
            if ($scanLines[$i] -cmatch $rule.Regex) {
                $findings.Add([pscustomobject]@{
                    File     = $file
                    Line     = $i + 1
                    Category = $rule.Category
                    Text     = $origLines[$i].Trim()
                })
            }
        }
    }
}

# --- Report -----------------------------------------------------------------
$scope = if ($Extended) { 'core + extended' } else { 'core' }
if (-not $Quiet) {
    Write-Host ""
    Write-Host "Tekla Open API - side-effect check ($scope ruleset)" -ForegroundColor Cyan
    Write-Host "Scanned $($files.Count) .cs file(s)."
    Write-Host ""
}

if ($findings.Count -eq 0) {
    Write-Host "VERDICT: READ-ONLY  (no model-mutating API calls found)" -ForegroundColor Green
    exit 0
}

if (-not $Quiet) {
    $root = (Get-Location).Path
    foreach ($f in $findings | Sort-Object File, Line) {
        $rel = $f.File
        if ($rel.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            $rel = $rel.Substring($root.Length).TrimStart('\', '/')
        }
        $tag = ('[{0}]' -f $f.Category).PadRight(16)
        Write-Host ("  {0,-45} {1} {2}" -f "$($rel):$($f.Line)", $tag, $f.Text) -ForegroundColor Yellow
    }
    Write-Host ""
    $byCat = $findings | Group-Object Category | Sort-Object Name |
        ForEach-Object { "$($_.Name) x$($_.Count)" }
    Write-Host ("Breakdown: {0}" -f ($byCat -join ', '))
    Write-Host ""
}

$fileCount = @($findings | Select-Object -ExpandProperty File -Unique).Count
Write-Host ("VERDICT: HAS SIDE EFFECTS  ({0} call(s) across {1} file(s))" -f $findings.Count, $fileCount) -ForegroundColor Red
exit 1
