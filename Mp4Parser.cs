using System.Buffers.Binary;
using System.Text;

namespace Mls;

public static class Mp4Parser
{
    // NEW: Added "mvex" to the container list so we can step inside it!
    private static readonly string[] ContainerBoxes = ["moov", "trak", "mdia", "minf", "stbl", "stsd", "sinf", "mvex"];

    public static void Parse(string filePath, bool isVerbose, bool showDuration)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        if (fs.Length < 8) return;

        byte[] peekBuffer = new byte[8];
        fs.ReadExactly(peekBuffer, 0, 8);

        // MKV Check
        if (peekBuffer[0] == 0x1A && peekBuffer[1] == 0x45 && peekBuffer[2] == 0xDF && peekBuffer[3] == 0xA3)
        {
            Console.WriteLine($"\n--- {Path.GetFileName(filePath)} ---");
            Console.WriteLine($"  {"Format:",-21} Matroska / WebM (MKV)");
            return;
        }

        // M2TS Check (Sync byte 0x47)
        if (peekBuffer[0] == 0x47 || (peekBuffer[0] == 0x00 && peekBuffer[4] == 0x47))
        {
            Console.WriteLine($"\n--- {Path.GetFileName(filePath)} ---");
            Console.WriteLine($"  {"Format:",-21} Blu-ray Video (M2TS)");
            return;
        }

        string firstBoxType = Encoding.ASCII.GetString(peekBuffer, 4, 4);
        string[] validStarts = ["ftyp", "moov", "mdat", "free", "skip", "wide"];
        if (!validStarts.Contains(firstBoxType)) return;

        var context = new ParseContext { TotalFileSize = fs.Length };
        Console.WriteLine($"\n--- {Path.GetFileName(filePath)} ---");

        if (firstBoxType == "ftyp")
        {
            byte[] brandBuf = new byte[4];
            fs.Seek(8, SeekOrigin.Begin);
            fs.ReadExactly(brandBuf, 0, 4);
            context.MajorBrand = Encoding.ASCII.GetString(brandBuf);
            context.BrandExplanation = context.MajorBrand == "iso6" ? " (MP4 Base Media version 6)" : "";
        }

        (long Offset, long Size) = FindMoovBox(fs);
        if (Offset != -1 && Size <= int.MaxValue)
        {
            fs.Seek(Offset, SeekOrigin.Begin);

            byte[] moovBuffer = new byte[(int)Size];
            fs.ReadExactly(moovBuffer, 0, (int)Size);

            using var memoryStream = new MemoryStream(moovBuffer);
            ParseBoxes(memoryStream, Size, 0, isVerbose, context);

            PrintConsolidatedSummary(context, showDuration);
        }
    }

    private static (long Offset, long Size) FindMoovBox(FileStream fs)
    {
        fs.Seek(0, SeekOrigin.Begin);
        byte[] header = new byte[8];
        while (fs.Position + 8 <= fs.Length)
        {
            fs.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);

            if (size == 1)
            {
                byte[] ext = new byte[8];
                fs.ReadExactly(ext, 0, 8);
                size = BinaryPrimitives.ReadInt64BigEndian(ext);
                if (type == "moov") return (fs.Position - 16, size);
                fs.Seek(size - 16, SeekOrigin.Current);
            }
            else if (size == 0)
            {
                if (type == "moov") return (fs.Position - 8, fs.Length - fs.Position + 8);
                break;
            }
            else
            {
                if (type == "moov") return (fs.Position - 8, size);
                fs.Seek(size - 8, SeekOrigin.Current);
            }
        }
        return (-1, 0);
    }

    private static void ParseBoxes(Stream fs, long endPosition, int depth, bool isVerbose, ParseContext ctx)
    {
        byte[] header = new byte[8];
        string indent = new(' ', depth * 2);

        while (fs.Position + 8 <= endPosition)
        {
            fs.ReadExactly(header, 0, 8);
            long boxSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string boxType = Encoding.ASCII.GetString(header, 4, 4);
            long dataSize = boxSize - 8;

            if (boxSize == 1)
            {
                byte[] ext = new byte[8];
                fs.ReadExactly(ext, 0, 8);
                boxSize = BinaryPrimitives.ReadInt64BigEndian(ext);
                dataSize = boxSize - 16;
            }
            if (boxType == "moof" || boxType == "mdat")
            {
                fs.Seek(dataSize, SeekOrigin.Current);
                continue;
            }

            if (isVerbose)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {indent}[{boxType}] Size: {boxSize:N0}");
                Console.ResetColor();
            }

            if (boxType == "trak")
            {
                ctx.CurrentTrack = new TrackInfo();
                ctx.Tracks.Add(ctx.CurrentTrack);
            }

            if (boxType == "mvhd") ParseMvhd(fs, dataSize, ctx);
            else if (boxType == "mehd") ParseMehd(fs, dataSize, ctx); // NEW: Route to fragmented duration
            else if (boxType is "encv" or "avc1" or "avc3" or "hev1" or "hvc1" or "dvh1" or "dvhe" or "vp09" or "av01")
            {
                if (ctx.CurrentTrack != null)
                {
                    ctx.CurrentTrack.Codec = NormalizeSampleEntryCodec(boxType);
                    // FIXED: Only trigger DRM tags for actual encrypted payload atoms
                    ctx.CurrentTrack.IsDrm = boxType is "encv";
                    // Prefer VisualSampleEntry coded size over tkhd display size (matches MediaInfo).
                    TryReadVisualSampleEntryResolution(fs, dataSize, ctx.CurrentTrack);
                }
                else
                {
                    fs.Seek(dataSize, SeekOrigin.Current);
                }
            }
            else if (boxType is "enca" or "mp4a" or "ac-3" or "ec-3" or "Opus" or "fLaC")
            {
                if (ctx.CurrentTrack != null)
                {
                    ctx.CurrentTrack.Codec = NormalizeSampleEntryCodec(boxType);
                    ctx.CurrentTrack.IsDrm = boxType is "enca";
                }
                fs.Seek(dataSize, SeekOrigin.Current);
            }
            else if (Array.Exists(ContainerBoxes, c => c == boxType))
            {
                long skip = (boxType == "stsd") ? 8 : 0;
                if (skip > 0) fs.Seek(skip, SeekOrigin.Current);
                ParseBoxes(fs, fs.Position + dataSize - skip, depth + 1, isVerbose, ctx);
            }
            else if (boxType == "tkhd") ParseTrackHeader(fs, (int)dataSize, ctx);
            else if (boxType == "hdlr") ParseHandler(fs, (int)dataSize, ctx);
            else fs.Seek(dataSize, SeekOrigin.Current);
        }
    }

    private static void ParseMvhd(Stream fs, long dataSize, ParseContext ctx)
    {
        byte[] buf = new byte[32];
        int readLen = (int)Math.Min(dataSize, 32);
        fs.ReadExactly(buf, 0, readLen);
        byte v = buf[0];

        // Save the clock speed to our context so the mehd box can use it later!
        ctx.TimeScale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(v == 1 ? 20 : 12, 4));

        // FIX: The Version 1 duration offset is 24, not 28!
        ulong dur = v == 1 ? BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(24, 8)) : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16, 4));

        if (dur > 0 && ctx.TimeScale > 0)
        {
            ctx.DurationSeconds = (double)dur / ctx.TimeScale;
            ctx.BitrateMbps = (ctx.TotalFileSize * 8.0) / ctx.DurationSeconds / 1_000_000;
        }

        fs.Seek(dataSize - readLen, SeekOrigin.Current);
    }

    // NEW: The Fragmented MP4 duration decoder
    private static void ParseMehd(Stream fs, long dataSize, ParseContext ctx)
    {
        byte[] buf = new byte[16];
        int readLen = (int)Math.Min(dataSize, 16);
        fs.ReadExactly(buf, 0, readLen);

        byte v = buf[0];
        ulong dur = v == 1 ? BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(4, 8)) : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));

        // Only overwrite the duration if the mvhd reported 0
        if (dur > 0 && ctx.TimeScale > 0 && ctx.DurationSeconds == 0)
        {
            ctx.DurationSeconds = (double)dur / ctx.TimeScale;
            ctx.BitrateMbps = (ctx.TotalFileSize * 8.0) / ctx.DurationSeconds / 1_000_000;
        }

        fs.Seek(dataSize - readLen, SeekOrigin.Current);
    }

    private static void ParseTrackHeader(Stream fs, int dataSize, ParseContext ctx)
    {
        byte[] b = new byte[dataSize];
        fs.ReadExactly(b, 0, dataSize);
        int off = 4 + (b[0] == 1 ? 32 : 20) + 16 + 36;
        if (off + 8 <= dataSize && ctx.CurrentTrack != null)
        {
            uint w = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off, 4));
            uint h = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 4, 4));

            // tkhd stores 16.16 fixed-point display size; round to nearest pixel.
            int width = (int)((w + 0x8000) >> 16);
            int height = (int)((h + 0x8000) >> 16);
            if (width > 0 && height > 0)
            {
                // Sample-entry coded size (set later) is preferred when available.
                ctx.CurrentTrack.Resolution = $"{width}x{height}";
            }
        }
    }

    /// <summary>
    /// Maps ISO BMFF sample-entry fourccs to the same human-readable labels used by MKV/M2TS.
    /// Encrypted entries (encv/enca) keep their fourcc; the DRM flag carries the protection state.
    /// </summary>
    private static string NormalizeSampleEntryCodec(string boxType) => boxType switch
    {
        "avc1" or "avc3" => "H.264/AVC",
        "hev1" or "hvc1" => "H.265/HEVC",
        "dvh1" or "dvhe" => "Dolby Vision",
        "vp09" => "VP9",
        "av01" => "AV1",
        "mp4a" => "AAC",
        "ac-3" => "AC3",
        "ec-3" => "E-AC3",
        "Opus" => "Opus",
        "fLaC" => "FLAC",
        _ => boxType
    };

    /// <summary>
    /// Reads width/height from a VisualSampleEntry payload (avc1/hev1/encv/…).
    /// Layout: SampleEntry(8) + pre_defined/reserved(16) + width(2) + height(2).
    /// </summary>
    private static void TryReadVisualSampleEntryResolution(Stream fs, long dataSize, TrackInfo track)
    {
        if (dataSize < 28)
        {
            fs.Seek(dataSize, SeekOrigin.Current);
            return;
        }

        byte[] buf = new byte[28];
        fs.ReadExactly(buf, 0, 28);
        ushort width = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(24, 2));
        ushort height = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(26, 2));
        if (width > 0 && height > 0)
        {
            track.Resolution = $"{width}x{height}";
        }

        fs.Seek(dataSize - 28, SeekOrigin.Current);
    }

    private static void ParseHandler(Stream fs, int dataSize, ParseContext ctx)
    {
        byte[] b = new byte[dataSize];
        fs.ReadExactly(b, 0, dataSize);
        if (dataSize >= 12 && ctx.CurrentTrack != null && string.IsNullOrEmpty(ctx.CurrentTrack.HandlerCode))
        {
            string h = Encoding.ASCII.GetString(b, 8, 4);
            ctx.CurrentTrack.HandlerCode = h;
            ctx.CurrentTrack.Type = h == "vide" ? "Video Track" :
                                    h == "soun" ? "Audio Track" :
                                    (h == "text" || h == "subt" || h == "sbtl") ? "Subtitle Track" :
                                    $"Track ({h})";
        }
    }

    private static void PrintConsolidatedSummary(ParseContext ctx, bool showDuration)
    {
        Console.WriteLine($"  {"Format:",-21} {ctx.MajorBrand}{ctx.BrandExplanation}");

        if (ctx.DurationSeconds > 0)
        {
            if (showDuration)
            {
                TimeSpan t = TimeSpan.FromSeconds(ctx.DurationSeconds);
                Console.WriteLine($"  {"Duration:",-21} {(int)t.TotalMinutes} min {t.Seconds} sec");
            }

            // Always print bitrate when duration is known.
            Console.WriteLine($"  {"Bitrate:",-21} {ctx.BitrateMbps:F2} Mbps");
        }

        foreach (var track in ctx.Tracks)
        {
            Console.ForegroundColor = track.HandlerCode == "vide" ? ConsoleColor.Cyan : ConsoleColor.Green;

            bool hasData = !string.IsNullOrEmpty(track.Codec) || !string.IsNullOrEmpty(track.Resolution);
            string typeLabel = track.Type + (hasData ? ":" : "");

            string resPart = (track.HandlerCode == "vide" && !string.IsNullOrEmpty(track.Resolution)) ? $" [{track.Resolution}]" : "";
            string codecPart = track.Codec;
            string drm = track.IsDrm ? " [DRM-protected]" : "";

            Console.WriteLine($"    -> {typeLabel,-16} {codecPart}{resPart}{drm}");
            Console.ResetColor();
        }
    }

    public static void Evict(string file)
    {
        const int FILE_FLAG_NO_BUFFERING = 0x20000000;
        using var evictFs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, (FileOptions)FILE_FLAG_NO_BUFFERING);
    }
}
