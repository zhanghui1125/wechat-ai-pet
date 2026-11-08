using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Automation;

namespace WeChatPet.Runtime {
    public static class Win {
        [StructLayout(LayoutKind.Sequential)] public struct ScreenRect {public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out ScreenRect rect);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        public static bool PhysicalBounds(IntPtr hwnd,out ScreenRect rect) {
            IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
            try{return GetWindowRect(hwnd,out rect);}finally{if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
        }
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from,uint to,bool attach);
        [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h,bool altTab);
        delegate bool EnumWindowCallback(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback,IntPtr state);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int length);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder name,int length);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
        [DllImport("user32.dll")] static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint t);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int k);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a,bool i,int p);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll",SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr r);
        [DllImport("kernel32.dll",SetLastError=true)] public static extern bool WriteProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr r);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        public static void SameUser(Process p) {
            if(p.SessionId != Process.GetCurrentProcess().SessionId) throw new InvalidOperationException("wrong_windows_session");
            IntPtr token;
            if(!OpenProcessToken(p.Handle,8,out token)) throw new InvalidOperationException("user_check_failed");
            try { using(var identity = new WindowsIdentity(token))
                if(identity.User != WindowsIdentity.GetCurrent().User) throw new InvalidOperationException("wrong_windows_user"); }
            finally { CloseHandle(token); }
        }
        public static byte[] Read(IntPtr p,long address,int length) {
            byte[] b = new byte[length]; UIntPtr read;
            if(!ReadProcessMemory(p,new IntPtr(address),b,new UIntPtr((uint)length),out read)||read.ToUInt64()!=(ulong)length) throw new IOException("native_read_failed"); return b;
        }
        public static bool Click(IntPtr window,int x,int y) {
            IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
            try {
                if(window!=IntPtr.Zero&&GetForegroundWindow()!=window)return false;
                if(!SetCursorPos(x,y))return false;
                mouse_event(0x0002,0,0,0,UIntPtr.Zero);
                System.Threading.Thread.Sleep(60);
                mouse_event(0x0004,0,0,0,UIntPtr.Zero);
                return true;
            } finally {if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
        }
        public static void CtrlV() {
            keybd_event(0x11,0,0,UIntPtr.Zero);
            System.Threading.Thread.Sleep(45);
            keybd_event(0x56,0,0,UIntPtr.Zero);
            System.Threading.Thread.Sleep(65);
            keybd_event(0x56,0,2,UIntPtr.Zero);
            System.Threading.Thread.Sleep(45);
            keybd_event(0x11,0,2,UIntPtr.Zero);
        }
        public static bool ActivateWindow(IntPtr window) {
            if(window==IntPtr.Zero)return false;
            if(IsIconic(window))ShowWindow(window,9);
            IntPtr foreground=GetForegroundWindow();
            uint ignored;
            uint foregroundThread=foreground==IntPtr.Zero?0:GetWindowThreadProcessId(foreground,out ignored);
            uint targetThread=GetWindowThreadProcessId(window,out ignored);
            bool attached=foregroundThread!=0&&targetThread!=0&&foregroundThread!=targetThread&&AttachThreadInput(foregroundThread,targetThread,true);
            try {BringWindowToTop(window);SetForegroundWindow(window);}
            finally {if(attached)AttachThreadInput(foregroundThread,targetThread,false);}
            if(GetForegroundWindow()!=window) {
                SwitchToThisWindow(window,true);
                System.Threading.Thread.Sleep(80);
            }
            if(GetForegroundWindow()!=window) {
                keybd_event(0x12,0,0,UIntPtr.Zero);
                SetForegroundWindow(window);
                keybd_event(0x12,0,2,UIntPtr.Zero);
                System.Threading.Thread.Sleep(80);
            }
            return GetForegroundWindow()==window;
        }
        public static IntPtr FindVisibleWindow(int pid,string className,int minWidth,int minHeight) {
            IntPtr found=IntPtr.Zero;
            EnumWindows(delegate(IntPtr window,IntPtr state) {
                uint owner;
                if(GetWindowThreadProcessId(window,out owner)==0||owner!=(uint)pid||!IsWindowVisible(window))return true;
                var name=new StringBuilder(256);GetClassName(window,name,name.Capacity);
                if(!String.Equals(name.ToString(),className,StringComparison.Ordinal))return true;
                ScreenRect bounds;
                if(GetWindowRect(window,out bounds)&&bounds.Right-bounds.Left>=minWidth&&bounds.Bottom-bounds.Top>=minHeight) {
                    found=window;return false;
                }
                return true;
            },IntPtr.Zero);
            return found;
        }
        public static string WindowTitle(IntPtr window) {
            var title=new StringBuilder(512);
            return window==IntPtr.Zero||GetWindowText(window,title,title.Capacity)<=0?"":title.ToString();
        }
        public static bool IsWeixinChatWindow(IntPtr window) {
            if(window==IntPtr.Zero||!IsWindowVisible(window)||IsIconic(window))return false;
            ScreenRect bounds;
            if(!PhysicalBounds(window,out bounds)||bounds.Right-bounds.Left<500||bounds.Bottom-bounds.Top<400)return false;
            try {
                AutomationElement root=AutomationElement.FromHandle(window);
                TreeWalker walker=TreeWalker.RawViewWalker;
                var queue=new Queue<AutomationElement>();queue.Enqueue(root);
                bool input=false,list=false;int visited=0;
                while(queue.Count>0&&visited++<1400) {
                    AutomationElement current=queue.Dequeue();
                    try {
                        string id=current.Current.AutomationId;int dot=id.LastIndexOf('.');
                        if(dot>=0)id=id.Substring(dot+1);
                        if(id=="chat_input_field"&&current.Current.ClassName=="mmui::ChatInputField")input=true;
                        if(id=="chat_message_list"&&current.Current.ClassName=="mmui::RecyclerListView")list=true;
                        if(input&&list)return true;
                        for(var child=walker.GetFirstChild(current);child!=null;child=walker.GetNextSibling(child))queue.Enqueue(child);
                    }catch {}
                }
            }catch {}
            return false;
        }
        // 只按窗口外观筛：可见、未最小化、微信 Qt 窗口、尺寸像聊天窗口。
        // 微信把聊天开在独立窗口时，主窗口往往还是最小化状态，这里不依赖 UIA 探测。
        public static List<IntPtr> FindVisibleWeixinWindows(int pid) {
            var result=new List<IntPtr>();
            EnumWindows(delegate(IntPtr window,IntPtr state) {
                uint owner;
                if(GetWindowThreadProcessId(window,out owner)==0||owner!=(uint)pid||!IsWindowVisible(window)||IsIconic(window))return true;
                var name=new StringBuilder(256);GetClassName(window,name,name.Capacity);
                string className=name.ToString();
                if(className.IndexOf("QWindowIcon",StringComparison.Ordinal)<0&&
                   className!="mmui::MainWindow"&&className!="mmui::LoginWindow"&&className!="mmui::ChatSingleWindow")return true;
                ScreenRect bounds;
                if(!PhysicalBounds(window,out bounds)||bounds.Right-bounds.Left<500||bounds.Bottom-bounds.Top<400)return true;
                result.Add(window);
                return result.Count<4;
            },IntPtr.Zero);
            return result;
        }
        public static List<IntPtr> FindWeixinChatWindows(int pid) {
            var result=new List<IntPtr>();
            AccessibilityBridge.Ensure(pid);
            EnumWindows(delegate(IntPtr window,IntPtr state) {
                uint owner;
                if(GetWindowThreadProcessId(window,out owner)==0||owner!=(uint)pid||!IsWindowVisible(window)||IsIconic(window))return true;
                if(IsWeixinChatWindow(window))result.Add(window);
                return result.Count<12;
            },IntPtr.Zero);
            return result;
        }
    }
    internal static class TargetIdentity {
        public static bool IsCurrent(int pid,long birth,long hwnd) {
            if(pid<=0||birth<=0||hwnd==0)return false;
            try {
                using(var p=Process.GetProcessById(pid)) {
                    if(p.HasExited||p.StartTime.ToUniversalTime().Ticks!=birth)return false;
                    uint owner;
                    return Win.GetWindowThreadProcessId(new IntPtr(hwnd),out owner)!=0&&owner==pid;
                }
            }catch{return false;}
        }
    }
    public static class BuildIdentity {
        public const string Hash = "e0428a7b691853de496532523c2c09aa095f90941c37c9f4cfb3f137f3c66556";
        public const long FileLength = 204305968;
        static FileStream held; static string verifiedPath;
        static readonly object sync = new object();
        public static void VerifyFile(string path) {
            lock(sync) {
                if(held!=null && String.Equals(path,verifiedPath,StringComparison.OrdinalIgnoreCase)) return;
                if(held!=null) { held.Dispose(); held=null; }
                var file = File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                try {
                    if(file.Length!=FileLength) throw new InvalidOperationException("unsupported_wechat_build");
                    using(var sha=SHA256.Create()) if(BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant()!=Hash) throw new InvalidOperationException("unsupported_wechat_build");
                    held=file; verifiedPath=path;
                } catch { file.Dispose(); throw; }
            }
        }
        public static ProcessModule Bind(int pid, long birth, long hwnd) {
            var p=Process.GetProcessById(pid); Win.SameUser(p);
            uint owner; Win.GetWindowThreadProcessId(new IntPtr(hwnd),out owner);
            if(p.StartTime.ToUniversalTime().Ticks!=birth || p.ProcessName!="Weixin" || owner!=pid) throw new InvalidOperationException("target_identity_changed");
            var matches=p.Modules.Cast<ProcessModule>().Where(m=>m.ModuleName.Equals("Weixin.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length!=1) throw new InvalidOperationException("wechat_module_missing");
            VerifyFile(matches[0].FileName);
            NativeMessageReader.Configure(p.MainModule.FileName,matches[0].FileName);
            QtCompositionGate.Configure(p.MainModule.FileName,matches[0].FileName);
            return matches[0];
        }
    }
    // Separate process: stdin EOF or missed heartbeats restore only the byte this lease owns.
    public static class AccessibilityGuardian {
        public static int Run(int pid,long birth,long hwnd) {
            IntPtr handle=IntPtr.Zero; bool owned=false; long address=0;
            try {
                var module=BuildIdentity.Bind(pid,birth,hwnd);
                handle=Win.OpenProcess(0x438,false,pid);
                if(handle==IntPtr.Zero) throw new IOException("lease_access_denied");
                if(BitConverter.ToString(Win.Read(handle,module.BaseAddress.ToInt64()+0x864afb,13)).Replace("-","").ToLowerInvariant()!="803dd67eb30a000f84ea060000") throw new InvalidOperationException("lease_signature_mismatch");
                address=module.BaseAddress.ToInt64()+0xb39c9d8;
                byte original=Win.Read(handle,address,1)[0];
                if(original>1) throw new InvalidOperationException("lease_state_unknown");
                if(original==0) { owned=true; Write(handle,address,1); }
                Console.WriteLine("ready"); Console.Out.Flush();
                while(true) {
                    if(!GuardianHeartbeat.WaitForNext(Console.In)) break;
                    if(Win.Read(handle,address,1)[0]!=1) break;
                }
                return 0;
            } catch(Exception error) { Console.WriteLine(ObserverFailureGate.GuardianFailure(error)); return 1; }
            finally {
                if(handle!=IntPtr.Zero) {
                    try { if(owned && Win.Read(handle,address,1)[0]==1) { Write(handle,address,0); Console.WriteLine("restored"); } }
                    catch { Console.WriteLine("restore_unconfirmed"); }
                    Win.CloseHandle(handle);
                }
            }
        }
        static void Write(IntPtr p,long a,byte value) {
            UIntPtr n;
            if(!Win.WriteProcessMemory(p,new IntPtr(a),new[]{value},new UIntPtr(1),out n)||n.ToUInt64()!=1||Win.Read(p,a,1)[0]!=value) throw new IOException("lease_write_failed");
        }
    }
}
