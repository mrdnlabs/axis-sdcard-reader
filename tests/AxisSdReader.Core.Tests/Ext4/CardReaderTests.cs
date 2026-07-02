using System.Text;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Tests.Fixtures;

namespace AxisSdReader.Core.Tests.Ext4;

public class CardReaderTests : IClassFixture<CardImageFixture>
{
    private const long BigFileSize = 5L * 1024 * 1024 * 1024;

    private readonly CardImageFixture _fixture;

    public CardReaderTests(CardImageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void OpensExt4PartitionOnMbrDisk()
    {
        using var card = CardReader.OpenImage(_fixture.ImagePath);

        Assert.Equal(CardOpenStatus.Ok, card.Status);
        Assert.NotNull(card.FileSystem);
        Assert.NotNull(card.Volume);
        Assert.Equal(0, card.Volume!.PartitionIndex);
        Assert.Equal(1024 * 1024, card.Volume.FirstByte);
    }

    [Fact]
    public void ListsRecordingDirectoriesAtRoot()
    {
        using var card = CardReader.OpenImage(_fixture.ImagePath);
        var fs = card.FileSystem!;

        var dirs = fs.GetDirectories(@"\")
            .Select(d => d.TrimStart('\\'))
            .Where(d => d != "lost+found")
            .OrderBy(d => d)
            .ToArray();

        Assert.Equal(
            [
                "20250114_093000_1A2B_ACCC8E123456",
                "20250114_101500_77F0_ACCC8E123456",
                "20250302_180000_0C3D_B8A44F998877",
            ],
            dirs);

        var rootFiles = fs.GetFiles(@"\").Select(f => f.TrimStart('\\')).OrderBy(f => f).ToArray();
        Assert.Equal(["bigfile.bin", "index.db"], rootFiles);
    }

    [Fact]
    public void ReadsChunkFileContent()
    {
        using var card = CardReader.OpenImage(_fixture.ImagePath);
        var fs = card.FileSystem!;

        var path = @"\20250114_093000_1A2B_ACCC8E123456\0.mkv";
        Assert.True(fs.FileExists(path));
        Assert.True(fs.GetFileLength(path) > 1024);

        using var stream = fs.OpenFile(path, FileMode.Open, FileAccess.Read);
        var header = new byte[4];
        stream.ReadExactly(header);
        Assert.Equal(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, header); // EBML magic
    }

    [Fact]
    public void ReadsLargeSparseFileSizeAndTail()
    {
        using var card = CardReader.OpenImage(_fixture.ImagePath);
        var fs = card.FileSystem!;

        // File size beyond 4 GiB proves 64-bit size handling (i_size_high).
        Assert.Equal(BigFileSize, fs.GetFileLength(@"\bigfile.bin"));

        using var stream = fs.OpenFile(@"\bigfile.bin", FileMode.Open, FileAccess.Read);

        stream.Position = BigFileSize - 16;
        var tail = new byte[16];
        stream.ReadExactly(tail);
        Assert.Equal("AXIS-TAIL-MARKER", Encoding.ASCII.GetString(tail));

        // A hole in the middle of the sparse file must read as zeros.
        stream.Position = BigFileSize / 2;
        var middle = new byte[4096];
        stream.ReadExactly(middle);
        Assert.All(middle, b => Assert.Equal(0, b));
    }

    [Fact]
    public void BlankImageReportsNoFileSystem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blank-{Guid.NewGuid():N}.img");
        try
        {
            using (var f = File.Create(path))
            {
                f.SetLength(4 * 1024 * 1024);
            }

            using var card = CardReader.OpenImage(path);
            Assert.Equal(CardOpenStatus.NoExt4FileSystem, card.Status);
            Assert.Null(card.FileSystem);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
