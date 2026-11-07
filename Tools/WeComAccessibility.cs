using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WeChatPet.Tools {
    // WeCom's DuiLib accessibility bridge is disabled by default. This helper
    // must be x86 because the desktop WeCom process is x86. Keeping the helper
    // alive lets it switch the bridge off again when the observer closes.
    public static class WeComAccessibilityProgram {
        const uint ProcessCreateThread = 0x0002;
        const uint ProcessQueryInformation = 0x0400;
        const uint ProcessVmOperation = 0x0008;
        const uint ProcessVmWrite = 0x0020;
        const uint ProcessVmRead = 0x0010;
        const string AuthorizedName="?SetAtWhitelistAuthorized@CAccessibilityNotifier@DuiLib@@SAX_N@Z";
        const string FeatureName="?SetFeatureEnabledByBusiness@CAccessibilityNotifier@DuiLib@@SAX_N@Z";
        const string EnabledName="?SetEnabled@CAccessibilityNotifier@DuiLib@@SAX_N@Z";
        const string IsFeatureName="?IsFeatureEnabled@CAccessibilityNotifier@DuiLib@@SA_NXZ";
        const string IsEnabledName="?IsEnabled@CAccessibilityNotifier@DuiLib@@SA_NXZ";

        [DllImport("kernel32.dll", SetLastError=true)]
        static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern IntPtr CreateRemoteThread(IntPtr process,IntPtr attributes,UIntPtr stackSize,IntPtr start,IntPtr parameter,uint flags,out uint threadId);
        [DllImport("kernel32.dll")]
        static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
        [DllImport("kernel32.dll")]
        static extern bool GetExitCodeThread(IntPtr thread,out uint exitCode);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi, SetLastError=true)]
        static extern IntPtr GetProcAddress(IntPtr module,string name);

        static int Call(IntPtr process,long remote,int argument) {
            uint threadId;
            IntPtr thread=CreateRemoteThread(process,IntPtr.Zero,UIntPtr.Zero,new IntPtr(remote),new IntPtr(argument),0,out threadId);
            if(thread==IntPtr.Zero)throw new IOException("remote_thread_failed");
            try {
                if(WaitForSingleObject(thread,5000)!=0)throw new IOException("remote_call_timeout");
                uint result;
                if(!GetExitCodeThread(thread,out result))throw new IOException("remote_result_unavailable");
                return unchecked((int)result);
            } finally {CloseHandle(thread);}
        }

        static long Export(long baseAddress,IntPtr localModule,string name) {
            IntPtr local=GetProcAddress(localModule,name);
            if(local==IntPtr.Zero)throw new InvalidOperationException("duilib_export_missing");
            return baseAddress+(local.ToInt64()-localModule.ToInt64());
        }

        public static int Main(string[] args) {
            if(args.Length<1)return 2;
            int pid;
            if(!Int32.TryParse(args[0],out pid)||pid<=0)return 2;
            bool hold=args.Length>1&&args[1]!="0";
            Process target=null;
            IntPtr process=IntPtr.Zero,localModule=IntPtr.Zero;
            long authorized=0,feature=0,enabled=0,isFeature=0,isEnabled=0;
            bool owned=false;
            try {
                target=Process.GetProcessById(pid);
                if(!target.ProcessName.Equals("WXWork",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("target_process_changed");
                ProcessModule dui=null;
                foreach(ProcessModule module in target.Modules) {
                    if(!module.ModuleName.Equals("DuiLib.dll",StringComparison.OrdinalIgnoreCase))continue;
                    if(dui!=null)throw new InvalidOperationException("duilib_ambiguous");
                    dui=module;
                }
                if(dui==null)throw new InvalidOperationException("duilib_missing");
                localModule=LoadLibraryEx(dui.FileName,IntPtr.Zero,0x8);
                if(localModule==IntPtr.Zero)throw new IOException("duilib_load_failed_"+Marshal.GetLastWin32Error());
                long remoteBase=dui.BaseAddress.ToInt64();
                authorized=Export(remoteBase,localModule,AuthorizedName);
                feature=Export(remoteBase,localModule,FeatureName);
                enabled=Export(remoteBase,localModule,EnabledName);
                isFeature=Export(remoteBase,localModule,IsFeatureName);
                isEnabled=Export(remoteBase,localModule,IsEnabledName);

                process=OpenProcess(ProcessCreateThread|ProcessQueryInformation|ProcessVmOperation|ProcessVmWrite|ProcessVmRead,false,pid);
                if(process==IntPtr.Zero)throw new IOException("process_open_failed");

                bool already=Call(process,isFeature,0)!=0&&Call(process,isEnabled,0)!=0;
                if(hold) {
                    if(already) {
                        Call(process,enabled,0);
                        Call(process,feature,0);
                        Call(process,authorized,0);
                        System.Threading.Thread.Sleep(120);
                    }
                    owned=true;
                    Call(process,authorized,1);
                    Call(process,feature,1);
                    Call(process,enabled,1);
                    if(Call(process,isFeature,0)==0||Call(process,isEnabled,0)==0)throw new InvalidOperationException("accessibility_enable_failed");
                } else if(!hold&&already) {
                    Call(process,enabled,0);
                    Call(process,feature,0);
                    Call(process,authorized,0);
                }
                Console.WriteLine("ready");Console.Out.Flush();
                if(hold)while(Console.ReadLine()!=null) { }
                return 0;
            } catch(Exception error) {
                Console.WriteLine(error.GetType().Name+":"+error.Message);
                Console.Out.Flush();
                return 1;
            } finally {
                if(owned&&process!=IntPtr.Zero) {
                    try {
                        Call(process,enabled,0);
                        Call(process,feature,0);
                        Call(process,authorized,0);
                    } catch { }
                }
                if(process!=IntPtr.Zero)CloseHandle(process);
                if(target!=null)target.Dispose();
            }
        }

    }
}
