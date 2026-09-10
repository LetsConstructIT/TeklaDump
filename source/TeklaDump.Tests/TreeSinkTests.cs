using TeklaDump.Json;
using TeklaDump.Sinks;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// The inspect-side sink: sections that never opened, and the abort that keeps a half-written
/// record out of the document.
/// </summary>
public class TreeSinkTests
{
    [Fact]
    public void Omits_a_section_that_received_no_values()
    {
        var sink = new TreeSink();
        sink.BeginRecord();
        sink.Write("objectType", JsonValue.String("Beam"));
        sink.BeginSection("create");
        sink.Write("Profile", null);
        sink.EndSection();
        sink.EndRecord();

        Assert.Equal("[{\"objectType\":\"Beam\"}]", sink.Records.ToCompactString());
    }

    [Fact]
    public void Aborting_discards_the_partial_record()
    {
        // What happens when an extractor throws half-way: the record is dropped, not committed
        // with the fields that happened to be read first. A half-written record is worse than a
        // skip, because it looks complete.
        var sink = new TreeSink();

        sink.BeginRecord();
        sink.Write("objectType", JsonValue.String("Beam"));
        sink.BeginSection("create");
        sink.Write("Profile", JsonValue.String("HEA300"));
        sink.AbortRecord();

        sink.BeginRecord();
        sink.Write("objectType", JsonValue.String("Bolt"));
        sink.EndRecord();

        Assert.Equal("[{\"objectType\":\"Bolt\"}]", sink.Records.ToCompactString());
    }

    [Fact]
    public void Keeps_keys_in_the_order_they_were_written()
    {
        // The declared order IS the emitted order, and the emitted order is what a reader turns
        // back into code.
        var sink = new TreeSink();
        sink.BeginRecord();
        sink.BeginSection("create");
        sink.Write("$ctor", JsonValue.String("new Beam()"));
        sink.Write("StartPoint", JsonValue.Number(1));
        sink.Write("Profile", JsonValue.String("HEA300"));
        sink.EndSection();
        sink.EndRecord();

        Assert.Equal(
            "[{\"create\":{\"$ctor\":\"new Beam()\",\"StartPoint\":1,\"Profile\":\"HEA300\"}}]",
            sink.Records.ToCompactString());
    }
}
