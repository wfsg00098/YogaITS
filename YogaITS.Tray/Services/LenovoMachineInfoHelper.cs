using System.Management;

namespace YogaITS.Tray.Services;

public sealed record MachineInfoSnapshot(
    string Vendor,
    string MachineType,
    string Model)
{
    public string DisplayModel => string.IsNullOrWhiteSpace(Model)
        ? "未知机型"
        : Model.Trim();
}

public static class LenovoMachineInfoHelper
{
    public static MachineInfoSnapshot Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct");

            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    string vendor = item["Vendor"]?.ToString()?.Trim() ?? string.Empty;
                    string machineType = item["Name"]?.ToString()?.Trim() ?? string.Empty;
                    string model = item["Version"]?.ToString()?.Trim() ?? string.Empty;

                    return new(vendor, machineType, model);
                }
            }
        }
        catch
        {
        }

        return new(string.Empty, string.Empty, string.Empty);
    }
}
