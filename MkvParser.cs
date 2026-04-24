using System.Text;

namespace Mls;

class MkvParser
{
    static int Go(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: mkvls [-a] <path_to_mkv_file_or_wildcard>");
            Console.WriteLine("  -a, --all        Show all tracks (including subtitles)");
            return 1;
        }

        bool showAll = args.Contains("-a") || args.Contains("--all");
        string targetPattern = args.LastOrDefault(a => !a.StartsWith('-')) ?? "";

        if (string.IsNullOrEmpty(targetPattern))
        {
            Console.WriteLine("No path provided.");
            return 2;
        }

        string[] filesToProcess;
        try
        {
            if (targetPattern.Contains('*') || targetPattern.Contains('?'))
            {
                string? dir = Path.GetDirectoryName(targetPattern);
                string pattern = Path.GetFileName(targetPattern);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                filesToProcess = Directory.GetFiles(dir, pattern);
                if (filesToProcess.Length == 0) return 3;
            }
            else
            {
                if (!File.Exists(targetPattern)) return 4;
                filesToProcess = [targetPattern];
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 5;
        }

        foreach (string file in filesToProcess)
        {
            try
            {
                ProcessFile(file, showAll);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Console.WriteLine($"{file}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }

    public static void ProcessFile(string file, bool showAll)
    {
        Console.WriteLine($"\n--- {Path.GetFileName(file)} ---");

        var context = new ParseContext();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.RandomAccess);
        context.TotalFileSize = fs.Length;

        // 1. Validate EBML Signature
        var (ebmlId, _) = ReadVint(fs, false);
        if (ebmlId != 0x1A45DFA3)
        {
            Console.WriteLine("  Not a valid MKV/EBML file.");
            return;
        }

        var (ebmlSize, _) = ReadVint(fs, true);

        // Parse the EBML header to extract the exact DocType
        long ebmlEnd = fs.Position + (long)ebmlSize;
        while (fs.Position < ebmlEnd)
        {
            var (id, _) = ReadVint(fs, false);
            var (len, _) = ReadVint(fs, true);

            if (id == 0x4282) // DocType Element ID
            {
                context.DocType = ReadString(fs, (int)len);
            }
            else
            {
                fs.Seek((long)len, SeekOrigin.Current);
            }
        }

        // 2. Find the Segment
        var (segmentId, _) = ReadVint(fs, false);
        if (segmentId != 0x18538067) return;
        ReadVint(fs, true); // Skip segment size

        // 3. The Parse Loop
        while (fs.Position < context.TotalFileSize)
        {
            var (elementId, idLen) = ReadVint(fs, false);
            if (idLen == 0) break;

            var (elementSize, _) = ReadVint(fs, true);

            if (elementId == 0x1F43B675) // [Cluster] -> THE BRAKE PEDAL
            {
                break; // Stop parsing to save I/O!
            }
            else if (elementId == 0x1549A966) // [Info]
            {
                SlurpAndParse(fs, (long)elementSize, context, ParseInfoBox);
            }
            else if (elementId == 0x1654AE6B) // [Tracks]
            {
                SlurpAndParse(fs, (long)elementSize, context, ParseTracksBox);
            }
            else
            {
                fs.Seek((long)elementSize, SeekOrigin.Current); // Jump over unknown data
            }
        }

        PrintSummary(context, showAll);
    }

    // --- The "Slurp" Mechanism ---
    private static void SlurpAndParse(Stream fs, long size, ParseContext ctx, Action<Stream, long, ParseContext> parseMethod)
    {
        byte[] buffer = new byte[(int)size];
        fs.ReadExactly(buffer, 0, (int)size);
        using var ms = new MemoryStream(buffer);
        parseMethod(ms, size, ctx);
    }

    private static void ParseInfoBox(Stream ms, long size, ParseContext ctx)
    {
        while (ms.Position < size)
        {
            var (id, _) = ReadVint(ms, false);
            var (len, _) = ReadVint(ms, true);

            if (id == 0x2AD7B1) // TimecodeScale
                ctx.TimecodeScale = ReadUint(ms, (int)len);
            else if (id == 0x4489) // Duration
                ctx.DurationSeconds = ReadFloat(ms, (int)len) * ctx.TimecodeScale / 1_000_000_000.0;
            else
                ms.Seek((long)len, SeekOrigin.Current);
        }
    }

    private static void ParseTracksBox(Stream ms, long size, ParseContext ctx)
    {
        while (ms.Position < size)
        {
            var (id, _) = ReadVint(ms, false);
            var (len, _) = ReadVint(ms, true);

            if (id == 0xAE) // TrackEntry
                ParseTrackEntry(ms, (long)len, ctx);
            else
                ms.Seek((long)len, SeekOrigin.Current);
        }
    }

    private static void ParseTrackEntry(Stream ms, long size, ParseContext ctx)
    {
        long endPos = ms.Position + size;
        var track = new TrackInfo();
        ctx.Tracks.Add(track);

        ulong pWidth = 0, pHeight = 0;
        string fourcc = "";
        ushort audioFormatTag = 0; // NEW: Variable to hold the ACM tag

        while (ms.Position < endPos)
        {
            var (id, _) = ReadVint(ms, false);
            var (len, _) = ReadVint(ms, true);

            if (id == 0x83) // TrackType
            {
                ulong type = ReadUint(ms, (int)len);
                track.Type = type == 1 ? "Video Track" : type == 2 ? "Audio Track" : type == 17 ? "Subtitle Track" : "Track";
            }
            else if (id == 0x86) // CodecID
            {
                track.Codec = ReadString(ms, (int)len).Replace("V_", "").Replace("A_", "").Replace("S_", "");
            }
            else if (id == 0x22B59C) // Language
            {
                track.Language = ReadString(ms, (int)len);
            }
            else if (id == 0x63A2) // CodecPrivate
            {
                if (len >= 40 && track.Codec == "MS/VFW/FOURCC")
                {
                    byte[] priv = new byte[(int)len];
                    ms.ReadExactly(priv, 0, (int)len);
                    // Extract the 4 ASCII characters starting at offset 16
                    fourcc = Encoding.ASCII.GetString(priv, 16, 4);
                }
                else if (len >= 18 && track.Codec == "MS/ACM")
                {
                    byte[] priv = new byte[(int)len];
                    ms.ReadExactly(priv, 0, (int)len);
                    audioFormatTag = BitConverter.ToUInt16(priv, 0);

                    // NEW: If it is the Extensible format, jump to byte 24 and extract the true tag!
                    if (audioFormatTag == 0xFFFE && len >= 40)
                    {
                        audioFormatTag = BitConverter.ToUInt16(priv, 24);
                    }
                }
                else
                {
                    ms.Seek((long)len, SeekOrigin.Current);
                }
            }
            else if (id == 0xE0) // Video
            {
                long vEnd = ms.Position + (long)len;
                while (ms.Position < vEnd)
                {
                    var (vid, _) = ReadVint(ms, false);
                    var (vlen, _) = ReadVint(ms, true);
                    if (vid == 0xB0) pWidth = ReadUint(ms, (int)vlen);
                    else if (vid == 0xBA) pHeight = ReadUint(ms, (int)vlen);
                    else ms.Seek((long)vlen, SeekOrigin.Current);
                }
            }
            else
            {
                ms.Seek((long)len, SeekOrigin.Current);
            }
        }

        // Translate the VFW FOURCC into a clean string
        if (track.Codec == "MS/VFW/FOURCC" && !string.IsNullOrEmpty(fourcc))
        {
            track.Codec = fourcc switch
            {
                "WVC1" or "WMVA" => "VC-1",
                "WMV3" => "VC-1 (WMV3)",
                "DX50" or "DIVX" => "DivX",
                "XVID" => "Xvid",
                "H264" or "avc1" => "H.264/AVC",
                _ => $"VFW [{fourcc}]"
            };
        }

        // Translate the ACM Audio Tag into a clean string
        if (track.Codec == "MS/ACM" && audioFormatTag > 0)
        {
            track.Codec = audioFormatTag switch
            {
                0x0001 => "PCM",
                0x0055 => "MP3",
                0x0161 => "WMA",
                0x0162 => "WMA Pro",
                0x2000 => "AC3",
                0x2001 => "DTS",
                _ => $"ACM [0x{audioFormatTag:X4}]"
            };
        }

        if (pWidth > 0 && pHeight > 0) track.Resolution = $"{pWidth}x{pHeight}";
    }

    // --- Output Formatting ---
    private static void PrintSummary(ParseContext ctx, bool showAll)
    {
        string formatDisplay = ctx.DocType != null && ctx.DocType.Equals("webm", StringComparison.CurrentCultureIgnoreCase) ? "WebM" : "Matroska (MKV)";
        Console.WriteLine($"  {"Format:",-21} {formatDisplay}");

        if (ctx.DurationSeconds > 0)
        {
            ctx.BitrateMbps = (ctx.TotalFileSize * 8.0) / ctx.DurationSeconds / 1_000_000.0;
            TimeSpan t = TimeSpan.FromSeconds(ctx.DurationSeconds);
            Console.WriteLine($"  {"Duration:",-21} {(int)t.TotalMinutes} min {t.Seconds} sec");
            Console.WriteLine($"  {"Bitrate:",-21} {ctx.BitrateMbps:F2} Mbps");
        }
        Console.WriteLine("  Tracks:");
        foreach (var track in ctx.Tracks)
        {
            // Skip rendering subtitle tracks unless the user explicitly asks for them
            if (!showAll && track.Type == "Subtitle Track") continue;

            Console.ForegroundColor = track.Type == "Video Track" ? ConsoleColor.Cyan : track.Type == "Audio Track" ? ConsoleColor.Green : ConsoleColor.Gray;

            string langPart = !string.IsNullOrEmpty(track.Language) && track.Language != "eng" ? $" ({track.Language})" : "";
            string resPart = !string.IsNullOrEmpty(track.Resolution) ? $" [{track.Resolution}]" : "";

            Console.WriteLine($"    -> {track.Type,-16} {track.Codec}{resPart}{langPart}");
            Console.ResetColor();
        }
    }

    // --- EBML Binary Decoders ---
    private static (ulong value, int length) ReadVint(Stream stream, bool isSize)
    {
        int firstByte = stream.ReadByte();
        if (firstByte < 0) return (0, 0);

        int length = 1;
        int mask = 0x80;
        while (length <= 8 && (firstByte & mask) == 0) { mask >>= 1; length++; }

        ulong value = (byte)firstByte;
        if (isSize) value &= (ulong)~mask; // Sizes drop the mask bit, IDs keep it

        // Cast directly to byte to explicitly avoid sign-extension and suppress CS0675
        for (int i = 1; i < length; i++) value = (value << 8) | (byte)stream.ReadByte();

        if (isSize && value == (1UL << (length * 7)) - 1) return (0, length); // Unknown size
        return (value, length);
    }

    private static ulong ReadUint(Stream stream, int length)
    {
        ulong val = 0;
        for (int i = 0; i < length; i++) val = (val << 8) | (byte)stream.ReadByte();
        return val;
    }

    private static double ReadFloat(Stream stream, int length)
    {
        byte[] buf = new byte[length];
        stream.ReadExactly(buf, 0, length);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf); // EBML is Big-Endian
        return length == 4 ? BitConverter.ToSingle(buf, 0) : BitConverter.ToDouble(buf, 0);
    }

    private static string ReadString(Stream stream, int length)
    {
        byte[] buf = new byte[length];
        stream.ReadExactly(buf, 0, length);
        return Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }
}
