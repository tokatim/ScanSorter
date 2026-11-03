using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ScanSorter.Licensing;

/// <summary>
/// Код компьютера из трёх независимых признаков: UUID системной платы (SMBIOS), идентификатор установки Windows и серийный номер диска.
/// Каждый признак хранится как короткий хэш; лицензия подходит, если совпали минимум два (замена диска или переустановка Windows не ломают её).
/// </summary>
public static class MachineId
{
    private static uint[]? _cached;

    public static uint[] Parts() => _cached ??= new[] { Hash(SmbiosUuid()), Hash(WindowsGuid()), Hash(DiskSerial()) };

    public static string Code => LicenseFormat.MachineCodeToText(Parts());

    private static uint Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("SS1|" + value.Trim().ToUpperInvariant()));
        var v = BitConverter.ToUInt32(h, 0);
        return v == 0 ? 1u : v;
    }

    private static string? SmbiosUuid()
    {
        var uuid = Wmi("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID");
        // у некоторых компьютеров стоит заглушка из одинаковых символов: это не признак
        return uuid != null && uuid.Replace("-", "").Distinct().Count() > 2 ? uuid : null;
    }

    private static string? WindowsGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch { return null; }
    }

    private static string? DiskSerial()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber, Index FROM Win32_DiskDrive");
            var serials = searcher.Get().Cast<ManagementObject>()
                .OrderBy(d => Convert.ToInt32(d["Index"] ?? 0))
                .Select(d => (d["SerialNumber"] as string)?.Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            return serials.FirstOrDefault();
        }
        catch { return null; }
    }

    private static string? Wmi(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            return searcher.Get().Cast<ManagementObject>().Select(o => o[property] as string).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        }
        catch { return null; }
    }
}
