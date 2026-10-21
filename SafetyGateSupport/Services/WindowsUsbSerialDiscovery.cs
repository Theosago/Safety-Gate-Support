using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SafetyGateSupport.Hokuyo;

namespace SafetyGateSupport.Services;

/// <summary>Uses the present PnP device tree, including USB driver child interfaces.
/// Does not rely on the Ports device class or the legacy serial-port registry map.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsUsbSerialDiscovery
{
    private static readonly IntPtr InvalidHandle = new(-1);

    public static IReadOnlyList<UamSerialDevice> Enumerate()
    {
        // All present classes: some vendor serial interfaces do not appear in the Ports class.
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4);
        if (set == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        var found = new List<UamSerialDevice>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref device))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break; // ERROR_NO_MORE_ITEMS
                    throw new Win32Exception(error);
                }
                IntPtr raw = SetupDiOpenDevRegKey(set, ref device, 1, 0, 1, 0x20019);
                if (raw == InvalidHandle) continue;
                string? port;
                using (var key = RegistryKey.FromHandle(new SafeRegistryHandle(raw, ownsHandle: true)))
                {
                    try { port = key.GetValue("PortName") as string; }
                    catch (IOException) { continue; } // Unplugged between enumeration and reading its key.
                    catch (UnauthorizedAccessException) { continue; }
                }
                if (port is null || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;

                string instance = DeviceId(device.DevInst);
                string usb = UsbAncestor(device.DevInst);
                if (instance.Length == 0) continue;
                string description = Property(set, ref device, 12); // SPDRP_FRIENDLYNAME
                if (description.Length == 0) description = Property(set, ref device, 0); // SPDRP_DEVICEDESC
                found.Add(new UamSerialDevice(port, instance, usb, description, Property(set, ref device, 11)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return found;
    }

    private static string UsbAncestor(uint node)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            string id = DeviceId(node);
            // Do not treat a Bluetooth/virtual serial endpoint behind a USB radio as a USB scanner.
            if (id.StartsWith(@"BTH", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) return "";
            if (id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)) return id;
            if (CM_Get_Parent(out uint parent, node, 0) != 0) return "";
            node = parent;
        }
        return "";
    }
    private static string DeviceId(uint node)
    {
        var id = new StringBuilder(1024);
        return CM_Get_Device_IDW(node, id, id.Capacity, 0) == 0 ? id.ToString() : "";
    }
    private static string Property(IntPtr set, ref DeviceInfo device, uint property)
    {
        var bytes = new byte[4096];
        return SetupDiGetDeviceRegistryPropertyW(set, ref device, property, out _, bytes, (uint)bytes.Length, out _)
            ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo device);
    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DeviceInfo device, uint scope, uint profile, uint type, uint access);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo device, uint property, out uint type, byte[] data, uint size, out uint needed);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);
}
