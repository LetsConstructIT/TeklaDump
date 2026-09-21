namespace TeklaDump.Attributes;

/// <summary>
/// Which of Tekla's three report-property buckets an attribute belongs to. Taken from the datatype
/// column of <c>contentattributes*.lst</c>; it decides which <c>ArrayList</c> the name goes into
/// when batching the read.
/// </summary>
internal enum TemplateValueType
{
    Character,
    Float,
    Integer,
}

/// <summary>
/// One template (report) attribute available on a given content type, as declared by Tekla's own
/// <c>contentattributes*.lst</c> files.
/// </summary>
/// <remarks>
/// <see cref="Name"/> is the QUERYABLE name — bracketed virtual nodes and <c>#N</c> version
/// suffixes have already been stripped, because those are display/authoring constructs that never
/// reach the property API. <see cref="Group"/> is the leading dotted segment, which is how Tekla
/// expresses related objects: on a bolt, <c>NUT.WEIGHT</c> and <c>WASHER.MATERIAL</c> reach the nut
/// and washer without the caller having to resolve them.
/// </remarks>
internal sealed class TemplateAttributeDefinition
{
    public TemplateAttributeDefinition(
        string name,
        TemplateValueType valueType,
        string group,
        string? label,
        bool isRelatedObject = false)
    {
        Name = name;
        ValueType = valueType;
        Group = group;
        Label = label;
        IsRelatedObject = isRelatedObject;
    }

    /// <summary>Queryable attribute name, e.g. <c>WEIGHT</c> or <c>NUT.WEIGHT</c>.</summary>
    public string Name { get; }

    public TemplateValueType ValueType { get; }

    /// <summary>Leading dotted segment (<c>NUT</c>, <c>ASSEMBLY</c>, ...), or empty for a direct attribute.</summary>
    public string Group { get; }

    /// <summary>True when the attribute belongs to the object itself rather than a related one.</summary>
    public bool IsDirect => Group.Length == 0;

    /// <summary>
    /// True when <see cref="Group"/> traverses to another full model object rather than to a
    /// constituent of this one — the expensive kind. Decided by the catalog; see
    /// <see cref="TemplateAttributeCatalog"/>.
    /// </summary>
    public bool IsRelatedObject { get; }

    /// <summary>
    /// Attributes read under <see cref="TemplateAttributeScope.Associated"/>: the object's own,
    /// plus constituent groups such as <c>NUT</c>, <c>WASHER</c>, <c>PROFILE</c>, <c>MATERIAL</c>.
    /// </summary>
    public bool IsAssociated => !IsRelatedObject;

    /// <summary>Optional human label, present only for user-defined attributes.</summary>
    public string? Label { get; }
}
