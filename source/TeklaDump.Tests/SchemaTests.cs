using System.IO;
using System.Text;
using TeklaDump.Extractors;
using TeklaDump.SchemaGen;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// The committed schema must equal what the generator produces right now.
/// </summary>
/// <remarks>
/// This is what makes <c>schema/v1/</c> a description of the code rather than of someone's memory
/// of it. When this fails, the fix is to run <c>scripts/Generate-Schema.ps1</c> and commit the
/// result — together with a CHANGELOG entry under Schema, because a schema diff is a public
/// interface change.
/// </remarks>
public class SchemaTests
{
    [Theory]
    [InlineData("inspect.schema.json")]
    [InlineData("bulk.schema.json")]
    public void The_committed_schema_is_current(string fileName)
    {
        var committed = Path.Combine(Fixtures.Root, "schema", "v1", fileName);
        Assert.True(File.Exists(committed), committed + " is missing. Run scripts/Generate-Schema.ps1.");

        var extractors = ExtractorRegistry.DefaultExtractors();
        var regenerated = fileName == "inspect.schema.json"
            ? SchemaGenerator.BuildInspectSchema(extractors)
            : SchemaGenerator.BuildBulkSchema(extractors);

        var expected = regenerated.ToIndentedString().Replace("\r\n", "\n") + "\n";
        var actual = ReadUtf8(committed).Replace("\r\n", "\n");

        Assert.True(
            expected == actual,
            fileName + " is stale. An extractor's declared keys changed without the schema being " +
            "regenerated — run scripts/Generate-Schema.ps1 and add a CHANGELOG entry under Schema.");
    }

    [Fact]
    public void The_schema_names_every_shipped_extractor()
    {
        var text = ReadUtf8(Path.Combine(Fixtures.Root, "schema", "v1", "inspect.schema.json"));

        foreach (var extractor in ExtractorRegistry.DefaultExtractors())
        {
            Assert.Contains("\"" + extractor.ObjectType.Name + "\"", text);
        }
    }

    private static string ReadUtf8(string path) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(File.ReadAllBytes(path));
}
