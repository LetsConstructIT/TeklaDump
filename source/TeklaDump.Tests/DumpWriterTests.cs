using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// End-to-end shape of both documents, driven with uninserted objects so no Tekla session is
/// needed.
/// </summary>
public class DumpWriterTests
{
    private static IEnumerable<ModelObject> SampleBeam()
    {
        var beam = new Beam(new Point(0, 0, 0), new Point(6000, 0, 0));
        beam.Profile.ProfileString = "HEA300";
        beam.Material.MaterialString = "S355J2";
        beam.Class = "3";
        beam.Name = "BEAM";
        yield return beam;
    }

    [Fact]
    public void Inspect_produces_a_header_and_one_record_per_object()
    {
        var document = DumpWriter.Inspect(SampleBeam()).ToIndentedString();

        Assert.Contains("\"header\"", document);
        Assert.Contains("\"schemaVersion\": \"" + SchemaVersion.Current + "\"", document);
        Assert.Contains("\"domain\": \"model\"", document);
        Assert.Contains("\"objectType\": \"Beam\"", document);
    }

    [Fact]
    public void The_create_block_leads_with_the_constructor_then_the_defining_geometry()
    {
        var document = DumpWriter.Inspect(SampleBeam()).ToIndentedString();

        var ctor = document.IndexOf("\"$ctor\"", StringComparison.Ordinal);
        var start = document.IndexOf("\"StartPoint\"", StringComparison.Ordinal);
        var profile = document.IndexOf("\"Profile\"", StringComparison.Ordinal);

        // A create block is ordered as you would write the code: constructor, then what the object
        // cannot exist without, then the catalog properties.
        Assert.True(ctor >= 0 && ctor < start, "the constructor hint comes first");
        Assert.True(start < profile, "the defining points come before the optional properties");
        Assert.Contains("new Beam(Beam.BeamTypeEnum.BEAM)", document);
    }

    [Fact]
    public void A_read_only_property_lands_in_derived_not_create()
    {
        var document = DumpWriter.Inspect(SampleBeam()).ToIndentedString();

        // Beam.Type is read-only after construction, so a generated script that assigns it does
        // not compile. It belongs in derived, and the constructor hint carries the same fact.
        var derived = document.IndexOf("\"derived\"", StringComparison.Ordinal);
        var type = document.IndexOf("\"Type\"", StringComparison.Ordinal);
        Assert.True(derived >= 0 && type > derived);
    }

    [Fact]
    public void No_derived_yields_a_create_only_document()
    {
        var options = DumpOptions.Default;
        options.IncludeDerived = false;

        var document = DumpWriter.Inspect(SampleBeam(), options).ToIndentedString();

        Assert.Contains("\"create\"", document);
        Assert.DoesNotContain("\"derived\"", document);
    }

    [Fact]
    public void Session_details_are_off_by_default()
    {
        // An inspect file is meant to be pasted into a chat, and a customer's folder path and a
        // Tekla user name do not belong in one.
        var document = DumpWriter.Inspect(SampleBeam()).ToIndentedString();

        Assert.DoesNotContain("\"modelPath\"", document);
        Assert.DoesNotContain("\"user\"", document);
    }

    [Fact]
    public void Bulk_writes_a_header_line_then_one_line_per_object()
    {
        var stream = new MemoryStream();
        var result = DumpWriter.Bulk(SampleBeam().Concat(SampleBeam()), stream);

        var lines = Encoding.UTF8.GetString(stream.ToArray())
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Contains("\"schemaVersion\"", lines[0]);
        Assert.Contains("\"objectType\":\"Beam\"", lines[1]);
        Assert.Equal(2, result.ObjectsWritten);
        Assert.Equal(0, result.ObjectsSkipped);
        Assert.Equal("T0", result.AttributeTier);
    }

    [Fact]
    public void Bulk_states_the_object_count_when_it_knew_it_before_writing()
    {
        var stream = new MemoryStream();
        DumpWriter.Bulk(SampleBeam(), stream);

        var header = Encoding.UTF8.GetString(stream.ToArray()).Split('\n')[0];
        Assert.Contains("\"objectCount\":1", header);
    }

    [Fact]
    public void Two_runs_over_the_same_objects_are_byte_identical()
    {
        // The golden-file promise. Only the five volatile header fields may differ, and this masks
        // exactly those and nothing else.
        var objects = SampleBeam().ToList();

        var first = Mask(DumpWriter.Inspect(objects).ToIndentedString());
        var second = Mask(DumpWriter.Inspect(objects).ToIndentedString());

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_related_object_is_a_reference_never_a_nested_record()
    {
        var weld = new Weld();
        weld.MainObject = new Beam();

        var document = DumpWriter.Inspect(new ModelObject[] { weld }).ToIndentedString();

        var main = document.IndexOf("\"MainObject\"", StringComparison.Ordinal);
        Assert.True(main > 0);

        // A reference names the object and stops. Following it is what drags a whole model into a
        // dump of one weld, and it is why cycles are a non-issue rather than a cycle detector.
        var block = document.Substring(main, Math.Min(200, document.Length - main));
        Assert.Contains("\"objectType\": \"Beam\"", block);
        Assert.DoesNotContain("\"create\"", block);
    }

    /// <summary>Masks exactly the header fields that legitimately differ between two runs.</summary>
    private static string Mask(string document) =>
        Regex.Replace(
            document,
            "\"(generatedAt|tool|buildNumber|modelPath|user)\": \"[^\"]*\"",
            "\"$1\": \"<masked>\"");

}
