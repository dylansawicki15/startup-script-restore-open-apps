// WindowLayout.cs - compiled by startup-apps.ps1 (Add-Type). Two jobs:
//
//   Windows          Where is each app window right now - which Win+Tab desktop,
//                    which monitor, what size - and put a window back on a monitor.
//                    Documented Win32 only.
//
//   VirtualDesktops  Move ANOTHER app's window to a Win+Tab desktop. Windows only
//                    documents moving your own windows, so this uses the shell's
//                    internal interfaces, whose IDs change between Windows releases.
//                    Open() throws NotSupportedException unless the shell accepts one
//                    of the IDs below, and InvalidOperationException unless the
//                    shell's desktop list matches the registry's - so after a Windows
//                    update desktop moves switch off instead of calling a wrong method.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace StartupApps
{
    public sealed class WindowInfo
    {
        public IntPtr Hwnd;
        public string ProcessName;   // as Get-Process names it: the exe file name without .exe
        public string Exe;           // full image path
        public string Aumid;         // packaged (Store/MSIX) apps only, otherwise null
        public bool Elevated;        // also true when Windows won't tell us
        public string Title;
        public int Desktop;          // 1-based, the number Win+Tab shows; 0 = not on a numbered desktop
        public string Monitor;       // DISPLAY1, DISPLAY2, ... - for the log; the rectangle identifies it
        public int MonLeft, MonTop, MonRight, MonBottom;
        public int ShowCmd;          // 1 normal, 2 minimized, 3 maximized
        public int Flags;            // WINDOWPLACEMENT.flags, e.g. "restore to maximized"
        public int Left, Top, Right, Bottom;   // the restored (un-maximized) rectangle
    }

    public static class DesktopOrder
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

        // Desktop IDs in Win+Tab order. Explorer rewrites this value whenever desktops
        // are added, removed or reordered. Empty when only one desktop has ever existed.
        public static List<Guid> Read()
        {
            var ids = new List<Guid>();
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                var raw = k == null ? null : k.GetValue("VirtualDesktopIDs") as byte[];
                if (raw == null) return ids;
                if (raw.Length % 16 != 0)
                    throw new InvalidDataException(string.Format(
                        @"HKCU\{0}\VirtualDesktopIDs is {1} bytes; expected a whole number of 16-byte GUIDs", Key, raw.Length));
                for (int i = 0; i < raw.Length; i += 16)
                {
                    var b = new byte[16];
                    Array.Copy(raw, i, b, 0, 16);
                    ids.Add(new Guid(b));
                }
            }
            return ids;
        }
    }

    public static class Windows
    {
        // --- queries ------------------------------------------------------------

        // Every window a person would call "an open app": visible, top-level, titled,
        // not a tool palette, and placed on a desktop by the shell (which rules out
        // shell surfaces such as the input panel and hosted Settings frames).
        public static List<WindowInfo> List()
        {
            UsePhysicalPixels();
            var order = DesktopOrder.Read();
            var result = new List<WindowInfo>();
            EnumWindows((h, _) =>
            {
                var w = Describe(h, order);
                if (w != null) result.Add(w);
                return true;
            }, IntPtr.Zero);
            return result;   // EnumWindows order = z-order, topmost first
        }

        public static bool MonitorExists(int left, int top, int right, int bottom)
        {
            UsePhysicalPixels();
            var r = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
            var m = MonitorFromRect(ref r, MONITOR_DEFAULTTONULL);
            return m != IntPtr.Zero && SameRect(MonitorRect(m), r);
        }

        public static bool IsOnMonitor(IntPtr hwnd, int left, int top, int right, int bottom)
        {
            UsePhysicalPixels();
            var r = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
            return SameRect(MonitorRect(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)), r);
        }

        // --- action -------------------------------------------------------------

        // Put a window back where it was saved: its restored rectangle, then its
        // maximized/minimized state. SW_*NOACTIVATE where one exists, so a sign-in
        // restore doesn't pull focus around while you start working.
        public static void Place(IntPtr hwnd, int showCmd, int flags, int left, int top, int right, int bottom)
        {
            UsePhysicalPixels();
            var wp = new WINDOWPLACEMENT
            {
                length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)),
                flags = flags,
                showCmd = showCmd == SW_SHOWMINIMIZED ? SW_SHOWMINNOACTIVE : SW_SHOWNOACTIVATE,
                rcNormalPosition = new RECT { Left = left, Top = top, Right = right, Bottom = bottom },
            };
            SetPlacement(hwnd, ref wp);

            // Maximizing is a second step on purpose. Asking an already-maximized window
            // to be "maximized with its restore rectangle on the other monitor" only
            // updates the restore rectangle - the window stays maximized where it is.
            // Restoring it onto the target rectangle first moves it; then maximize there.
            if (showCmd == SW_SHOWMAXIMIZED)
            {
                wp.showCmd = SW_SHOWMAXIMIZED;
                SetPlacement(hwnd, ref wp);
            }
        }

        static void SetPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp)
        {
            if (!SetWindowPlacement(hwnd, ref wp))
                throw new InvalidOperationException(string.Format(
                    "SetWindowPlacement(0x{0:X}, showCmd {1}) failed: Win32 error {2}", hwnd.ToInt64(), wp.showCmd, Marshal.GetLastWin32Error()));
        }

        // --- internals ------------------------------------------------------------

        static WindowInfo Describe(IntPtr h, List<Guid> order)
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return null;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return null;

            // Windows on other desktops are cloaked by the shell - keep those. Anything
            // cloaked by its own app (suspended Store apps, hidden frames) isn't "open".
            int cloaked;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0 && cloaked != DWM_CLOAKED_SHELL)
                return null;

            var title = new StringBuilder(512);
            GetWindowText(h, title, title.Capacity);
            if (title.Length == 0) return null;

            Guid desk;
            if (Vdm.GetWindowDesktopId(h, out desk) != 0 || desk == Guid.Empty) return null;

            uint pid;
            GetWindowThreadProcessId(h, out pid);
            var w = new WindowInfo { Hwnd = h, Title = title.ToString(), Desktop = order.IndexOf(desk) + 1 };
            if (!ReadProcess(pid, w)) return null;   // exited meanwhile, or a protected process

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(h, ref wp)) return null;   // closed between EnumWindows and now
            w.ShowCmd = wp.showCmd;
            w.Flags = wp.flags;
            w.Left = wp.rcNormalPosition.Left; w.Top = wp.rcNormalPosition.Top;
            w.Right = wp.rcNormalPosition.Right; w.Bottom = wp.rcNormalPosition.Bottom;

            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            GetMonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref mi);
            w.Monitor = mi.szDevice.Replace(@"\\.\", "");
            w.MonLeft = mi.rcMonitor.Left; w.MonTop = mi.rcMonitor.Top;
            w.MonRight = mi.rcMonitor.Right; w.MonBottom = mi.rcMonitor.Bottom;
            return w;
        }

        static bool ReadProcess(uint pid, WindowInfo w)
        {
            IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (p == IntPtr.Zero) return false;
            try
            {
                var path = new StringBuilder(1024);
                int size = path.Capacity;
                if (!QueryFullProcessImageName(p, 0, path, ref size)) return false;
                w.Exe = path.ToString();
                w.ProcessName = Path.GetFileNameWithoutExtension(w.Exe);

                var aumid = new StringBuilder(256);
                int len = aumid.Capacity;
                if (GetApplicationUserModelId(p, ref len, aumid) == 0) w.Aumid = aumid.ToString();

                // A normal process can't open an elevated process's token - that
                // refusal is itself the answer.
                IntPtr token;
                if (!OpenProcessToken(p, TOKEN_QUERY, out token)) { w.Elevated = true; return true; }
                try
                {
                    int elevated, ret;
                    w.Elevated = !GetTokenInformation(token, TokenElevation, out elevated, sizeof(int), out ret) || elevated != 0;
                }
                finally { CloseHandle(token); }
                return true;
            }
            finally { CloseHandle(p); }
        }

        // Physical pixels for every coordinate we read or write, so a rectangle saved
        // on one monitor means the same thing when restored, whatever each one's scaling.
        static void UsePhysicalPixels() { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }

        static RECT MonitorRect(IntPtr monitor)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            GetMonitorInfo(monitor, ref mi);
            return mi.rcMonitor;
        }

        static bool SameRect(RECT a, RECT b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }

        static IVirtualDesktopManager vdm;
        static IVirtualDesktopManager Vdm
        {
            get
            {
                if (vdm == null)
                    vdm = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")));
                return vdm;
            }
        }

        const int GW_OWNER = 4, GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        const int DWMWA_CLOAKED = 14, DWM_CLOAKED_SHELL = 2;
        const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x8;
        const int TokenElevation = 20;
        const int SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3, SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7;
        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr p, ref int length, StringBuilder id);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int size, out int returned);
    }

    public static class Session
    {
        // Shutdown ends processes from the highest level down. Apps default to 0x280;
        // 0x3FF is the highest an app may take. Ending the recorder first means it is
        // gone before any app starts closing, so it never records a half-closed desktop.
        public static void ExitBeforeOtherApps()
        {
            if (!SetProcessShutdownParameters(0x3FF, 0))
                throw new InvalidOperationException("SetProcessShutdownParameters(0x3FF) failed: Win32 error " + Marshal.GetLastWin32Error());
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessShutdownParameters(uint level, uint flags);
    }

    // The documented half: which desktop is a window on.
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
    }

    public sealed class VirtualDesktops : IDisposable
    {
        static readonly Guid CLSID_ImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        static readonly Guid SID_VirtualDesktopManagerInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
        static readonly Guid IID_IServiceProvider = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
        static readonly Guid IID_IApplicationViewCollection = new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
        static readonly Guid IID_IVirtualDesktop = new Guid("3F07F4BE-B107-441A-AF0F-39D82529072C");
        // IVirtualDesktopManagerInternal as published by Windows 11 24H2/25H2, then 23H2.
        // Both put the methods used here in the same slots (see the calls below).
        static readonly Guid[] IID_IVirtualDesktopManagerInternal =
        {
            new Guid("53F5CA0B-158F-4124-900C-057158060B27"),
            new Guid("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E"),
        };
        const int E_NOINTERFACE = unchecked((int)0x80004002);

        IntPtr shell, manager, views;
        readonly List<IntPtr> desktops = new List<IntPtr>();
        readonly List<Guid> ids = new List<Guid>();

        public int Count { get { return desktops.Count; } }

        public static VirtualDesktops Open()
        {
            var vd = new VirtualDesktops();
            try { vd.Connect(); return vd; }
            catch { vd.Dispose(); throw; }
        }

        // Move a window to desktop N (1-based, as numbered in Win+Tab), then confirm
        // with the documented API that it really landed there.
        public void MoveWindow(IntPtr hwnd, int desktop)
        {
            if (desktop < 1 || desktop > desktops.Count)
                throw new ArgumentOutOfRangeException("desktop", desktop, "only " + desktops.Count + " desktops exist");
            IntPtr view;
            Check(Slot<GetViewForHwndFn>(views, 3)(views, hwnd, out view), "IApplicationViewCollection.GetViewForHwnd", hwnd);
            try { Check(Slot<MoveViewToDesktopFn>(manager, 1)(manager, view, desktops[desktop - 1]), "MoveViewToDesktop", hwnd); }
            finally { Marshal.Release(view); }

            var vdm = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")));
            Guid now;
            Check(vdm.GetWindowDesktopId(hwnd, out now), "GetWindowDesktopId", hwnd);
            if (now != ids[desktop - 1])
                throw new InvalidOperationException(string.Format(
                    "window 0x{0:X}: shell accepted the move to desktop {1} but the window is on {2}", hwnd.ToInt64(), desktop, now));
        }

        void Connect()
        {
            Guid clsid = CLSID_ImmersiveShell, iidSp = IID_IServiceProvider;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iidSp, out shell),
                  "CoCreateInstance(ImmersiveShell) - Explorer not ready yet?", IntPtr.Zero);

            foreach (var candidate in IID_IVirtualDesktopManagerInternal)
            {
                Guid sid = SID_VirtualDesktopManagerInternal, iid = candidate;
                int hr = Slot<QueryServiceFn>(shell, 0)(shell, ref sid, ref iid, out manager);
                if (hr == 0) break;
                manager = IntPtr.Zero;
                if (hr != E_NOINTERFACE) Check(hr, "QueryService(VirtualDesktopManagerInternal)", IntPtr.Zero);
            }
            if (manager == IntPtr.Zero)
                throw new NotSupportedException("this Windows build doesn't accept any IVirtualDesktopManagerInternal ID in WindowLayout.cs - it needs the IDs for this release");

            Guid vsid = IID_IApplicationViewCollection, viid = IID_IApplicationViewCollection;
            int hrViews = Slot<QueryServiceFn>(shell, 0)(shell, ref vsid, ref viid, out views);
            if (hrViews == E_NOINTERFACE) throw new NotSupportedException("this Windows build doesn't accept the IApplicationViewCollection ID in WindowLayout.cs");
            Check(hrViews, "QueryService(ApplicationViewCollection)", IntPtr.Zero);

            // Prove the slots mean what we think before anything calls MoveViewToDesktop:
            // slot 0 must count the desktops and slot 4 must list them in registry order.
            int count;
            Check(Slot<GetCountFn>(manager, 0)(manager, out count), "IVirtualDesktopManagerInternal.GetCount", IntPtr.Zero);
            var registry = DesktopOrder.Read();
            IntPtr array;
            Check(Slot<GetDesktopsFn>(manager, 4)(manager, out array), "IVirtualDesktopManagerInternal.GetDesktops", IntPtr.Zero);
            try
            {
                uint n;
                Check(Slot<ArrayGetCountFn>(array, 0)(array, out n), "IObjectArray.GetCount", IntPtr.Zero);
                if (n != count || n != registry.Count)
                    throw new InvalidOperationException(string.Format(
                        "desktop count disagrees: GetCount={0}, GetDesktops={1}, registry={2}", count, n, registry.Count));
                for (uint i = 0; i < n; i++)
                {
                    Guid iid = IID_IVirtualDesktop;
                    IntPtr d;
                    int hr = Slot<ArrayGetAtFn>(array, 1)(array, i, ref iid, out d);
                    if (hr == E_NOINTERFACE) throw new NotSupportedException("this Windows build doesn't accept the IVirtualDesktop ID in WindowLayout.cs");
                    Check(hr, "IObjectArray.GetAt", IntPtr.Zero);
                    desktops.Add(d);
                    Guid id;
                    Check(Slot<GetIdFn>(d, 1)(d, out id), "IVirtualDesktop.GetId", IntPtr.Zero);
                    if (id != registry[(int)i])
                        throw new InvalidOperationException(string.Format(
                            "desktop {0}: shell says {1}, registry says {2}", i + 1, id, registry[(int)i]));
                    ids.Add(id);
                }
            }
            finally { Marshal.Release(array); }
        }

        public void Dispose()
        {
            foreach (var d in desktops) Marshal.Release(d);
            desktops.Clear();
            if (views != IntPtr.Zero) { Marshal.Release(views); views = IntPtr.Zero; }
            if (manager != IntPtr.Zero) { Marshal.Release(manager); manager = IntPtr.Zero; }
            if (shell != IntPtr.Zero) { Marshal.Release(shell); shell = IntPtr.Zero; }
        }

        static void Check(int hr, string what, IntPtr hwnd)
        {
            if (hr == 0) return;
            throw new InvalidOperationException(hwnd == IntPtr.Zero
                ? string.Format("{0} failed: HRESULT 0x{1:X8}", what, hr)
                : string.Format("{0} failed for window 0x{1:X}: HRESULT 0x{2:X8}", what, hwnd.ToInt64(), hr));
        }

        // Method `index` of a COM interface, counting from the first method after
        // IUnknown's three. Called through the vtable so each call names its slot.
        static T Slot<T>(IntPtr comObject, int index) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, (3 + index) * IntPtr.Size));
        }

        delegate int QueryServiceFn(IntPtr self, ref Guid service, ref Guid riid, out IntPtr obj);   // IServiceProvider 0
        delegate int GetCountFn(IntPtr self, out int count);                                        // manager 0
        delegate int MoveViewToDesktopFn(IntPtr self, IntPtr view, IntPtr desktop);                 // manager 1
        delegate int GetDesktopsFn(IntPtr self, out IntPtr objectArray);                            // manager 4
        delegate int ArrayGetCountFn(IntPtr self, out uint count);                                  // IObjectArray 0
        delegate int ArrayGetAtFn(IntPtr self, uint index, ref Guid riid, out IntPtr obj);          // IObjectArray 1
        delegate int GetIdFn(IntPtr self, out Guid id);                                             // IVirtualDesktop 1
        delegate int GetViewForHwndFn(IntPtr self, IntPtr hwnd, out IntPtr view);                   // IApplicationViewCollection 3

        const uint CLSCTX_LOCAL_SERVER = 4;
        [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr obj);
    }
}
