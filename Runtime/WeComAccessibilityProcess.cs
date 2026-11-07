using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace WeChatPet.Runtime {
    internal sealed class WeComAccessibilityProcess : IDisposable {
        readonly Process process;
        WeComAccessibilityProcess(Process process) { this.process=process; }
        internal static WeComAccessibilityProcess Start(int pid) {
            string helper=Path.Combine(Path.GetDirectoryName(typeof(WeComAccessibilityProcess).Assembly.Location),"WeComAccessibility.exe");
            if(!File.Exists(helper))throw new IOException("wecom_accessibility_helper_missing");
            var start=new ProcessStartInfo {
                FileName=helper,Arguments=pid.ToString()+" 1",UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
            };
            var process=new Process {StartInfo=start};
            try {
                process.Start();
                var ready=process.StandardOutput.ReadLineAsync();
                if(!ready.Wait(5000)||ready.Result!="ready")throw new IOException("wecom_accessibility_unavailable");
                // DuiLib publishes its accessible subtree on the next UI update.
                Thread.Sleep(450);
                return new WeComAccessibilityProcess(process);
            } catch {try{process.Kill();}catch{}process.Dispose();throw;}
        }
        public void Dispose() {
            try {process.StandardInput.Close();}catch{}
            try {if(!process.WaitForExit(5000))process.Kill();}catch{}
            process.Dispose();
        }
    }
}
