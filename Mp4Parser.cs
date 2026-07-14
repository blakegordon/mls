using System.Buffers.Binary;
using System.Text;

namespace Mls;

public static class Mp4Parser
{
    // NEW: Added "mvex" to the container list so we can step inside it!
    private static readonly string[] ContainerBoxes = ["moov", "trak", "mdia", "minf", "stbl", "stsd", "sinf", "mvex"];

    public static void Parse(string filePath, bool isVerbose, bool showDuration, bool showSubtitles)
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

            PrintConsolidatedSummary(context, showDuration, showSubtitles);
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
                    ParseVisualSampleEntry(fs, dataSize, boxType, isVerbose, depth, ctx);
                else
                    fs.Seek(dataSize, SeekOrigin.Current);
            }
            else if (boxType is "enca" or "mp4a" or "ac-3" or "ec-3" or "Opus" or "fLaC")
            {
                if (ctx.CurrentTrack != null)
                    ParseAudioSampleEntry(fs, dataSize, boxType, isVerbose, depth, ctx);
                else
                    fs.Seek(dataSize, SeekOrigin.Current);
            }
            else if (boxType is "tx3g" or "text" or "stpp" or "wvtt" or "c608" or "c708" or "mp4s" or "enct")
            {
                if (ctx.CurrentTrack != null)
                {
                    ctx.CurrentTrack.Codec = NormalizeSampleEntryCodec(boxType);
                    ctx.CurrentTrack.IsDrm = boxType is "enct";
                    if (string.IsNullOrEmpty(ctx.CurrentTrack.Type) || ctx.CurrentTrack.Type == "Unknown")
                        ctx.CurrentTrack.Type = "Subtitle Track";
                }
                fs.Seek(dataSize, SeekOrigin.Current);
            }
            else if (boxType == "frma")
            {
                ParseFrma(fs, dataSize, ctx);
            }
            else if (boxType == "mdhd")
            {
                ParseMdhd(fs, dataSize, ctx);
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

    /// <summary>
    /// VisualSampleEntry fixed header is 78 bytes; children (sinf/frma/avcC/…) follow.
    /// </summary>
    private static void ParseVisualSampleEntry(Stream fs, long dataSize, string boxType, bool isVerbose, int depth, ParseContext ctx)
    {
        long end = fs.Position + dataSize;
        var track = ctx.CurrentTrack!;
        track.Codec = NormalizeSampleEntryCodec(boxType);
        track.IsDrm = boxType is "encv";

        const int VisualFixed = 78;
        if (dataSize < 28)
        {
            fs.Seek(end, SeekOrigin.Begin);
            return;
        }

        byte[] head = new byte[(int)Math.Min(dataSize, VisualFixed)];
        fs.ReadExactly(head, 0, head.Length);
        ushort width = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(24, 2));
        ushort height = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(26, 2));
        if (width > 0 && height > 0)
            track.Resolution = $"{width}x{height}";

        if (head.Length < VisualFixed)
        {
            fs.Seek(end, SeekOrigin.Begin);
            return;
        }

        // Nested boxes: sinf → frma (original fourcc under DRM), avcC, etc.
        if (fs.Position < end)
            ParseBoxes(fs, end, depth + 1, isVerbose, ctx);
        fs.Seek(end, SeekOrigin.Begin);
    }

    /// <summary>
    /// AudioSampleEntry fixed header is 28 bytes (includes channel count + sample rate);
    /// children (sinf/frma/esds/dec3/…) follow. Channel count is cleartext even for enca.
    /// </summary>
    private static void ParseAudioSampleEntry(Stream fs, long dataSize, string boxType, bool isVerbose, int depth, ParseContext ctx)
    {
        long end = fs.Position + dataSize;
        var track = ctx.CurrentTrack!;
        track.Codec = NormalizeSampleEntryCodec(boxType);
        track.IsDrm = boxType is "enca";

        const int AudioFixed = 28;
        if (dataSize < AudioFixed)
        {
            fs.Seek(end, SeekOrigin.Begin);
            return;
        }

        byte[] head = new byte[AudioFixed];
        fs.ReadExactly(head, 0, AudioFixed);
        int channels = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(16, 2));
        if (channels > 0)
        {
            track.Channels = channels;
            track.ChannelLayout = FormatChannelLayout(channels);
        }

        if (fs.Position < end)
            ParseBoxes(fs, end, depth + 1, isVerbose, ctx);
        fs.Seek(end, SeekOrigin.Begin);
    }

    private static void ParseFrma(Stream fs, long dataSize, ParseContext ctx)
    {
        if (dataSize < 4 || ctx.CurrentTrack == null)
        {
            fs.Seek(dataSize, SeekOrigin.Current);
            return;
        }

        byte[] fourcc = new byte[4];
        fs.ReadExactly(fourcc, 0, 4);
        string original = Encoding.ASCII.GetString(fourcc);
        // Replace encv/enca with the clear original format when present.
        ctx.CurrentTrack.Codec = NormalizeSampleEntryCodec(original);
        if (dataSize > 4)
            fs.Seek(dataSize - 4, SeekOrigin.Current);
    }

    private static void ParseMdhd(Stream fs, long dataSize, ParseContext ctx)
    {
        if (dataSize < 20 || ctx.CurrentTrack == null)
        {
            fs.Seek(dataSize, SeekOrigin.Current);
            return;
        }

        byte[] buf = new byte[(int)Math.Min(dataSize, 32)];
        fs.ReadExactly(buf, 0, buf.Length);
        byte version = buf[0];
        // language is packed ISO-639-2/T after timescale+duration
        int langOffset = version == 1 ? 4 + 8 + 8 + 4 + 8 : 4 + 4 + 4 + 4 + 4;
        if (langOffset + 2 <= buf.Length)
        {
            int packed = (buf[langOffset] << 8) | buf[langOffset + 1];
            char c1 = (char)(0x60 + ((packed >> 10) & 0x1F));
            char c2 = (char)(0x60 + ((packed >> 5) & 0x1F));
            char c3 = (char)(0x60 + (packed & 0x1F));
            string lang = new string([c1, c2, c3]);
            if (lang is not ("und" or "\0\0\0"))
                ctx.CurrentTrack.Language = lang;
        }

        if (dataSize > buf.Length)
            fs.Seek(dataSize - buf.Length, SeekOrigin.Current);
    }

    /// <summary>Common layouts; otherwise fall back to "Nch".</summary>
    private static string FormatChannelLayout(int channels) => channels switch
    {
        1 => "1.0",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels}ch"
    };

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
        "tx3g" => "3GPP Timed Text",
        "text" => "QuickTime Text",
        "stpp" => "TTML",
        "wvtt" => "WebVTT",
        "c608" => "CEA-608",
        "c708" => "CEA-708",
        "mp4s" => "MPEG-4 Systems",
        "encv" or "enca" or "enct" => boxType, // replaced by frma when present
        _ => boxType
    };

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
                                    (h == "text" || h == "subt" || h == "sbtl" || h == "clcp") ? "Subtitle Track" :
                                    $"Track ({h})";
        }
    }

    private static void PrintConsolidatedSummary(ParseContext ctx, bool showDuration, bool showSubtitles)
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
            if (!showSubtitles && track.Type == "Subtitle Track") continue;

            Console.ForegroundColor = track.HandlerCode == "vide" ? ConsoleColor.Cyan
                : track.Type == "Subtitle Track" ? ConsoleColor.Gray
                : ConsoleColor.Green;

            string layoutPart = !string.IsNullOrEmpty(track.ChannelLayout) ? $" {track.ChannelLayout}" : "";
            string codecPart = track.Codec + layoutPart;
            bool hasData = !string.IsNullOrEmpty(codecPart) || !string.IsNullOrEmpty(track.Resolution);
            string typeLabel = track.Type + (hasData ? ":" : "");

            string resPart = (track.HandlerCode == "vide" && !string.IsNullOrEmpty(track.Resolution)) ? $" [{track.Resolution}]" : "";
            string drm = track.IsDrm ? " [DRM-protected]" : "";
            string langPart = !string.IsNullOrEmpty(track.Language) && track.Language is not ("eng" or "und")
                ? $" ({track.Language})"
                : "";

            Console.WriteLine($"    -> {typeLabel,-16} {codecPart}{resPart}{drm}{langPart}");
            Console.ResetColor();
        }
    }

    public static void Evict(string file)
    {
        const int FILE_FLAG_NO_BUFFERING = 0x20000000;
        using var evictFs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, (FileOptions)FILE_FLAG_NO_BUFFERING);
    }
}
