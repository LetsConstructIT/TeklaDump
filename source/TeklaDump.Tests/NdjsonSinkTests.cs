using System.IO;
using System.Linq;
using System.Text;
using TeklaDump.Json;
using TeklaDump.Sinks;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// The bulk framing: one JSON value per line, UTF-8 with no BOM, "\n" endings, and no section that
/// turned out empty.
/// </summary>
public class NdjsonSinkTests
{
    [Fact]
    public void Writes_one_compact_line_per_record()
    {
        var text = Write(sink =>
        {
            sink.BeginRecord();
            sink.Write("objectType", JsonValue.String("Beam"));
            sink.EndRecord();

            sink.BeginRecord();
            sink.Write("objectType", JsonValue.String("Bolt"));
            sink.EndRecord();
        });

        Assert.Equal("{\"objectType\":\"Beam\"}\n{\"objectType\":\"Bolt\"}\n", text);
    }

    [Fact]
    public void Writes_no_byte_order_mark()
    {
        var bytes = WriteBytes(sink =>
        {
            sink.BeginRecord();
            sink.Write("a", JsonValue.Number(1));
            sink.EndRecord();
        });

        // A BOM breaks jq, breaks a naive readline(), and is not part of the documented framing.
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void Uses_line_feeds_only()
    {
        var bytes = WriteBytes(sink =>
        {
            sink.BeginRecord();
            sink.Write("a", JsonValue.Number(1));
            sink.EndRecord();
        });

        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[bytes.Length - 1]);
    }

    [Fact]
    public void Omits_a_section_that_received_no_values()
    {
        var text = Write(sink =>
        {
            sink.BeginRecord();
            sink.Write("objectType", JsonValue.String("Beam"));
            sink.BeginSection("create");
            // Every value was null — an extractor writing unconditionally is the normal case.
            sink.Write("Profile", null);
            sink.EndSection();
            sink.BeginSection("derived");
            sink.Write("Phase", JsonValue.Number(1));
            sink.EndSection();
            sink.EndRecord();
        });

        Assert.Equal("{\"objectType\":\"Beam\",\"derived\":{\"Phase\":1}}\n", text);
    }

    [Fact]
    public void Escapes_control_characters_and_quotes()
    {
        var text = Write(sink =>
        {
            sink.BeginRecord();
            sink.Write("comment", JsonValue.String("a \"quoted\" \\ path\nwith\ttabs and \u0001"));
            sink.EndRecord();
        });

        Assert.Equal(
            "{\"comment\":\"a \\\"quoted\\\" \\\\ path\\nwith\\ttabs and \\u0001\"}\n",
            text);
    }

    [Fact]
    public void Closes_a_record_left_open_so_the_last_line_still_parses()
    {
        // The Ctrl+C promise: every completed line is independently parseable, and disposal never
        // leaves a dangling half-record behind.
        var stream = new MemoryStream();
        using (var sink = new NdjsonSink(stream))
        {
            sink.BeginRecord();
            sink.Write("objectType", JsonValue.String("Beam"));
            sink.BeginSection("create");
            sink.Write("Profile", JsonValue.String("HEA300"));
        }

        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("{\"objectType\":\"Beam\",\"create\":{\"Profile\":\"HEA300\"}}\n", text);
    }

    [Fact]
    public void Writes_the_header_as_its_own_line()
    {
        var stream = new MemoryStream();
        using (var sink = new NdjsonSink(stream))
        {
            var header = new JsonObject();
            header.Add("schemaVersion", JsonValue.String("1.0"));
            sink.WriteLine(header);

            sink.BeginRecord();
            sink.Write("objectType", JsonValue.String("Beam"));
            sink.EndRecord();
        }

        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
        Assert.Equal("{\"schemaVersion\":\"1.0\"}", lines[0]);
        Assert.Equal("{\"objectType\":\"Beam\"}", lines[1]);
    }

    private static string Write(System.Action<NdjsonSink> body) =>
        Encoding.UTF8.GetString(WriteBytes(body));

    private static byte[] WriteBytes(System.Action<NdjsonSink> body)
    {
        var stream = new MemoryStream();
        using (var sink = new NdjsonSink(stream)) body(sink);
        return stream.ToArray();
    }
}
