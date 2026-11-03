using System.Text;

namespace ScanSorter.Licensing;

/// <summary>Содержимое лицензии. Machine: три хэша компьютера (0 = компонент не определён).</summary>
public sealed record LicenseData(string Licensee, DateTime Issued, DateTime? Expires, uint[] Machine);

/// <summary>
/// Формат лицензии и кода компьютера. Используется для проверки лицензии приложения.
/// Ключ = подписанная (ECDSA P-256) короткая запись: кому выдан, на какой компьютер, до какой даты.
/// </summary>
public static class LicenseFormat
{
    private static readonly byte[] Magic = { (byte)'S', (byte)'S', (byte)'L', (byte)'1' };
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Prefix = "SS1-";

    public static LicenseData Unpack(byte[] payload)
    {
        using var r = new BinaryReader(new MemoryStream(payload));
        if (!r.ReadBytes(4).SequenceEqual(Magic) || r.ReadByte() != 1) throw new InvalidDataException("Неизвестный формат лицензии.");
        var issued = Epoch.AddDays(r.ReadInt32());
        int exp = r.ReadInt32();
        var machine = new[] { r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32() };
        var name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
        return new LicenseData(name, issued, exp == 0 ? null : Epoch.AddDays(exp), machine);
    }

    public static bool TryParseText(string text, out byte[] payload, out byte[] signature)
    {
        payload = signature = Array.Empty<byte>();
        try
        {
            var t = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
            if (t.StartsWith(Prefix, StringComparison.Ordinal)) t = t[Prefix.Length..];
            var bytes = Base32Decode(t.Replace("-", ""));
            int len = (bytes[0] << 8) | bytes[1];
            if (len < 20 || bytes.Length < 2 + len + 64) return false;
            payload = bytes[2..(2 + len)];
            signature = bytes[(2 + len)..];
            return true;
        }
        catch { return false; }
    }

    // ---------- код компьютера: «XXXXXXXX-XXXXXXXX-XXXXXXXX» ----------

    public static string MachineCodeToText(uint[] parts) => string.Join("-", parts.Select(p => p.ToString("X8")));

    public static bool TryParseMachineCode(string text, out uint[] parts)
    {
        parts = Array.Empty<uint>();
        var chunks = text.Trim().ToUpperInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (chunks.Length != 3) return false;
        var res = new uint[3];
        for (int i = 0; i < 3; i++)
            if (chunks[i].Length != 8 || !uint.TryParse(chunks[i], System.Globalization.NumberStyles.HexNumber, null, out res[i])) return false;
        parts = res;
        return true;
    }

    /// <summary>Подходит ли лицензия к компьютеру: совпасть должны минимум два непустых компонента (замена одного узла, например диска, не ломает лицензию).</summary>
    public static bool MachineMatches(uint[] licensed, uint[] current)
    {
        int matches = 0, filled = 0;
        for (int i = 0; i < 3; i++)
        {
            if (licensed[i] == 0) continue;
            filled++;
            if (licensed[i] == current[i]) matches++;
        }
        return filled >= 2 && matches >= 2;
    }

    // ---------- Base32 (RFC 4648, без заполнения) ----------

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static byte[] Base32Decode(string s)
    {
        var res = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            int v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException();
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { res.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return res.ToArray();
    }
}
