using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GreatMaoNet.Updates;

internal static partial class WindowsServiceAccess
{
    [SupportedOSPlatform("windows")]
    public static void EnsureCanStart(string name)
    {
        nint manager = OpenSCManagerW(null, null, 0x0001); // SC_MANAGER_CONNECT
        if (manager == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            nint service = OpenServiceW(manager, name, 0x0010); // SERVICE_START
            if (service == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Update account must be able to start its Windows service.");
            CloseServiceHandle(service);
        }
        finally { CloseServiceHandle(manager); }
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenServiceW(nint manager, string service, uint access);
    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
