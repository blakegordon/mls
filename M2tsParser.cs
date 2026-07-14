namespace Mls;

class M2tsParser
{
    private const int PacketSize = 192;

    static int Go(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: m2tsls [-a] <path_to_m2ts_file_or_wildcard>");
            Console.WriteLine("  -a, --all        Show all tracks (including subtitles)");
            return 1;
        }

        bool showAll = args.Contains("-a") || args.Contains("--all");
        string targetPattern = args.LastOrDefault(a => !a.StartsWith('-')) ?? "";

        if (string.IsNullOrEmpty(targetPattern)) return 2;

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
        // FIX: Re-implemented strict exception filters to only catch expected I/O issues!
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 5;
        }

        foreach (string file in filesToProcess)
        {
            try
            {
                ProcessFile(file, showAll, false);
            }
            // FIX: Ensure we don't swallow fatal parsing bugs inside the loop
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Console.WriteLine($"{file}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }

    public static void ProcessFile(string file, bool showAll, bool showDuration)
    {
        Console.WriteLine($"\n--- {Path.GetFileName(file)} ---");

        var context = new ParseContext();

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65472, FileOptions.SequentialScan);
        context.TotalFileSize = fs.Length;

        int pmtPid = -1;
        bool foundPmt = false;
        bool foundFirstPcr = false;

        byte[] packet = new byte[PacketSize];
        List<byte> videoBuffer = new(8192);

        while (fs.Position < context.TotalFileSize)
        {
            fs.ReadExactly(packet, 0, PacketSize);
            if (packet[4] != 0x47) continue;

            int pid = ((packet[5] & 0x1F) << 8) | packet[6];
            bool pusi = (packet[5] & 0x40) != 0;

            if (pid == 0 && pmtPid == -1 && pusi)
            {
                pmtPid = ParsePatForPmt(packet);
            }
            else if (pid == pmtPid && pusi && !foundPmt)
            {
                ParsePmt(packet, context);
                if (context.Tracks.Count > 0) foundPmt = true;
            }
            else if (foundPmt)
            {
                if (pid == context.PcrPid && !foundFirstPcr)
                {
                    if (TryReadPcr(packet, out ulong pcr))
                    {
                        context.FirstPcr = pcr;
                        foundFirstPcr = true;
                    }
                }

                if (pid == context.VideoPid && !context.FoundResolution)
                {
                    int payloadOffset = 8;
                    if ((packet[7] & 0x20) != 0) payloadOffset += 1 + packet[8];

                    if (payloadOffset < PacketSize)
                    {
                        videoBuffer.AddRange(packet.Skip(payloadOffset));

                        if (videoBuffer.Count > 1024)
                        {
                            TryParseVideoResolution(videoBuffer, context);

                            // Give up if we scan 2MB of video and find nothing
                            if (videoBuffer.Count > 2_000_000) context.FoundResolution = true;
                        }
                    }
                }

                if (foundFirstPcr && context.FoundResolution) break;
            }

            if (fs.Position > 50_000_000) break;
        }

        // Last PCR is almost always in the final packets; a small EOF window is enough
        // for duration/bitrate and avoids reading a multi-MB tail on every file.
        const long TailScanBytes = 512 * 1024;
        if (context.PcrPid != -1 && context.TotalFileSize > TailScanBytes)
        {
            long seekPos = context.TotalFileSize - TailScanBytes;
            seekPos -= seekPos % PacketSize;
            fs.Seek(seekPos, SeekOrigin.Begin);

            while (fs.Position < context.TotalFileSize)
            {
                int bytesRead = fs.Read(packet, 0, PacketSize);
                if (bytesRead < PacketSize) break;
                if (packet[4] != 0x47) continue;

                int pid = ((packet[5] & 0x1F) << 8) | packet[6];
                if (pid == context.PcrPid && TryReadPcr(packet, out ulong pcr))
                {
                    context.LastPcr = pcr;
                }
            }

            if (context.LastPcr > context.FirstPcr)
            {
                context.DurationSeconds = (context.LastPcr - context.FirstPcr) / 27_000_000.0;
            }
        }

        PrintSummary(context, showAll, showDuration);
    }

    // --- The Universal Deep Packet Resolution Decoder ---
    private static void TryParseVideoResolution(List<byte> buffer, ParseContext ctx)
    {
        for (int i = 0; i < buffer.Count - 16; i++)
        {
            if (buffer[i] == 0x00 && buffer[i + 1] == 0x00 && buffer[i + 2] == 0x01)
            {
                int code = buffer[i + 3];

                if (ctx.VideoCodec == "MPEG2" && code == 0xB3)
                {
                    int width = (buffer[i + 4] << 4) | (buffer[i + 5] >> 4);
                    int height = ((buffer[i + 5] & 0x0F) << 8) | buffer[i + 6];
                    SetResolution(ctx, width, height);
                    return;
                }
                else if (ctx.VideoCodec == "VC-1" && code == 0x0F)
                {
                    int profile = (buffer[i + 4] >> 6) & 0x03;
                    if (profile == 3) // Advanced Profile
                    {
                        int widthVal = (buffer[i + 6] << 4) | (buffer[i + 7] >> 4);
                        int heightVal = ((buffer[i + 7] & 0x0F) << 8) | buffer[i + 8];
                        SetResolution(ctx, (widthVal + 1) * 2, (heightVal + 1) * 2);
                    }
                    return;
                }
                else if (ctx.VideoCodec == "H.264/AVC" && (code & 0x1F) == 7)
                {
                    byte[] rbsp = StripEmulationPrevention(buffer, i + 4);
                    ParseAvcSps(rbsp, ctx);
                    if (ctx.FoundResolution) return;
                }
                else if (ctx.VideoCodec == "H.265/HEVC" && ((code >> 1) & 0x3F) == 33)
                {
                    byte[] rbsp = StripEmulationPrevention(buffer, i + 5);
                    ParseHevcSps(rbsp, ctx);
                    if (ctx.FoundResolution) return;
                }
            }
        }

        if (buffer.Count > 1024) buffer.RemoveRange(0, buffer.Count - 16);
    }

    private static void SetResolution(ParseContext ctx, int width, int height)
    {
        var track = ctx.Tracks.FirstOrDefault(t => t.Pid == ctx.VideoPid);
        track?.Resolution = $"[{width}x{height}]";
        ctx.FoundResolution = true;
    }

    private static byte[] StripEmulationPrevention(List<byte> buffer, int startIdx)
    {
        List<byte> unescaped = new(buffer.Count - startIdx);
        for (int i = startIdx; i < buffer.Count; i++)
        {
            if (i < buffer.Count - 2 && buffer[i] == 0x00 && buffer[i + 1] == 0x00 && buffer[i + 2] == 0x03)
            {
                unescaped.Add(0x00);
                unescaped.Add(0x00);
                i += 2; // Jump over the 0x03 emulation prevention byte
            }
            else
            {
                unescaped.Add(buffer[i]);
            }
        }
        return [.. unescaped];
    }

    private static void ParseAvcSps(byte[] data, ParseContext ctx)
    {
        try
        {
            var br = new BitReader(data);
            int profile_idc = br.ReadBits(8);
            br.SkipBits(16); // constraint_set_flags, level_idc
            br.ReadUE(); // seq_parameter_set_id

            if (profile_idc == 100 || profile_idc == 110 || profile_idc == 122 || profile_idc == 244 ||
                profile_idc == 44 || profile_idc == 83 || profile_idc == 86 || profile_idc == 118 ||
                profile_idc == 128 || profile_idc == 138 || profile_idc == 139 || profile_idc == 134 || profile_idc == 135)
            {
                int chroma = br.ReadUE();
                if (chroma == 3) br.SkipBits(1);
                br.ReadUE(); // bit_depth_luma
                br.ReadUE(); // bit_depth_chroma
                br.SkipBits(1); // qpprime_y_zero_transform_bypass_flag
                if (br.ReadBit() == 1)
                { // seq_scaling_matrix_present_flag
                    for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                    {
                        if (br.ReadBit() == 1)
                        {
                            int sizeOfScalingList = i < 6 ? 16 : 64;
                            int lastScale = 8, nextScale = 8;
                            for (int j = 0; j < sizeOfScalingList; j++)
                            {
                                if (nextScale != 0)
                                {
                                    int delta_scale = br.ReadSE();
                                    nextScale = (lastScale + delta_scale + 256) % 256;
                                }
                                lastScale = nextScale == 0 ? lastScale : nextScale;
                            }
                        }
                    }
                }
            }

            br.ReadUE(); // log2_max_frame_num_minus4
            int pic_order_cnt_type = br.ReadUE();
            if (pic_order_cnt_type == 0)
            {
                br.ReadUE(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (pic_order_cnt_type == 1)
            {
                br.SkipBits(1);
                br.ReadSE();
                br.ReadSE();
                int num_ref_frames = br.ReadUE();
                for (int i = 0; i < num_ref_frames; i++) br.ReadSE();
            }
            br.ReadUE(); // max_num_ref_frames
            br.SkipBits(1); // gaps_in_frame_num_value_allowed_flag

            int pic_width_in_mbs_minus1 = br.ReadUE();
            int pic_height_in_map_units_minus1 = br.ReadUE();
            int frame_mbs_only_flag = br.ReadBit();

            int width = (pic_width_in_mbs_minus1 + 1) * 16;
            int height = (2 - frame_mbs_only_flag) * (pic_height_in_map_units_minus1 + 1) * 16;

            if (frame_mbs_only_flag == 0) br.SkipBits(1);
            br.SkipBits(1); // direct_8x8_inference_flag

            if (br.ReadBit() == 1) // frame_cropping_flag
            {
                int crop_left = br.ReadUE();
                int crop_right = br.ReadUE();
                int crop_top = br.ReadUE();
                int crop_bottom = br.ReadUE();

                width -= (crop_left + crop_right) * 2;
                height -= (crop_top + crop_bottom) * 2 * (2 - frame_mbs_only_flag);
            }

            SetResolution(ctx, width, height);
        }
        catch { /* Malformed SPS, loop will try next packet */ }
    }

    private static void ParseHevcSps(byte[] data, ParseContext ctx)
    {
        try
        {
            var br = new BitReader(data);
            br.SkipBits(4);
            int max_sub_layers = br.ReadBits(3);
            br.SkipBits(1);

            // Profile Tier Level
            br.SkipBits(88);
            bool[] subLayerProfilePresent = new bool[8];
            bool[] subLayerLevelPresent = new bool[8];
            for (int i = 0; i < max_sub_layers; i++)
            {
                subLayerProfilePresent[i] = br.ReadBit() == 1;
                subLayerLevelPresent[i] = br.ReadBit() == 1;
            }
            if (max_sub_layers > 0)
            {
                for (int i = max_sub_layers; i < 8; i++) br.SkipBits(2);
            }
            for (int i = 0; i < max_sub_layers; i++)
            {
                if (subLayerProfilePresent[i]) br.SkipBits(88);
                if (subLayerLevelPresent[i]) br.SkipBits(8);
            }

            br.ReadUE(); // sps_seq_parameter_set_id
            int chroma = br.ReadUE();
            if (chroma == 3) br.SkipBits(1);

            int width = br.ReadUE();
            int height = br.ReadUE();

            if (br.ReadBit() == 1) // conformance_window_flag
            {
                int conf_win_left_offset = br.ReadUE();
                int conf_win_right_offset = br.ReadUE();
                int conf_win_top_offset = br.ReadUE();
                int conf_win_bottom_offset = br.ReadUE();

                width -= (conf_win_left_offset + conf_win_right_offset) * 2;
                height -= (conf_win_top_offset + conf_win_bottom_offset) * 2;
            }

            SetResolution(ctx, width, height);
        }
        catch { /* Malformed SPS, loop will try next packet */ }
    }

    // ... (ParsePatForPmt, ParsePmt, and TryReadPcr remain identical)
    private static int ParsePatForPmt(byte[] packet)
    {
        int payloadOffset = 8;
        if ((packet[7] & 0x20) != 0) payloadOffset += packet[8] + 1;

        int pointerField = packet[payloadOffset];
        int tableStart = payloadOffset + 1 + pointerField;
        int sectionLength = ((packet[tableStart + 1] & 0x0F) << 8) | packet[tableStart + 2];

        int offset = tableStart + 8;
        int endOfPat = tableStart + 3 + sectionLength - 4;

        while (offset < endOfPat && offset + 3 < packet.Length)
        {
            int programNum = (packet[offset] << 8) | packet[offset + 1];
            int pid = ((packet[offset + 2] & 0x1F) << 8) | packet[offset + 3];

            if (programNum != 0) return pid;
            offset += 4;
        }

        return -1;
    }

    private static void ParsePmt(byte[] packet, ParseContext ctx)
    {
        int payloadOffset = 8;
        if ((packet[7] & 0x20) != 0) payloadOffset += packet[8] + 1;

        int pointerField = packet[payloadOffset];
        int tableStart = payloadOffset + 1 + pointerField;

        if (packet[tableStart] != 0x02) return;

        ctx.PcrPid = ((packet[tableStart + 8] & 0x1F) << 8) | packet[tableStart + 9];

        int sectionLength = ((packet[tableStart + 1] & 0x0F) << 8) | packet[tableStart + 2];
        int programInfoLength = ((packet[tableStart + 10] & 0x0F) << 8) | packet[tableStart + 11];

        int streamOffset = tableStart + 12 + programInfoLength;
        int endOfTable = tableStart + 3 + sectionLength - 4;

        while (streamOffset < endOfTable && streamOffset + 5 <= packet.Length)
        {
            int streamType = packet[streamOffset];
            int elementaryPid = ((packet[streamOffset + 1] & 0x1F) << 8) | packet[streamOffset + 2];
            int esInfoLength = ((packet[streamOffset + 3] & 0x0F) << 8) | packet[streamOffset + 4];

            var track = new TrackInfo { Pid = elementaryPid };

            switch (streamType)
            {
                case 0x02: track.Type = "Video Track"; track.Codec = "MPEG2"; break;
                case 0x1B: track.Type = "Video Track"; track.Codec = "H.264/AVC"; break;
                case 0x24: track.Type = "Video Track"; track.Codec = "H.265/HEVC"; break;
                case 0xEA: track.Type = "Video Track"; track.Codec = "VC-1"; break;

                case 0x80: track.Type = "Audio Track"; track.Codec = "LPCM"; break;
                case 0x81: track.Type = "Audio Track"; track.Codec = "AC3"; break;
                case 0x82: track.Type = "Audio Track"; track.Codec = "DTS"; break;
                case 0x83: track.Type = "Audio Track"; track.Codec = "TrueHD"; break;
                case 0x84: track.Type = "Audio Track"; track.Codec = "E-AC3"; break;
                case 0x85: track.Type = "Audio Track"; track.Codec = "DTS-HD HR"; break;
                case 0x86: track.Type = "Audio Track"; track.Codec = "DTS-HD MA"; break;

                case 0x90: track.Type = "Subtitle Track"; track.Codec = "HDMV/PGS"; break;

                default: track.Type = "Data/Unknown"; track.Codec = $"0x{streamType:X2}"; break;
            }

            if (track.Type != "Data/Unknown")
            {
                ctx.Tracks.Add(track);

                if (ctx.VideoPid == -1 && track.Type == "Video Track")
                {
                    ctx.VideoPid = elementaryPid;
                    ctx.VideoCodec = track.Codec;
                }
            }

            streamOffset += 5 + esInfoLength;
        }

        if (ctx.VideoPid == -1) ctx.FoundResolution = true;
    }

    private static bool TryReadPcr(byte[] packet, out ulong pcr)
    {
        pcr = 0;
        if ((packet[7] & 0x20) == 0) return false;

        int adaptLen = packet[8];
        if (adaptLen == 0) return false;
        if ((packet[9] & 0x10) == 0) return false;

        ulong pcr_base = ((ulong)packet[10] << 25) | ((ulong)packet[11] << 17) |
                         ((ulong)packet[12] << 9) | ((ulong)packet[13] << 1) | ((ulong)packet[14] >> 7);
        ulong pcr_ext = (ulong)(((packet[14] & 0x01) << 8) | packet[15]);

        pcr = (pcr_base * 300) + pcr_ext;
        return true;
    }

    private static void PrintSummary(ParseContext ctx, bool showAll, bool showDuration)
    {
        Console.WriteLine($"  {"Format:",-21} Blu-ray Video (M2TS)");

        if (ctx.DurationSeconds > 0)
        {
            ctx.BitrateMbps = (ctx.TotalFileSize * 8.0) / ctx.DurationSeconds / 1_000_000.0;

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
            if (!showAll && track.Type == "Subtitle Track") continue;

            Console.ForegroundColor = track.Type == "Video Track" ? ConsoleColor.Cyan : track.Type == "Audio Track" ? ConsoleColor.Green : ConsoleColor.Gray;

            string resPart = !string.IsNullOrEmpty(track.Resolution) ? $" {track.Resolution}" : "";
            Console.WriteLine($"    -> {track.Type,-16} {track.Codec}{resPart}");
            Console.ResetColor();
        }
    }
}

// --- Custom Exp-Golomb Bit Reader ---
class BitReader(byte[] data)
{
    private readonly byte[] _data = data;
    private int _bitOffset;

    public int ReadBit()
    {
        if (_bitOffset / 8 >= _data.Length) return 0;
        int val = (_data[_bitOffset / 8] >> (7 - (_bitOffset % 8))) & 1;
        _bitOffset++;
        return val;
    }

    public int ReadBits(int count)
    {
        int val = 0;
        for (int i = 0; i < count; i++) val = (val << 1) | ReadBit();
        return val;
    }

    public int ReadUE()
    {
        int leadingZeroBits = -1;
        for (int b = 0; b == 0; leadingZeroBits++) b = ReadBit();
        return (int)((1 << leadingZeroBits) - 1 + ReadBits(leadingZeroBits));
    }

    public int ReadSE()
    {
        int ue = ReadUE();
        return ((ue & 1) != 0 ? 1 : -1) * ((ue + 1) / 2);
    }

    public void SkipBits(int count) { _bitOffset += count; }
}
