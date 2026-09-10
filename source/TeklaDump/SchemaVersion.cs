namespace TeklaDump;

/// <summary>
/// The schema version stamped into every document.
/// </summary>
/// <remarks>
/// Deliberately decoupled from the package version: the package follows GitVersion and moves on
/// every fix, while this is hand-bumped and only its MAJOR moves on a breaking field change.
/// A consumer pins behaviour to this, not to the NuGet version.
/// </remarks>
public static class SchemaVersion
{
    public const string Current = "1.0";

    /// <summary>
    /// Reserves room for a drawing dump without inviting one. A drawing dump would be a separate
    /// file with <c>"domain":"drawing"</c>, never records mixed into a model file — so adding it
    /// later breaks nothing in v1.
    /// </summary>
    public const string ModelDomain = "model";
}
