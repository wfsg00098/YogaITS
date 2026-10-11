namespace YogaITS.Tray.Services.Color;

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

internal sealed record DisplayInfo(
    NativeMethods.LUID AdapterId,
    uint SourceId,
    uint TargetId,
    uint OutputTechnology,
    string SourceName,
    string WcsDeviceId,
    string WcsDiscovery,
    string FriendlyName,
    string DevicePath,
    string? HardwareId,
    int ProfileScope,
    NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2? AdvancedColor,
    int AdvancedColorError)
{
    public bool IsInternalOutput => OutputTechnology is 6u or 11u or 13u or 0x80000000u;
    public bool HdrEnabled => AdvancedColor?.HighDynamicRangeUserEnabled ?? false;
    public bool AcmEnabled => AdvancedColor?.WideColorUserEnabled ?? false;
    public bool IsBlocked => AdvancedColor is null || HdrEnabled || AcmEnabled ||
        AdvancedColor.Value.activeColorMode != NativeMethods.DISPLAYCONFIG_ADVANCED_COLOR_MODE_SDR;

    public string ColorBlockReason => AdvancedColor is null
        ? $"无法读取高级颜色状态（错误码 {AdvancedColorError}），暂不能切换色域。"
        : HdrEnabled ? "HDR 已开启。"
        : AcmEnabled ? "ACM 已开启。"
        : AdvancedColor.Value.activeColorMode != NativeMethods.DISPLAYCONFIG_ADVANCED_COLOR_MODE_SDR
            ? "系统仍在使用高级颜色模式，请稍后刷新。" : "";
}

internal static class DisplayService
{
    internal static NativeMethods.DISPLAYCONFIG_SET_COLOR_STATE CreateColorStateRequest(DisplayInfo display, bool hdr, bool enabled) => new()
    {
        header = Header(hdr ? NativeMethods.DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE : NativeMethods.DISPLAYCONFIG_DEVICE_INFO_SET_WCG_STATE,
            (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SET_COLOR_STATE>(), display.AdapterId, display.TargetId),
        value = enabled ? 1u : 0u
    };

    public static IReadOnlyList<DisplayInfo> EnumerateActive()
    {
        while (true)
        {
            int rc = NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out uint pc, out uint mc);
            if (rc != 0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {rc}");
            var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pc];
            var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[mc];
            rc = NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (rc == NativeMethods.ERROR_INSUFFICIENT_BUFFER) continue;
            if (rc != 0) throw new InvalidOperationException($"QueryDisplayConfig failed: {rc}");

            var result = new List<DisplayInfo>();
            for (int i = 0; i < pc; i++)
            {
                var p = paths[i];
                var tn = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), p.targetInfo.adapterId, p.targetInfo.id),
                    monitorFriendlyDeviceName = string.Empty,
                    monitorDevicePath = string.Empty
                };
                _ = NativeMethods.DisplayConfigGetTargetDeviceInfo(ref tn);

                var sn = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), p.sourceInfo.adapterId, p.sourceInfo.id),
                    viewGdiDeviceName = string.Empty
                };
                _ = NativeMethods.DisplayConfigGetSourceDeviceInfo(ref sn);

                var ac = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
                {
                    header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2,
                        (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(), p.targetInfo.adapterId, p.targetInfo.id)
                };
                int acRc = NativeMethods.DisplayConfigGetAdvancedColorInfo2(ref ac);

                int scope = NativeMethods.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER;
                if (NativeMethods.ColorProfileGetDisplayUserScope(p.targetInfo.adapterId, p.sourceInfo.id, out int detectedScope) >= 0)
                    scope = detectedScope;

                string path = tn.monitorDevicePath ?? string.Empty;
                string sourceName = sn.viewGdiDeviceName ?? string.Empty;
                string? hwid = ParseHardwareId(path);
                var discovery = FindWcsMonitorDeviceId(sourceName, hwid);

                result.Add(new DisplayInfo(
                    p.targetInfo.adapterId,
                    p.sourceInfo.id,
                    p.targetInfo.id,
                    p.targetInfo.outputTechnology,
                    sourceName,
                    discovery.DeviceId ?? string.Empty,
                    discovery.Detail,
                    tn.monitorFriendlyDeviceName ?? string.Empty,
                    path,
                    hwid,
                    scope,
                    acRc == 0 ? ac : null,
                    acRc));
            }
            return result;
        }
    }

    public static DisplayInfo? FindInternal(IReadOnlyList<DisplayInfo> displays) =>
        displays.FirstOrDefault(d => d.IsInternalOutput && !string.IsNullOrWhiteSpace(d.HardwareId));

    public static DisplayInfo? RefreshDisplayIdentity(DisplayInfo previous) =>
        EnumerateActive().FirstOrDefault(d =>
            d.AdapterId.LowPart == previous.AdapterId.LowPart &&
            d.AdapterId.HighPart == previous.AdapterId.HighPart &&
            d.SourceId == previous.SourceId);

    private sealed record WcsDiscoveryResult(string? DeviceId, string Detail);

    private static WcsDiscoveryResult FindWcsMonitorDeviceId(string sourceName, string? expectedHardwareId)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return new(null, "SourceName is empty; WCS DeviceID discovery refused.");
        if (string.IsNullOrWhiteSpace(expectedHardwareId))
            return new(null, "Panel Hardware ID is unknown; refusing ambiguous WCS DeviceID fallback.");

        var candidates = new List<string>();
        var matches = new List<string>();
        for (uint i = 0; ; i++)
        {
            var dd = new NativeMethods.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty
            };
            if (!NativeMethods.EnumDisplayDevicesW(sourceName, i, ref dd, 0)) break;
            if ((dd.StateFlags & NativeMethods.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
            if (string.IsNullOrWhiteSpace(dd.DeviceID)) continue;
            candidates.Add(dd.DeviceID);
            if ((dd.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) != 0 &&
                DeviceIdMatchesHardwareId(dd.DeviceID, expectedHardwareId))
                matches.Add(dd.DeviceID);
        }

        if (matches.Count == 1)
            return new(matches[0], $"Dynamic unique match: Source={sourceName}, Panel={expectedHardwareId}, CandidateCount={candidates.Count}");
        if (matches.Count == 0)
            return new(null, $"No active nested monitor DeviceID matched Panel={expectedHardwareId}. Candidates=[{string.Join(" | ", candidates)}]");
        return new(null, $"Ambiguous: {matches.Count} active monitor DeviceIDs matched Panel={expectedHardwareId}. Refusing Apply.");
    }

    private static bool DeviceIdMatchesHardwareId(string deviceId, string expectedHardwareId)
    {
        var m = Regex.Match(deviceId, @"^MONITOR\\(?<id>[A-Za-z]{3}[0-9A-Fa-f]{4})\\", RegexOptions.IgnoreCase);
        return m.Success && string.Equals(m.Groups["id"].Value, expectedHardwareId, StringComparison.OrdinalIgnoreCase);
    }

    private static NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, uint size, NativeMethods.LUID adapter, uint id) =>
        new() { type = type, size = size, adapterId = adapter, id = id };

    private static string? ParseHardwareId(string path)
    {
        var m = Regex.Match(path, @"DISPLAY[#\\](?<id>[A-Za-z]{3}[0-9A-Fa-f]{4})[#\\]", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["id"].Value.ToUpperInvariant() : null;
    }
}
