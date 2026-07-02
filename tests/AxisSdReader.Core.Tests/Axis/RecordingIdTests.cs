using AxisSdReader.Core.Axis;

namespace AxisSdReader.Core.Tests.Axis;

public class RecordingIdTests
{
    [Fact]
    public void ParsesDocumentedAxisFormat()
    {
        var id = RecordingId.TryParse("20110812_081211_016F_00408C1834FD");

        Assert.NotNull(id);
        Assert.Equal(new DateTime(2011, 8, 12, 8, 12, 11), id!.StartTime);
        Assert.Equal("016F", id.Suffix);
        Assert.Equal("00408C1834FD", id.CameraSerial);
    }

    [Theory]
    [InlineData("lost+found")]
    [InlineData("System Volume Information")]
    [InlineData("20250230_081211_016F_00408C1834FD")] // Feb 30 does not exist
    [InlineData("2025011_093000_1A2B_ACCC8E123456")] // date too short
    [InlineData("notarecording")]
    public void RejectsNonRecordingNames(string name)
    {
        Assert.Null(RecordingId.TryParse(name));
    }

    [Fact]
    public void NormalizesHexCasing()
    {
        var id = RecordingId.TryParse("20250114_093000_1a2b_accc8e123456");

        Assert.NotNull(id);
        Assert.Equal("1A2B", id!.Suffix);
        Assert.Equal("ACCC8E123456", id.CameraSerial);
    }
}
