namespace WindowsCleaner

open System
open System.Runtime.InteropServices

module Win32 =
    [<DllImport("shell32.dll", CharSet = CharSet.Unicode)>]
    extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint32 flags)

    let SHERB_NOCONFIRMATION = 0x00000001u
    let SHERB_NOPROGRESSUI = 0x00000002u
    let SHERB_NOSOUND = 0x00000004u

    [<DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")>]
    extern int DnsFlushResolverCache()

    [<DllImport("kernel32.dll")>]
    extern bool SetConsoleMode(IntPtr hConsoleHandle, uint32 dwMode)

    [<DllImport("kernel32.dll")>]
    extern bool GetConsoleMode(IntPtr hConsoleHandle, uint32& lpMode)

    [<DllImport("kernel32.dll")>]
    extern IntPtr GetStdHandle(int nStdHandle)

    let STD_OUTPUT_HANDLE = -11
    let ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004u

    let checkAdminPrivileges () =
        let identity = System.Security.Principal.WindowsIdentity.GetCurrent()
        let principal = System.Security.Principal.WindowsPrincipal(identity)
        principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
