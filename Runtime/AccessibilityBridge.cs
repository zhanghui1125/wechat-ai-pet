using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WeChatPet.Runtime {
    // WeChat exposes its UIA tree only when a known accessibility bridge is
    // present. The bundled marker is intentionally inert; loading it does not
    // read, modify, or execute code in WeChat.
    internal static class AccessibilityBridge {
        const uint CreateThread = 0x0002;
        const uint VirtualMemoryOperation = 0x0008;
        const uint VirtualMemoryRead = 0x0010;
        const uint VirtualMemoryWrite = 0x0020;
        const uint QueryInformation = 0x0400;
        const uint CommitReserve = 0x3000;
        const uint PageReadWrite = 0x04;
        const uint WaitMilliseconds = 2500;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protection);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] data, UIntPtr size, out UIntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeThread(IntPtr thread, out UIntPtr exitCode);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        internal static bool Ensure(int pid) {
            string ignored;
            return Ensure(pid, out ignored);
        }

        internal static bool Ensure(int pid, out string reason) {
            reason = null;
            string bridge = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nvdaHelperRemote.dll");
            try {
                if(pid <= 0 || !File.Exists(bridge)) {
                    reason = "accessibility_bridge_missing";
                    return false;
                }
                using(var process = Process.GetProcessById(pid)) {
                    if(process.HasExited) {
                        reason = "target_not_running";
                        return false;
                    }
                    if(process.Modules.Cast<ProcessModule>().Any(m => String.Equals(m.ModuleName, "nvdaHelperRemote.dll", StringComparison.OrdinalIgnoreCase))) {
                        return true;
                    }
                    IntPtr localKernel = GetModuleHandle("kernel32.dll");
                    IntPtr localLoadLibrary = GetProcAddress(localKernel, "LoadLibraryW");
                    ProcessModule kernel = process.Modules.Cast<ProcessModule>().FirstOrDefault(m => String.Equals(m.ModuleName, "kernel32.dll", StringComparison.OrdinalIgnoreCase));
                    if(localKernel == IntPtr.Zero || localLoadLibrary == IntPtr.Zero || kernel == null) {
                        reason = "bridge_loader_unavailable";
                        return false;
                    }
                    IntPtr remoteLoadLibrary = new IntPtr(kernel.BaseAddress.ToInt64() + (localLoadLibrary.ToInt64() - localKernel.ToInt64()));
                    byte[] payload = System.Text.Encoding.Unicode.GetBytes(bridge + "\0");
                    IntPtr handle = OpenProcess(CreateThread | VirtualMemoryOperation | VirtualMemoryRead | VirtualMemoryWrite | QueryInformation, false, pid);
                    if(handle == IntPtr.Zero) {
                        reason = "bridge_access_denied_" + Marshal.GetLastWin32Error();
                        return false;
                    }
                    IntPtr remote = IntPtr.Zero;
                    IntPtr thread = IntPtr.Zero;
                    try {
                        remote = VirtualAllocEx(handle, IntPtr.Zero, new UIntPtr((uint)payload.Length), CommitReserve, PageReadWrite);
                        if(remote == IntPtr.Zero) {
                            reason = "bridge_allocate_failed_" + Marshal.GetLastWin32Error();
                            return false;
                        }
                        UIntPtr written;
                        if(!WriteProcessMemory(handle, remote, payload, new UIntPtr((uint)payload.Length), out written) || written.ToUInt64() != (ulong)payload.Length) {
                            reason = "bridge_write_failed_" + Marshal.GetLastWin32Error();
                            return false;
                        }
                        uint threadId;
                        thread = CreateRemoteThread(handle, IntPtr.Zero, UIntPtr.Zero, remoteLoadLibrary, remote, 0, out threadId);
                        if(thread == IntPtr.Zero) {
                            reason = "bridge_thread_failed_" + Marshal.GetLastWin32Error();
                            return false;
                        }
                        if(WaitForSingleObject(thread, WaitMilliseconds) != 0) {
                            reason = "bridge_timeout";
                            return false;
                        }
                        UIntPtr exitCode;
                        if(!GetExitCodeThread(thread, out exitCode) || exitCode == UIntPtr.Zero) {
                            reason = "bridge_load_failed";
                            return false;
                        }
                        return true;
                    } finally {
                        if(thread != IntPtr.Zero) CloseHandle(thread);
                        if(remote != IntPtr.Zero) VirtualFreeEx(handle, remote, UIntPtr.Zero, 0x8000);
                        CloseHandle(handle);
                    }
                }
            } catch(Exception error) {
                reason = "bridge_" + error.GetType().Name;
                return false;
            }
        }
    }
}
