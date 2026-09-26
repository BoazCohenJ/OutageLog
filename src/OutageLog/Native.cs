using System;
using System.Runtime.InteropServices;

namespace OutageLog;

internal static class Native
{
    public const int AttachParentProcess = -1;
    public const uint DnsTypeA = 1;
    public const uint DnsQueryBypassCache = 0x8;
    public const uint DnsQueryNoHostsFile = 0x40;
    public const int DnsFreeRecordList = 1;

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, EntryPoint = "DnsQuery_W")]
    public static extern int DnsQuery(string name, uint type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DllImport("dnsapi.dll")]
    public static extern void DnsRecordListFree(IntPtr list, int freeType);

    [DllImport("iphlpapi.dll")]
    public static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}
