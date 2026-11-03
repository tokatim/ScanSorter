using System.Security.Cryptography;
using System.Text;
using ScanSorter.Config;

namespace ScanSorter.Licensing;

public sealed record LicenseStatus(bool IsValid, string Message, LicenseData? Data);

/// <summary>
/// Проверка лицензии: подпись ключа (открытый ключ зашит в программу, закрытый есть только у автора), совпадение с компьютером
/// и срок действия. Ключ хранится в %LocalAppData%\ScanSorter\license.key.
/// </summary>
public static class LicenseManager
{
    private static string LicenseFile => Path.Combine(AppPaths.Root, "license.key");
    private static string StateFile => Path.Combine(AppPaths.Root, "license.state");

    /// <summary>Проверка установленной лицензии на этом компьютере.</summary>
    public static LicenseStatus Check()
    {
        string text;
        try { text = File.Exists(LicenseFile) ? File.ReadAllText(LicenseFile) : ""; }
        catch { text = ""; }
        if (string.IsNullOrWhiteSpace(text)) return new LicenseStatus(false, "Лицензия не установлена.", null);

        var status = Evaluate(text, DateTime.UtcNow, MachineId.Parts());
        if (!status.IsValid) return status;

        // защита от перевода системных часов назад (важно для лицензий с ограниченным сроком)
        var last = ReadLastSeen();
        var today = DateTime.UtcNow.Date;
        if (status.Data!.Expires.HasValue && last.HasValue && today < last.Value.AddDays(-1))
            return new LicenseStatus(false, "Системная дата установлена раньше, чем при предыдущем запуске. Исправьте дату и время Windows.", status.Data);
        if (!last.HasValue || today > last.Value) WriteLastSeen(today);
        return status;
    }

    /// <summary>Проверяет ключ и, если он подходит этому компьютеру, сохраняет его.</summary>
    public static LicenseStatus Install(string text)
    {
        var status = Evaluate(text, DateTime.UtcNow, MachineId.Parts());
        if (!status.IsValid) return status;
        try { File.WriteAllText(LicenseFile, text.Trim()); }
        catch (Exception ex) { return new LicenseStatus(false, "Не удалось сохранить лицензию: " + ex.Message, null); }
        WriteLastSeen(DateTime.UtcNow.Date);
        return status;
    }

    /// <summary>Бросает исключение, если лицензии нет: вызывается перед самой работой (обработкой файлов).</summary>
    public static void Require()
    {
        var st = Check();
        if (!st.IsValid) throw new UnauthorizedAccessException(st.Message);
    }

    /// <summary>Чистая проверка текста ключа (без файлов): используется и программой, и проверками.</summary>
    public static LicenseStatus Evaluate(string text, DateTime utcNow, uint[] machine)
    {
        if (string.IsNullOrWhiteSpace(LicensePublicKey.Spki))
            return new LicenseStatus(false, "В эту сборку программы не встроен ключ проверки лицензий.", null);
        if (!LicenseFormat.TryParseText(text, out var payload, out var signature))
            return new LicenseStatus(false, "Ключ введён неверно: проверьте, что скопирован целиком.", null);
        if (!VerifySignature(payload, signature))
            return new LicenseStatus(false, "Ключ недействителен (подпись не совпала).", null);

        LicenseData data;
        try { data = LicenseFormat.Unpack(payload); }
        catch { return new LicenseStatus(false, "Ключ повреждён.", null); }

        if (!LicenseFormat.MachineMatches(data.Machine, machine))
            return new LicenseStatus(false, "Этот ключ выдан для другого компьютера.", data);
        if (data.Expires.HasValue && utcNow.Date > data.Expires.Value.Date)
            return new LicenseStatus(false, $"Срок лицензии истёк {data.Expires.Value:dd.MM.yyyy}.", data);

        var until = data.Expires.HasValue ? $"до {data.Expires.Value:dd.MM.yyyy}" : "бессрочная";
        return new LicenseStatus(true, $"Лицензия: {data.Licensee}, {until}", data);
    }

    private static bool VerifySignature(byte[] payload, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(LicensePublicKey.Spki), out _);
            return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    // ---------- «последняя дата запуска» с подписью, привязанной к компьютеру ----------

    private static byte[] StateKey() => SHA256.HashData(Encoding.UTF8.GetBytes("ss-state|" + MachineId.Code));

    private static DateTime? ReadLastSeen()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            var parts = File.ReadAllText(StateFile).Trim().Split(';');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var days)) return null;
            var mac = Convert.ToHexString(HMACSHA256.HashData(StateKey(), Encoding.ASCII.GetBytes(parts[0])))[..16];
            return mac == parts[1] ? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(days) : null;
        }
        catch { return null; }
    }

    private static void WriteLastSeen(DateTime utcDate)
    {
        try
        {
            var days = (int)(utcDate.Date - new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
            var mac = Convert.ToHexString(HMACSHA256.HashData(StateKey(), Encoding.ASCII.GetBytes(days.ToString())))[..16];
            File.WriteAllText(StateFile, $"{days};{mac}");
        }
        catch { /* не мешает работе */ }
    }
}
