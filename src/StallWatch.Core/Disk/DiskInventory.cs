using System.Management;
using System.Runtime.Versioning;

namespace StallWatch.Core.Disk;

/// <summary>Maps physical disk numbers to something a human recognises, e.g. "C: (Samsung SSD 980 PRO 2TB)".</summary>
[SupportedOSPlatform("windows")]
public static class DiskInventory
{
    const string StorageNs = @"root\Microsoft\Windows\Storage";

    public static Dictionary<int, string> Load()
    {
        var names = new Dictionary<int, string>();
        try
        {
            var letters = new Dictionary<int, List<string>>();
            using (var parts = new ManagementObjectSearcher(StorageNs, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
                foreach (ManagementObject p in parts.Get())
                {
                    var letter = Convert.ToChar(p["DriveLetter"] ?? '\0');
                    if (letter == '\0') continue;
                    var disk = Convert.ToInt32(p["DiskNumber"]);
                    if (!letters.TryGetValue(disk, out var list)) letters[disk] = list = [];
                    list.Add(letter + ":");
                }

            using var disks = new ManagementObjectSearcher(StorageNs, "SELECT Number, FriendlyName FROM MSFT_Disk");
            foreach (ManagementObject d in disks.Get())
            {
                var number = Convert.ToInt32(d["Number"]);
                var model = (d["FriendlyName"] as string ?? "").Trim();
                names[number] = letters.TryGetValue(number, out var l)
                    ? $"{string.Join(" ", l.Order())} ({model})"
                    : $"disk {number} ({model})";
            }
        }
        catch (Exception)
        {
            // Inventory is cosmetic; fall back to plain disk numbers.
        }
        return names;
    }
}
