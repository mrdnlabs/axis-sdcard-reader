using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;
using AxisSdReader.Core;
using AxisSdReader.Core.Disk;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Ext4.Luks;
using DiscUtils.Ext;
using DiscUtils.Streams;

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
//   --recordings                 list every recording with its trigger metadata (continuous/motion/manual)
//                                and dump the raw recording.xml of each distinct trigger type
//   --pass-prompt                for an encrypted card: prompt (hidden) for the passphrase on stderr
//   --pass <passphrase>          for an encrypted card: passphrase on the command line (avoid; prefer --pass-prompt)

return args switch
{
    [] => ListDisks(),
    ["--disk", var n, .. var rest] when int.TryParse(n, out var disk) => ProbeDisk(disk, rest),
    ["--image", var path, .. var rest] => ProbeImage(path, rest),
    ["--luks", var n] when int.TryParse(n, out var disk) => DumpLuks(RawDiskStream.OpenPhysicalDrive(disk)),
    ["--luks-image", var path] => DumpLuks(File.OpenRead(path)),
    ["--unlock", var n, var pass] when int.TryParse(n, out var disk) => Unlock(RawDiskStream.OpenPhysicalDrive(disk), pass),
    ["--unlock-image", var path, var pass] => Unlock(File.OpenRead(path), pass),
    ["--mkv", var path] => ProbeMkv(path),
    ["--echo-pass", .. var rest] => EchoPass(rest),
    _ => Usage(),
};

// Diagnostic: report the char count + SHA-256 of the passphrase actually received, so command-line
// escaping of special characters (e.g. '!') can be verified end-to-end without revealing the passphrase.
static int EchoPass(string[] options)
{
    var pass = ResolvePassphrase(options);
    if (pass is null)
    {
        Console.WriteLine("no passphrase received");
        return 1;
    }

    var utf8 = System.Text.Encoding.UTF8.GetBytes(pass);
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(utf8));
    Console.WriteLine($"received: chars={pass.Length} utf8bytes={utf8.Length} sha256={sha}");
    return 0;
}

// Unlocks a LUKS-encrypted card with a passphrase and lists the decrypted ext4 root — an end-to-end
// validation of the decryption chain. Read-only.
static int Unlock(Stream deviceStream, string passphrase)
{
    using var raw = deviceStream;
    using var disk = new DiscUtils.Raw.Disk(raw, Ownership.None);

    long offset = 0, length = raw.Length;
    if (disk.IsPartitioned && disk.Partitions is { Count: > 0 } table)
    {
        offset = table[0].FirstSector * disk.SectorSize;
        length = (table[0].LastSector - table[0].FirstSector + 1) * disk.SectorSize;
    }

    var partition = new SubStream(disk.Content, offset, length);
    var sw = Stopwatch.StartNew();
    var result = LuksVolume.TryUnlock(partition, passphrase.ToCharArray());
    Console.WriteLine($"Unlock status: {result.Status}  ({sw.ElapsedMilliseconds} ms)");
    if (result.Detail is not null)
    {
        Console.WriteLine($"  {result.Detail}");
    }

    if (result.Status != LuksUnlockStatus.Success || result.PlaintextStream is null)
    {
        return 1;
    }

    using var plaintext = result.PlaintextStream;
    Console.WriteLine($"Decrypted payload: {plaintext.Length:N0} bytes");

    // The decrypted payload IS the ext4 filesystem.
    using var fs = new ExtFileSystem(plaintext);
    Console.WriteLine($"ext4 volume label: '{fs.VolumeLabel}'");
    Console.WriteLine("Root contents:");
    foreach (var dir in fs.GetDirectories(@"\").OrderBy(d => d))
    {
        Console.WriteLine($"  {dir.TrimStart('\\')}/");
    }

    foreach (var file in fs.GetFiles(@"\").OrderBy(f => f))
    {
        Console.WriteLine($"  {file.TrimStart('\\')}  {fs.GetFileLength(file):N0} bytes");
    }

    return 0;
}

// Reads and describes the LUKS header on the first partition, to characterize an encrypted card
// (version, cipher, mode, key-derivation). Read-only; dumps metadata only, never key material payloads.
static int DumpLuks(Stream deviceStream)
{
    using var raw = deviceStream;
    using var disk = new DiscUtils.Raw.Disk(raw, DiscUtils.Streams.Ownership.None);

    long offset = 0;
    if (disk.IsPartitioned && disk.Partitions is { Count: > 0 } table)
    {
        offset = table[0].FirstSector * disk.SectorSize;
    }

    Console.WriteLine($"First partition at byte offset {offset:N0}");

    var head = ReadAt(raw, offset, 4096);
    var magic = head.AsSpan(0, 6).ToArray();
    Console.WriteLine($"Magic: {Convert.ToHexString(magic)} ('{PrintableAscii(magic)}')");
    if (!magic.AsSpan().SequenceEqual(new byte[] { 0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE }))
    {
        Console.WriteLine("Not a LUKS volume (no LUKS magic at partition start).");
        return 1;
    }

    var version = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6, 2));
    Console.WriteLine($"LUKS version: {version}");

    if (version == 1)
    {
        Console.WriteLine($"  cipher-name : {AsciiZ(head, 8, 32)}");
        Console.WriteLine($"  cipher-mode : {AsciiZ(head, 40, 32)}");
        Console.WriteLine($"  hash-spec   : {AsciiZ(head, 72, 32)}");
        Console.WriteLine($"  payload-off : {BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(104, 4))} sectors");
        Console.WriteLine($"  key-bytes   : {BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(108, 4))}");
        Console.WriteLine($"  mk-iter     : {BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(164, 4))}");
        Console.WriteLine($"  uuid        : {AsciiZ(head, 168, 40)}");
        for (var i = 0; i < 8; i++)
        {
            var b = 208 + i * 48;
            var active = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(b, 4));
            if (active != 0x00AC71F3)
            {
                continue; // LUKS_KEY_ENABLED marker
            }

            var iter = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(b + 4, 4));
            var kmOff = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(b + 40, 4));
            var stripes = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(b + 44, 4));
            Console.WriteLine($"  keyslot {i}  : ENABLED  iterations={iter}  km-offset={kmOff} sectors  stripes={stripes}");
        }
    }
    else if (version == 2)
    {
        var hdrSize = BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8, 8));
        Console.WriteLine($"  hdr-size    : {hdrSize:N0} bytes");
        Console.WriteLine($"  label       : {AsciiZ(head, 24, 48)}");
        Console.WriteLine($"  checksum-alg: {AsciiZ(head, 72, 32)}");
        Console.WriteLine($"  uuid        : {AsciiZ(head, 168, 40)}");
        Console.WriteLine($"  subsystem   : {AsciiZ(head, 208, 48)}");

        var jsonLen = (int)Math.Min(Math.Max(0, (long)hdrSize - 4096), 256 * 1024);
        var json = ReadAt(raw, offset + 4096, jsonLen);
        var text = Encoding.UTF8.GetString(json).TrimEnd('\0');
        Console.WriteLine();
        Console.WriteLine("--- LUKS2 JSON metadata ---");
        Console.WriteLine(text);
    }

    Console.WriteLine();
    Console.WriteLine("First 256 bytes:");
    Console.WriteLine(Convert.ToHexString(head.AsSpan(0, 256)));
    return 0;
}

static byte[] ReadAt(Stream s, long offset, int count)
{
    s.Position = offset;
    var buf = new byte[count];
    var total = 0;
    while (total < count)
    {
        var n = s.Read(buf, total, count - total);
        if (n == 0)
        {
            break;
        }

        total += n;
    }

    return buf;
}

static string AsciiZ(byte[] b, int offset, int len)
{
    var span = b.AsSpan(offset, len);
    var end = span.IndexOf((byte)0);
    return Encoding.ASCII.GetString(span[..(end < 0 ? span.Length : end)]);
}

static string PrintableAscii(byte[] b) =>
    string.Concat(b.Select(c => c is >= 0x20 and < 0x7F ? (char)c : '.'));

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
    Console.WriteLine("       CardProbe --luks N            describe the LUKS encryption header on disk N (elevated)");
    Console.WriteLine("       CardProbe --unlock N <pass>   unlock a LUKS card with a passphrase and list ext4 root (elevated)");
    return 2;
}

// Resolves the LUKS passphrase for an encrypted card: a hidden interactive prompt (--pass-prompt, preferred —
// keeps the secret off the command line) or a plain --pass argument. Null when neither is given.
static char[]? ResolvePassphrase(string[] options)
{
    if (options.Contains("--pass-prompt"))
    {
        Console.Error.Write("SD card passphrase: ");
        return ReadPasswordHidden();
    }

    var idx = Array.IndexOf(options, "--pass");
    return idx >= 0 && idx + 1 < options.Length ? options[idx + 1].ToCharArray() : null;
}

// Reads a line from the console without echoing it (so a passphrase never appears on screen or in a log).
static char[] ReadPasswordHidden()
{
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            break;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Count > 0)
            {
                chars.RemoveAt(chars.Count - 1);
                Console.Error.Write("\b \b"); // erase one masking asterisk
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            chars.Add(key.KeyChar);
            Console.Error.Write('*'); // mask, but show character count as feedback
        }
    }

    return chars.ToArray();
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
        using var session = SdCardSession.Open(diskNumber, guard, ResolvePassphrase(options));
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
    using var card = CardReader.OpenImage(path, ResolvePassphrase(options));
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

            case "--recordings":
                DumpRecordings(fs);
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

// Lists every recording with the trigger metadata that distinguishes continuous / motion / manual, then
// dumps the raw recording.xml of the first recording of each distinct trigger, so we can read the exact
// on-card vocabulary the camera writes. Read-only.
static void DumpRecordings(DiscUtils.DiscFileSystem fs)
{
    var card = AxisSdReader.Core.Axis.AxisCardIndexer.Index(fs);
    Console.WriteLine();
    Console.WriteLine($"=== Recordings ({card.Recordings.Count}) ===");
    Console.WriteLine("  start(local)         src  triggerType     triggerName        chunks  recordingId");

    var firstOfCombo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // "type|name" -> recording dir
    foreach (var r in card.Recordings)
    {
        var type = r.Info?.TriggerType ?? "(none)";
        var name = r.Info?.TriggerName ?? "(none)";
        Console.WriteLine($"  {r.StartTime:yyyy-MM-dd HH:mm:ss}  {r.SourceToken,-3}  {type,-14}  {name,-16}  {r.Chunks.Count,5}  {r.Id}");
        var combo = type + "|" + name;
        if (!firstOfCombo.ContainsKey(combo))
        {
            firstOfCombo[combo] = r.DirectoryPath;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Distinct (triggerType | triggerName) combinations seen: {firstOfCombo.Count}");
    foreach (var combo in firstOfCombo.Keys.OrderBy(k => k))
    {
        Console.WriteLine($"  {combo}");
    }

    foreach (var (combo, dir) in firstOfCombo)
    {
        var xmlPath = dir.TrimEnd('\\') + @"\recording.xml";
        Console.WriteLine();
        Console.WriteLine($"--- raw recording.xml for [{combo}]  ({xmlPath}) ---");
        try
        {
            if (!fs.FileExists(xmlPath))
            {
                Console.WriteLine("(no recording.xml at this path)");
                continue;
            }

            using var s = fs.OpenFile(xmlPath, FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(s);
            var text = reader.ReadToEnd();
            Console.WriteLine(text.Length > 4000 ? text[..4000] + "\n…(truncated)" : text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(failed to read: {ex.Message})");
        }
    }
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
