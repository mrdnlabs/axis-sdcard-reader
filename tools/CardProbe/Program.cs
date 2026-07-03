using System.Diagnostics;
using AxisSdReader.Core;
using AxisSdReader.Core.Disk;
using AxisSdReader.Core.Ext4;

// CardProbe: diagnostic harness for validating raw ext4 SD card access.
//
//   CardProbe                    list physical disks
//   CardProbe --disk N           guarded probe of physical disk N (run elevated)
//   CardProbe --disk N --no-guard   ... without locking volumes (diagnostics only)
//   CardProbe --image path.img   probe a card image file
//
// Extra operations (combinable, after --disk/--image):
//   --tree [maxDepth]            recursive directory listing
//   --dump <cardPath> <localPath>  copy a file off the card (repeatable)

return args switch
{
    [] => ListDisks(),
    ["--disk", var n, .. var rest] when int.TryParse(n, out var disk) => ProbeDisk(disk, rest),
    ["--image", var path, .. var rest] => ProbeImage(path, rest),
    ["--mkv", var path] => ProbeMkv(path),
    _ => Usage(),
};

static int ProbeMkv(string path)
{
    using var stream = File.OpenRead(path);
    var meta = AxisSdReader.Core.Axis.Matroska.MkvMetadataReader.Read(stream);
    if (meta is null)
    {
        Console.WriteLine("Not a Matroska file.");
        return 1;
    }

    Console.WriteLine($"DateUtc:    {meta.DateUtc:O}");
    Console.WriteLine($"Duration:   {meta.Duration}");
    Console.WriteLine($"Codec:      {meta.VideoCodecId}");
    Console.WriteLine($"Resolution: {meta.PixelWidth}x{meta.PixelHeight}");
    Console.WriteLine($"WritingApp: {meta.WritingApp}");
    Console.WriteLine($"Truncated:  {meta.IsTruncated}");
    return 0;
}

static int Usage()
{
    Console.WriteLine("usage: CardProbe [--disk N [--no-guard] | --image path.img] [--tree [depth]] [--dump src dst]...");
    return 2;
}

static int ListDisks()
{
    Console.WriteLine("Physical disks:");
    foreach (var disk in DiskEnumerator.GetPhysicalDisks())
    {
        var size = disk.SizeBytes is { } s ? $"{s / (1024.0 * 1024 * 1024):F1} GiB" : "size unknown";
        Console.WriteLine($"  #{disk.DiskNumber}  {disk.FriendlyName}  [{disk.BusType}{(disk.IsRemovableMedia ? ", removable" : "")}]  {size}");
        foreach (var vol in disk.Volumes)
        {
            var mounts = vol.MountPoints.Count > 0 ? string.Join(", ", vol.MountPoints) : "(no drive letter)";
            Console.WriteLine($"        volume {mounts}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Probe a card with: CardProbe --disk N   (elevated)");
    return 0;
}

static int ProbeDisk(int diskNumber, string[] options)
{
    var guard = !options.Contains("--no-guard");
    Console.WriteLine($"Opening physical disk {diskNumber} (guard={guard})...");
    var sw = Stopwatch.StartNew();

    try
    {
        using var session = SdCardSession.Open(diskNumber, guard);
        foreach (var line in session.ProtectionLog)
        {
            Console.WriteLine($"  [protect] {line}");
        }

        var result = Report(session.Card, sw);
        return result != 0 ? result : RunExtraOps(session.Card, options);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.Message}");
        return 1;
    }
}

static int ProbeImage(string path, string[] options)
{
    Console.WriteLine($"Opening image {path}...");
    var sw = Stopwatch.StartNew();
    using var card = CardReader.OpenImage(path);
    var result = Report(card, sw);
    return result != 0 ? result : RunExtraOps(card, options);
}

static int RunExtraOps(CardReader card, string[] options)
{
    var fs = card.FileSystem!;

    for (var i = 0; i < options.Length; i++)
    {
        switch (options[i])
        {
            case "--tree":
                var depth = i + 1 < options.Length && int.TryParse(options[i + 1], out var d) ? d : 5;
                Console.WriteLine();
                Console.WriteLine($"Tree (max depth {depth}):");
                PrintTree(fs, @"\", depth, "");
                break;

            case "--dump" when i + 2 < options.Length:
                var src = options[i + 1];
                var dst = options[i + 2];
                i += 2;
                using (var source = fs.OpenFile(src, FileMode.Open, FileAccess.Read))
                using (var target = File.Create(dst))
                {
                    source.CopyTo(target);
                    Console.WriteLine($"Dumped {src} -> {dst} ({target.Length:N0} bytes)");
                }

                break;
        }
    }

    return 0;
}

static void PrintTree(DiscUtils.DiscFileSystem fs, string path, int depthLeft, string indent)
{
    const int maxEntries = 25;

    var files = fs.GetFiles(path).OrderBy(f => f).ToArray();
    foreach (var file in files.Take(maxEntries))
    {
        Console.WriteLine($"{indent}{Path.GetFileName(file)}  {fs.GetFileLength(file):N0}");
    }

    if (files.Length > maxEntries)
    {
        Console.WriteLine($"{indent}... and {files.Length - maxEntries} more files");
    }

    if (depthLeft <= 0)
    {
        return;
    }

    foreach (var dir in fs.GetDirectories(path).OrderBy(d => d))
    {
        var name = Path.GetFileName(dir.TrimEnd('\\'));
        if (name == "lost+found")
        {
            continue;
        }

        Console.WriteLine($"{indent}{name}/");
        PrintTree(fs, dir, depthLeft - 1, indent + "    ");
    }
}

static int Report(CardReader card, Stopwatch sw)
{
    Console.WriteLine($"Status: {card.Status}  (opened in {sw.ElapsedMilliseconds} ms)");
    if (card.Status != CardOpenStatus.Ok)
    {
        Console.WriteLine($"  {card.FailureDetail}");
        return 1;
    }

    var vol = card.Volume!;
    Console.WriteLine($"ext4 filesystem at partition {vol.PartitionIndex}, offset {vol.FirstByte:N0}, length {vol.LengthBytes:N0}");

    var fs = card.FileSystem!;
    Console.WriteLine($"Volume label: '{fs.VolumeLabel}'");
    Console.WriteLine();
    Console.WriteLine("Root contents:");

    foreach (var dir in fs.GetDirectories(@"\").OrderBy(d => d))
    {
        var name = dir.TrimStart('\\');
        var files = fs.GetFiles(dir).ToArray();
        Console.WriteLine($"  {name}/   ({files.Length} files)");
        foreach (var file in files.Take(5))
        {
            Console.WriteLine($"      {Path.GetFileName(file)}  {fs.GetFileLength(file):N0} bytes");
        }

        if (files.Length > 5)
        {
            Console.WriteLine($"      ... and {files.Length - 5} more");
        }
    }

    foreach (var file in fs.GetFiles(@"\").OrderBy(f => f))
    {
        Console.WriteLine($"  {file.TrimStart('\\')}  {fs.GetFileLength(file):N0} bytes");
    }

    // Read the first bytes of the first MKV found, as a data-path smoke test.
    var firstMkv = fs.GetDirectories(@"\")
        .SelectMany(d => fs.GetFiles(d))
        .FirstOrDefault(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));

    if (firstMkv is not null)
    {
        sw.Restart();
        using var stream = fs.OpenFile(firstMkv, FileMode.Open, FileAccess.Read);
        var head = new byte[4];
        stream.ReadExactly(head);
        var ebml = head is [0x1A, 0x45, 0xDF, 0xA3];
        Console.WriteLine();
        Console.WriteLine($"Read {firstMkv}: first bytes {Convert.ToHexString(head)} " +
                          $"({(ebml ? "valid EBML/Matroska" : "NOT Matroska!")}, {sw.ElapsedMilliseconds} ms)");
    }

    return 0;
}
