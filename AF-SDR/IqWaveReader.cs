using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AfSdr;

internal sealed record IqWaveInfo(string Container, ushort Format, ushort Bits, uint Rate, ushort BlockAlign,
    long DataOffset, long DataBytes, uint? FilenameFrequency)
{
    internal long Frames => DataBytes / BlockAlign;
    internal double Seconds => Frames / (double)Rate;
    internal bool Playable => Rate is >= 250_000 and <= 3_200_000;
    public override string ToString() => $"{Container} / {(Format == 3 ? "float" : "PCM")}{Bits} / 2ch / {Rate:N0} S/s / {Seconds:F6} 秒";
}

// Finished RIFF/RF64 recordings only. Chunk offsets and lengths never narrow to 32 bits.
internal sealed class IqWaveReader : IDisposable
{
    private readonly Stream stream;
    private byte[] bytes = [];
    internal IqWaveInfo Info { get; }
    internal long Position { get; private set; }
    internal IqWaveReader(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess), path) { }
    internal IqWaveReader(Stream stream, string path)
    {
        this.stream = stream;
        try { Info = Parse(stream, path); Seek(0); }
        catch { stream.Dispose(); throw; }
    }

    internal static uint? FrequencyFromName(string path)
    {
        var matches = Regex.Matches(Path.GetFileNameWithoutExtension(path), @"(?<![\d.])(?<n>\d+(?:\.\d+)?)(?<u>MHz|kHz|Hz)(?![a-z])", RegexOptions.IgnoreCase);
        if (matches.Count != 1) return null;
        var m = matches[0];
        if (!decimal.TryParse(m.Groups["n"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return null;
        decimal multiplier = m.Groups["u"].Value.ToLowerInvariant() switch { "mhz" => 1_000_000, "khz" => 1000, _ => 1 };
        if (n > uint.MaxValue / multiplier) return null;
        n *= multiplier;
        return n >= 1 && n <= uint.MaxValue && n == decimal.Truncate(n) ? (uint)n : null;
    }

    private static IqWaveInfo Parse(Stream stream, string path)
    {
        using var r = new BinaryReader(stream, Encoding.ASCII, true);
        string Four() => Encoding.ASCII.GetString(r.ReadBytes(4));
        long Long(ulong n) => n <= long.MaxValue ? (long)n : throw new InvalidDataException("64-bitチャンク長が範囲外です。");
        if (stream.Length < 12) throw new InvalidDataException("WAVヘッダーが不足しています。");
        string container = Four(); uint size = r.ReadUInt32();
        if (container is not ("RIFF" or "RF64") || Four() != "WAVE") throw new InvalidDataException("RIFF/RF64 WAVEではありません。");
        bool rf64 = container == "RF64", ds = false, fmt = false, data = false;
        if (rf64 && size != uint.MaxValue) throw new InvalidDataException("RF64サイズ識別子が不正です。");
        long end = rf64 ? stream.Length : checked(8L + size), dsData = -1, dsCount = 0, dataOffset = 0, dataBytes = 0;
        var sizes = new List<(string Id, long Size)>();
        ushort format = 0, bits = 0, align = 0; uint rate = 0;
        if (end > stream.Length || end < 12) throw new InvalidDataException("WAVが切断されています。");
        while (stream.Position < end)
        {
            if (end - stream.Position < 8) throw new InvalidDataException("チャンクヘッダーが切断されています。");
            string id = Four(); uint small = r.ReadUInt32(); long length = small, start = stream.Position;
            if (small == uint.MaxValue)
            {
                if (!rf64 || !ds) throw new InvalidDataException("ds64が必要です。");
                if (id == "data") length = dsData;
                else
                {
                    int index = sizes.FindIndex(s => s.Id == id);
                    if (index < 0) throw new InvalidDataException("ds64サイズテーブルが不足しています。");
                    length = sizes[index].Size; sizes.RemoveAt(index);
                }
            }
            long next = checked(start + length + (length & 1));
            if (length < 0 || next > end) throw new InvalidDataException("チャンクがファイル範囲外です。");
            if (id == "ds64")
            {
                if (!rf64 || ds || start != 20 || length < 28) throw new InvalidDataException("ds64が不正です。");
                end = checked(Long(r.ReadUInt64()) + 8);
                dsData = Long(r.ReadUInt64()); dsCount = Long(r.ReadUInt64());
                uint count = r.ReadUInt32();
                if (end > stream.Length || end < next || 28L + count * 12L != length || count > 65536) throw new InvalidDataException("ds64サイズが不正です。");
                for (uint i = 0; i < count; i++) sizes.Add((Four(), Long(r.ReadUInt64())));
                ds = true;
            }
            else if (id == "fmt ")
            {
                if (fmt || length < 16) throw new InvalidDataException("fmtチャンクが不正です。");
                format = r.ReadUInt16(); ushort channels = r.ReadUInt16(); rate = r.ReadUInt32();
                uint byteRate = r.ReadUInt32(); align = r.ReadUInt16(); bits = r.ReadUInt16();
                if (format == 0xfffe)
                {
                    if (length < 40) throw new InvalidDataException("拡張fmtが不足しています。");
                    ushort extra = r.ReadUInt16(), valid = r.ReadUInt16(); r.ReadUInt32();
                    var guid = new Guid(r.ReadBytes(16));
                    if (extra < 22 || 18L + extra > length || valid != bits) throw new InvalidDataException("有効ビット数/拡張fmtが非対応です。");
                    format = guid == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1
                        : guid == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
                }
                if (channels != 2 || !(format == 1 && bits is 8 or 16 || format == 3 && bits == 32))
                    throw new InvalidDataException("対応形式は2ch PCM8/PCM16/float32です。");
                if (rate == 0 || align != bits / 8 * 2 || (ulong)rate * align != byteRate) throw new InvalidDataException("サンプル形式の整合性が不正です。");
                fmt = true;
            }
            else if (id == "data")
            {
                if (data) throw new InvalidDataException("複数dataチャンクには対応していません。");
                dataOffset = start; dataBytes = length; data = true;
            }
            stream.Position = next;
        }
        if (!fmt || !data || rf64 && (!ds || dsData != dataBytes) || dataBytes % align != 0
            || dsCount != 0 && dsCount != dataBytes / align) throw new InvalidDataException("IQ WAVのサイズまたは必須チャンクが不正です。");
        return new(container, format, bits, rate, align, dataOffset, dataBytes, FrequencyFromName(path));
    }

    internal void Seek(long frame)
    {
        if (frame < 0 || frame > Info.Frames) throw new ArgumentOutOfRangeException(nameof(frame));
        stream.Position = checked(Info.DataOffset + frame * Info.BlockAlign); Position = frame;
    }

    internal float[] Read(int frames, bool swap = false)
    {
        if (frames <= 0 || frames > 65536) throw new ArgumentOutOfRangeException(nameof(frames));
        int count = (int)Math.Min(frames, Info.Frames - Position), size = checked(count * Info.BlockAlign);
        if (bytes.Length < size) bytes = new byte[size];
        stream.ReadExactly(bytes.AsSpan(0, size));
        var result = new float[count * 2];
        for (int n = 0; n < result.Length; n++)
        {
            float value = Info.Bits switch
            {
                8 => (bytes[n] - 128) / 128f,
                16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(n * 2, 2)) / 32768f,
                _ => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(n * 4, 4)))
            };
            if (!float.IsFinite(value)) throw new InvalidDataException($"NaN/Infinity: IQ frame {Position + n / 2}");
            result[swap ? n ^ 1 : n] = value;
        }
        Position += count; return result;
    }
    public void Dispose() => stream.Dispose();
}
