param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$Text
)

$source = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class ConsoleInjector {
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    public struct KEY_EVENT_RECORD {
        [FieldOffset(0)] public int bKeyDown;
        [FieldOffset(4)] public ushort wRepeatCount;
        [FieldOffset(6)] public ushort wVirtualKeyCode;
        [FieldOffset(8)] public ushort wVirtualScanCode;
        [FieldOffset(10)] public char UnicodeChar;
        [FieldOffset(12)] public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_RECORD {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteConsoleInputW(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength,
        out uint lpNumberOfEventsWritten);

    public static string Send(uint pid, string text) {
        FreeConsole();
        if (!AttachConsole(pid)) return "AttachConsole failed: " + Marshal.GetLastWin32Error();
        IntPtr h = CreateFileW("CONIN$", 0x80000000u | 0x40000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
        if (h == new IntPtr(-1)) {
            int e = Marshal.GetLastWin32Error();
            FreeConsole();
            return "CreateFile(CONIN$) failed: " + e;
        }
        var list = new List<INPUT_RECORD>();
        foreach (char c in text) {
            ushort vk = 0;
            if (c == '\r' || c == '\n') vk = 0x0D;
            else if (c == ' ') vk = 0x20;
            else if (c >= 'a' && c <= 'z') vk = (ushort)(0x41 + (c - 'a'));
            else if (c >= 'A' && c <= 'Z') vk = (ushort)(0x41 + (c - 'A'));
            else if (c >= '0' && c <= '9') vk = (ushort)(0x30 + (c - '0'));

            var down = new INPUT_RECORD();
            down.EventType = 1;
            down.KeyEvent.bKeyDown = 1;
            down.KeyEvent.wRepeatCount = 1;
            down.KeyEvent.wVirtualKeyCode = vk;
            down.KeyEvent.UnicodeChar = c;
            list.Add(down);

            var up = down;
            up.KeyEvent.bKeyDown = 0;
            list.Add(up);
        }
        uint written;
        bool ok = WriteConsoleInputW(h, list.ToArray(), (uint)list.Count, out written);
        int err = Marshal.GetLastWin32Error();
        FreeConsole();
        return ok ? ("OK written=" + written) : ("WriteConsoleInput failed: " + err);
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp
if (-not $Text.EndsWith("`r") -and -not $Text.EndsWith("`n")) { $Text += "`r" }
$result = [ConsoleInjector]::Send([uint32]$ProcessId, $Text)
$result | Out-File -FilePath "$PSScriptRoot\send-result.txt" -Encoding utf8
Write-Output $result
