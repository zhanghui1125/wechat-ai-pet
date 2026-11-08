using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace WeChatPet.Runtime {
    public static class ObserverProgram {
        static readonly object sync=new object(), outputLock=new object();
        static readonly JavaScriptSerializer json=new JavaScriptSerializer { MaxJsonLength=20*1024*1024 };
        static ChatSnapshot current; static Recommendation ready, fill;
        static long activity, pointerEpoch, fillPointerEpoch, cancellation, fillCancellation; static volatile bool stopping,filling,weComPasteKeys; static bool swallowed,tabDown;
        static string fillSource="physical_tab";static bool fillActivate;
        static IntPtr target,hook,mouseHook; static Hook callback=Keyboard, mouseCallback=Mouse;
        static IChatReader reader;static WeComAccessibilityProcess weComAccessibility;static string platform="weixin";
        delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
        [StructLayout(LayoutKind.Sequential)]struct Key { public uint Code,Scan,Flags,Time; public UIntPtr Extra; }
        [DllImport("user32.dll")]static extern IntPtr SetWindowsHookEx(int id,Hook cb,IntPtr mod,uint thread);
        [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr h,int code,IntPtr w,IntPtr l);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string name);
        static void Emit(object value) { lock(outputLock) { Console.WriteLine(new JavaScriptSerializer { MaxJsonLength=20*1024*1024 }.Serialize(value));Console.Out.Flush(); } }
        [STAThread] public static int Main(string[] args) {
            Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
            // A bounded, read-only support check. No hooks, lease changes, model
            // requests, draft writes or chat text are exposed by this mode.
            if((args.Length==4||args.Length==5)&&args[0]=="--inspect")return Inspect(Int32.Parse(args[1]),Int64.Parse(args[2]),Int64.Parse(args[3]),args.Length==5?args[4]:null,null);
            if((args.Length==4||args.Length==5)&&args[0]=="--inspect-wecom")return Inspect(Int32.Parse(args[1]),Int64.Parse(args[2]),Int64.Parse(args[3]),args.Length==5?args[4]:null,"wecom");
            if(args.Length==4&&args[0]=="--inspect-paste-windows") {
                try {BuildIdentity.Bind(Int32.Parse(args[1]),Int64.Parse(args[2]),Int64.Parse(args[3]));Emit(NativeEmojiWindowDiagnostics.Inspect(Int64.Parse(args[3])));return 0;}
                catch(Exception e){Emit(new{type="paste_window_inspection_failed",code=e.GetType().Name});return 1;}
            }
            if(args.Length==4&&args[0]=="--guardian")return AccessibilityGuardian.Run(Int32.Parse(args[1]),Int64.Parse(args[2]),Int64.Parse(args[3]));
            if(args.Length!=3&&args.Length!=4)return 2;
            try {
                int pid=Int32.Parse(args[0]);long birth=Int64.Parse(args[1]),hwnd=Int64.Parse(args[2]);target=new IntPtr(hwnd);
                platform=args.Length==4?args[3]:"weixin";
                if(platform=="wecom") {
                    if(!TargetIdentity.IsCurrent(pid,birth,hwnd))throw new InvalidOperationException("target_identity_changed");
                    weComAccessibility=WeComAccessibilityProcess.Start(pid);
                } else {
                    if(!TargetIdentity.IsCurrent(pid,birth,hwnd))throw new InvalidOperationException("target_identity_changed");
                    AccessibilityBridge.Ensure(pid);
                    try{BuildIdentity.Bind(pid,birth,hwnd);}catch{}
                    byte[] key=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(key);NativeMessageReader.Initialize(key);
                }
                try {NativeMessageReader.MemoryIds=new MemoryIdentity(LocalStore.Data);}catch {NativeMessageReader.MemoryIds=null;}
                System.Threading.Timer heartbeat=null;
                // The UIA adapter does not lease or modify any WeChat memory. The
                // legacy guardian belongs to the retired version-specific reader.
                reader=platform=="wecom"?(IChatReader)new WeComReader(pid,birth,hwnd):new WeChatReader(pid,birth,hwnd);
                hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);mouseHook=SetWindowsHookEx(14,mouseCallback,GetModuleHandle(null),0);
                if(hook==IntPtr.Zero||mouseHook==IntPtr.Zero)throw new IOException("keyboard_hook_unavailable");
                var loop=new Thread(ReadLoop) { IsBackground=true };loop.SetApartmentState(ApartmentState.MTA);loop.Start();
                new Thread(ReadCommands) { IsBackground=true }.Start();
                var timer=new System.Windows.Forms.Timer { Interval=200 };
                timer.Tick+=delegate { if(stopping&&NativeEmojiPasteShortcut.TryStop())Application.ExitThread(); };timer.Start();
                Application.Run();timer.Dispose();if(heartbeat!=null)heartbeat.Dispose();return 0;
            } catch(Exception e) { Emit(new { type="failure",code=e is InvalidOperationException?e.Message:"observer_start_failed" });return 1; }
            finally {
                stopping=true;lock(sync) { ready=fill=null; }
                if(hook!=IntPtr.Zero)UnhookWindowsHookEx(hook);if(mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(mouseHook);
                try {Emit(new{type="clipboard_end",status=NativeEmojiClipboard.CancelActive()});}catch { }
                if(weComAccessibility!=null){try{weComAccessibility.Dispose();}catch{}weComAccessibility=null;}
            }
        }
        static void InspectResult(object value,string output) {
            if(output==null)Emit(value);else LocalStore.Write(Path.GetFullPath(output),new JavaScriptSerializer().Serialize(value),false);
        }
        static int Inspect(int pid,long birth,long hwnd,string output,string requestedPlatform) {
            WeComAccessibilityProcess accessibility=null;
            try {
                if(requestedPlatform=="wecom") {
                    if(!TargetIdentity.IsCurrent(pid,birth,hwnd))throw new InvalidOperationException("target_identity_changed");
                    accessibility=WeComAccessibilityProcess.Start(pid);
                } else {
                    if(!TargetIdentity.IsCurrent(pid,birth,hwnd))throw new InvalidOperationException("target_identity_changed");
                    AccessibilityBridge.Ensure(pid);
                    try{BuildIdentity.Bind(pid,birth,hwnd);}catch{}
                    byte[] key=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(key);NativeMessageReader.Initialize(key);
                }
                NativeMessageReader.MemoryIds=new MemoryIdentity(LocalStore.Data);
                IChatReader check=requestedPlatform=="wecom"?(IChatReader)new WeComReader(pid,birth,hwnd):new WeChatReader(pid,birth,hwnd);
                var s=check.Read(0);WeChatReader nativeReader=check as WeChatReader;var f=nativeReader==null?null:nativeReader.NativeFrame;
                var latestMessages=s.Messages;var latestImage=latestMessages==null?null:latestMessages.LastOrDefault()==null?null:latestMessages.LastOrDefault().Image;
                InspectResult(new {type="inspection",s.Available,s.Reason,windowVisible=Win.IsWindowVisible(new IntPtr(hwnd)),windowMinimized=Win.IsIconic(new IntPtr(hwnd)),s.GroupChat,s.AtEnd,s.DraftEmpty,s.DraftConsistent,s.Focused,s.Foreground,s.Composing,s.ImeAllowed,uiDiagnostics=check.UiDiagnostics,
                    platform=requestedPlatform??"weixin",contact=s.Contact,messages=latestMessages==null?null:latestMessages.Select(m=>new{m.Key,m.Role,m.Type,m.Text,m.CreatedTicks}).ToArray(),
                    imageCount=latestMessages==null?0:latestMessages.Count(m=>m!=null&&m.Image!=null),latestImageMime=latestImage==null?null:latestImage.Mime,latestImageDataLength=latestImage==null?0:latestImage.Data.Length,
                    accountIdentified=MemoryIdentity.Valid(s.AccountId),groupIdentified=MemoryIdentity.Valid(s.GroupId),friendIdentified=MemoryIdentity.Valid(s.FriendId),native=f==null?null:new {f.Valid,f.Reason,f.Count,distinctMembers=f.Model.Select(r=>r.SenderTag).Distinct().Count(),distinctAccounts=f.Model.Select(r=>r.AccountId).Distinct().Count(),rows=f.Model.Select(r=>new {r.Type,r.ModelIndex,r.Peer,r.DirectionKnown,r.GroupChat,r.GroupSenderKnown,r.SenderEvidence,accountIdValid=MemoryIdentity.Valid(r.AccountId),groupIdValid=MemoryIdentity.Valid(r.GroupId),senderIdValid=MemoryIdentity.Valid(r.SenderId),friendIdValid=MemoryIdentity.Valid(r.FriendId)}).ToArray()}},output);
                return 0;
            }catch(Exception e){InspectResult(new{type="inspection_failure",targetProcessId=pid,code=e.GetType().Name,message=e.Message,method=e.TargetSite==null?null:e.TargetSite.DeclaringType.FullName+"."+e.TargetSite.Name},output);return 1;}
            finally{if(accessibility!=null)accessibility.Dispose();}
        }
        internal static bool SupportedCommand(string op){return op=="stop"||op=="invalidate"||op=="arm"||op=="accept";}
        static void ReadCommands() {
            try {
                string line;
                while(!stopping&&(line=Console.ReadLine())!=null) {
                    if(line.Length>262144)break;
                    var c=json.Deserialize<Dictionary<string,object>>(line);string op=Convert.ToString(c["type"]);
                    if(!SupportedCommand(op))continue;
                    // Cancel before taking sync: a slow UIA call must not delay a stop signal.
                    if(op=="stop")Interlocked.Increment(ref cancellation);
                    string refusal=null;
                    lock(sync) {
                        if(op=="stop") { stopping=true;break; }
                        if(op=="invalidate") { ready=null;if(fill!=null)filling=false;fill=null;fillActivate=false;cancellation++; }
                        if(op=="arm"&&current!=null) {
                            var r=json.ConvertToType<Recommendation>(c["recommendation"]);
                            if(Recommendation.ValidText(r.Text)&&SameContext(current,r))ready=r;
                        }
                        if(op=="accept") {
                            object idValue,sourceValue;
                            string id=c.TryGetValue("id",out idValue)?Convert.ToString(idValue):null;
                            string source=c.TryGetValue("source",out sourceValue)?Convert.ToString(sourceValue):"ui_click";
                            bool explicitActivation=source=="bubble_click";
                            if(filling)refusal="fill_in_progress";
                            else if(current==null)refusal="read_unavailable";
                            else if(ready==null||String.IsNullOrEmpty(id)||ready.Id!=id)refusal="recommendation_changed";
                            else if(!current.Available)refusal="read_"+current.Reason;
                            else if(!SameContext(current,ready))refusal="context_changed";
                            else if(!explicitActivation&&Win.GetForegroundWindow()!=target)refusal="wechat_background";
                            else if(!explicitActivation&&!current.Focused)refusal="input_not_focused";
                            else if(!explicitActivation&&!current.ImeAllowed)refusal="ime_unknown";
                            else if(DateTime.UtcNow.Ticks-current.CapturedTicks>TimeSpan.TicksPerMillisecond*500)refusal="snapshot_expired";
                            else if(!explicitActivation&&!FreshIme(current))refusal="ime_changed";
                            else {
                                fill=ready;ready=null;fillPointerEpoch=Interlocked.Read(ref pointerEpoch);
                                fillCancellation=cancellation;fillSource=source=="bubble_click"?"bubble_click":source=="pet_click"?"pet_click":"ui_click";
                                fillActivate=explicitActivation;filling=true;
                            }
                        }
                    }
                    if(refusal!=null)Emit(new{type="accept_refused",id=c.ContainsKey("id")?Convert.ToString(c["id"]):null,reason=refusal,source=sourceForDiagnostic(c)});
                }
            }catch { }
            stopping=true;
        }
        static string sourceForDiagnostic(Dictionary<string,object> command) {
            object value;
            return command.TryGetValue("source",out value)?Convert.ToString(value):"ui_click";
        }
        static void ReadLoop() {
            while(!stopping) {
                try {
                    long epoch=Interlocked.Read(ref activity);var s=reader.Read(epoch);
                    if(epoch!=Interlocked.Read(ref activity)){s.Available=false;s.Reason="input_activity";}
                    Recommendation request;
                    lock(sync) {
                        current=s;
                        if(ready!=null&&!SameContext(s,ready))ready=null;
                        request=fill;fill=null;
                    }
                    Emit(new{type="snapshot",snapshot=s});
                    if(request!=null)TryFill(request);
                }catch(Exception error) { lock(sync) { current=null;ready=null;fill=null;filling=false; } Emit(new{type="snapshot",snapshot=new ChatSnapshot { Reason="read_unavailable_"+error.GetType().Name }}); }
                Thread.Sleep(180);
            }
        }
        static bool SameContext(ChatSnapshot s,Recommendation r) {
            return s!=null&&s.Available&&s.AtEnd&&s.DraftEmpty&&s.DraftConsistent&&!s.Composing&&s.GroupChat==r.GroupChat&&(!s.GroupChat||(MemoryIdentity.Valid(s.AccountId)&&MemoryIdentity.Valid(s.GroupId)&&s.AccountId==r.AccountId&&s.GroupId==r.GroupId))&&s.Session==r.Session&&s.Tail==r.Tail&&s.ContentRevision==r.ContentRevision&&s.InputIdentity==r.InputIdentity&&s.Activity==r.Activity&&DateTime.UtcNow.Ticks-r.CreatedTicks>=0&&DateTime.UtcNow.Ticks-r.CreatedTicks<=TimeSpan.TicksPerSecond*60;
        }
        static bool Eligible(ChatSnapshot s,Recommendation r) {
            return SameContext(s,r)&&s.Foreground&&s.Focused&&s.ImeAllowed&&DateTime.UtcNow.Ticks-s.CapturedTicks<=TimeSpan.TicksPerMillisecond*500&&Win.GetForegroundWindow()==target;
        }
        static bool FreshIme(ChatSnapshot s) {
            uint p;long layout=Win.GetKeyboardLayout(Win.GetWindowThreadProcessId(target,out p)).ToInt64();
            if(layout!=s.Layout)return false;
            if(platform=="wecom")return s.ImeAllowed&&Win.GetForegroundWindow()==target;
            if(reader is WeChatReader&&((WeChatReader)reader).UsesUiFallback)return s.ImeAllowed&&Win.GetForegroundWindow()==target;
            var watch=Stopwatch.StartNew();var ime=QtCompositionGate.SampleCached(target.ToInt64());
            return watch.ElapsedMilliseconds<=100&&WeChatReader.ImeAllowed(ime,layout);
        }
        static bool FreshPasteIme(ChatSnapshot s,long popup,out string failure) {
            uint p;long layout=Win.GetKeyboardLayout(Win.GetWindowThreadProcessId(target,out p)).ToInt64();
            failure="native_ime_layout_changed";if(layout!=s.Layout)return false;
            if(reader is WeChatReader&&((WeChatReader)reader).UsesUiFallback){
                failure=s.ImeAllowed&&Win.GetForegroundWindow()==target?null:"ui_ime_unconfirmed";
                return failure==null;
            }
            var watch=Stopwatch.StartNew();var ime=popup==0?QtCompositionGate.SampleCached(target.ToInt64()):QtCompositionGate.SampleForPasteMenu(target.ToInt64(),popup);
            if(watch.ElapsedMilliseconds<=100&&WeChatReader.ImeAllowed(ime,layout)){failure=null;return true;}
            if(watch.ElapsedMilliseconds>100){failure="native_ime_sample_timeout";return false;}
            object reason;failure="native_ime_"+(ime.TryGetValue("reason",out reason)?Convert.ToString(reason):"unconfirmed");return false;
        }
        static IntPtr Mouse(int code,IntPtr message,IntPtr data) {
            if(code>=0&&IsPointerTakeover((uint)message.ToInt64()))Interlocked.Increment(ref pointerEpoch);
            return CallNextHookEx(mouseHook,code,message,data);
        }
        internal static bool IsPointerTakeover(uint message){return message==0x201||message==0x204||message==0x207||message==0x20b||message==0x20a||message==0x20e;}
        // The input hook must never wait for UIA SetValue or the command/read workers.
        // A missed invalidation remains safe: activity changes before trying this lock.
        internal static bool TryHookState(Func<bool> action) {
            if(!Monitor.TryEnter(sync))return false;
            try{return action();}finally{Monitor.Exit(sync);}
        }
        internal static bool ProcessTabState(bool down,Func<bool> accept) {
            if(!down) {bool eat=swallowed;swallowed=false;tabDown=false;return eat;}
            if(tabDown)return swallowed;
            tabDown=true;
            return TryHookState(delegate {if(!accept())return false;swallowed=true;return true;});
        }
        internal static bool IsInputActivityKey(uint key,bool down,bool injected,bool fillInProgress) {
            // Only auto-repeat of the Tab already swallowed for this fill is
            // exempt. A new Tab can move focus inside the same WeChat window;
            // foreign injected Tab events are never part of our Ctrl+V batch.
            return down&&(key!=9||fillInProgress&&(injected||!tabDown||!swallowed));
        }
        static IntPtr Keyboard(int code,IntPtr message,IntPtr data) {
            if(code<0)return CallNextHookEx(hook,code,message,data);
            var key=(Key)Marshal.PtrToStructure(data,typeof(Key));bool down=message.ToInt64()==0x100||message.ToInt64()==0x104;
            bool injected=(key.Flags&0x10)!=0;
            if(weComPasteKeys&&injected&&(key.Code==17||key.Code==86))return CallNextHookEx(hook,code,message,data);
            var pasteKey=NativeEmojiPasteShortcut.FilterHook(key.Code,down,key.Flags,key.Extra);
            if(pasteKey==NativeEmojiPasteShortcut.HookDisposition.BlockOwned)return new IntPtr(1);
            if(pasteKey==NativeEmojiPasteShortcut.HookDisposition.AllowOwned)return CallNextHookEx(hook,code,message,data);
            if(key.Code==9&&!down) {if(ProcessTabState(false,null))return new IntPtr(1);return CallNextHookEx(hook,code,message,data);}
            if(IsInputActivityKey(key.Code,down,injected,filling)&&(Win.GetForegroundWindow()==target||filling)) {
                Interlocked.Increment(ref activity);string suggestionId=null;
                TryHookState(delegate{suggestionId=ready==null?null:ready.Id;ready=null;return true;});
                if(suggestionId!=null)ThreadPool.QueueUserWorkItem(delegate {Emit(new{type="input_activity",id=suggestionId});});
            }
            bool allowTab=!injected;
            if(down&&key.Code==9&&allowTab) {
                if(ProcessTabState(true,delegate {
                    bool modifiers=Win.GetAsyncKeyState(16)<0||Win.GetAsyncKeyState(17)<0||Win.GetAsyncKeyState(18)<0||Win.GetAsyncKeyState(91)<0||Win.GetAsyncKeyState(92)<0;
                    if(!modifiers&&!filling&&ready!=null&&Eligible(current,ready)&&current.Activity==Interlocked.Read(ref activity)&&FreshIme(current)) {
                        fill=ready;ready=null;fillPointerEpoch=Interlocked.Read(ref pointerEpoch);fillCancellation=cancellation;fillActivate=false;filling=true;
                        return true;
                    }
                    return false;
                }))return new IntPtr(1);
            }
            return CallNextHookEx(hook,code,message,data);
        }
        static void TryFill(Recommendation r) {
            string source=fillSource;fillSource="physical_tab";
            bool activate=fillActivate;fillActivate=false;
            bool attempted=false,verified=false;
            string method="value_pattern",reason=null,clipboard="unused";int nativeCount=0;object clipboardEvidence=null,diagnostics=null;
            try {
                Emit(new{type="fill_begin",id=r.Id,window=target.ToInt64(),startedTicks=DateTime.UtcNow.Ticks});
                if(activate&&reader is WeChatReader) {
                    reader.Read(Interlocked.Read(ref activity));
                    if(!((WeChatReader)reader).FocusInput()){reason="wechat_foreground_failed";return;}
                    r.Activity=Interlocked.Read(ref activity);
                    fillPointerEpoch=Interlocked.Read(ref pointerEpoch);
                    fillCancellation=cancellation;
                    Thread.Sleep(180);
                } else if(activate&&reader is WeComReader) {
                    if(!Win.ActivateWindow(target)){reason="wecom_foreground_failed";return;}
                    r.Activity=Interlocked.Read(ref activity);
                    fillPointerEpoch=Interlocked.Read(ref pointerEpoch);
                    fillCancellation=cancellation;
                    Thread.Sleep(120);
                }
                var s=reader.Read(Interlocked.Read(ref activity));
                if(!Eligible(s,r)||s.Activity!=Interlocked.Read(ref activity)) {
                    reason=s==null||!s.Available?"fill_snapshot_unavailable":
                        Win.GetForegroundWindow()!=target?"fill_foreground_changed":
                        !s.Focused?"fill_input_not_focused":!s.ImeAllowed?"fill_ime_unknown":"fill_context_changed";
                    return;
                }
                if(reader is WeComReader) {
                    method="wecom_clipboard_paste";
                    var result=FillWeCom(r,s,(WeComReader)reader);
                    attempted=result.Attempted;verified=result.Verified;reason=result.Reason;
                    clipboard=result.Clipboard;clipboardEvidence=result.ClipboardEvidence;diagnostics=result.Diagnostics;return;
                }
                var field=reader.Field;object raw;
                if(!field.TryGetCurrentPattern(ValuePattern.Pattern,out raw))return;
                var value=(ValuePattern)raw;
                if(!FreshIme(s)||s.InputIdentity!=WeChatReader.InputName(field)+"|"+String.Join(".",field.GetRuntimeId())||!field.Current.HasKeyboardFocus||!field.Current.IsEnabled||field.Current.IsOffscreen||value.Current.IsReadOnly||!String.IsNullOrWhiteSpace(value.Current.Value)||Win.GetForegroundWindow()!=target||s.Activity!=Interlocked.Read(ref activity))return;
                var nativePlan=reader is WeChatReader&&((WeChatReader)reader).UsesUiFallback?null:NativeEmojiPlan.Create(r.Text);
                if(nativePlan!=null) {
                    method="native_clipboard_shortcut";nativeCount=nativePlan.Count;
                    var result=FillNative(r,s,field,nativePlan);
                    attempted=result.Attempted;verified=result.Verified;reason=result.Reason;clipboard=result.Clipboard;clipboardEvidence=result.ClipboardEvidence;diagnostics=result.Diagnostics;return;
                }
                lock(sync) {
                    if(stopping||s.Activity!=Interlocked.Read(ref activity)||fillPointerEpoch!=Interlocked.Read(ref pointerEpoch)||fillCancellation!=cancellation)return;
                    attempted=true;value.SetValue(r.Text);
                }
                var after=reader.Read(Interlocked.Read(ref activity));object again;
                verified=after.Available&&after.DraftConsistent&&after.GroupChat==r.GroupChat&&(!r.GroupChat||(after.AccountId==r.AccountId&&after.GroupId==r.GroupId))&&after.Session==r.Session&&after.InputIdentity==s.InputIdentity&&reader.Field!=null&&reader.Field.TryGetCurrentPattern(ValuePattern.Pattern,out again)&&((ValuePattern)again).Current.Value==r.Text;
            }catch(Exception e) {reason="fill_exception_"+e.GetType().Name;}
            finally {
                lock(sync){ ready=null;filling=false; }
                Emit(new{type="fill",id=r.Id,attempted=attempted,verified=verified,source=source,method=method,reason=reason,clipboard=clipboard,clipboardEvidence=clipboardEvidence,diagnostics=diagnostics,nativeEmojiCount=nativeCount,sendActions=0});
            }
        }
        sealed class NativeFillResult {internal bool Attempted,Verified;internal string Reason,Clipboard="unused";internal object ClipboardEvidence;internal readonly Dictionary<string,object> Diagnostics=new Dictionary<string,object>();}
        static NativeFillResult FillWeCom(Recommendation recommendation,ChatSnapshot original,WeComReader source) {
            var result=new NativeFillResult{Reason="wecom_fill_unconfirmed"};
            var diagnostic=result.Diagnostics;var elapsed=Stopwatch.StartNew();
            Action<string> stage=delegate(string name){diagnostic["stage"]=name;diagnostic["elapsedMs"]=elapsed.ElapsedMilliseconds;};
            Exception failure=null;
            var worker=new Thread((ThreadStart)delegate {
                object saved=null;
                try {
                    stage("preflight");
                    if(stopping||!original.DraftEmpty||!original.DraftConsistent||original.Composing||
                       original.Activity!=Interlocked.Read(ref activity)||fillCancellation!=cancellation){result.Reason="wecom_context_changed";return;}
                    if(!Win.ActivateWindow(target)){result.Reason="wecom_foreground_failed";return;}
                    Thread.Sleep(100);
                    stage("focus_input");
                    if(!source.FocusInput()){result.Reason="wecom_input_focus_failed";return;}
                    Thread.Sleep(120);
                    // The focus click belongs to this operation. A later user
                    // click still advances the epoch and invalidates verification.
                    fillPointerEpoch=Interlocked.Read(ref pointerEpoch);
                    var before=reader.Read(Interlocked.Read(ref activity));
                    if(!SameContext(before,recommendation)||!before.DraftEmpty||!before.DraftConsistent) {
                        result.Reason="wecom_context_changed_after_focus";return;
                    }
                    stage("clipboard_set");
                    try{saved=Clipboard.GetDataObject();}catch{}
                    Clipboard.SetText(recommendation.Text,TextDataFormat.UnicodeText);
                    result.Attempted=true;
                    Thread.Sleep(90);
                    stage("paste");
                    weComPasteKeys=true;Win.CtrlV();
                    Thread.Sleep(260);
                    stage("verify");
                    source.InvalidateScreen();
                    var after=reader.Read(Interlocked.Read(ref activity));
                    bool context=after!=null&&after.Available&&after.InputIdentity==original.InputIdentity&&
                        after.Session==original.Session&&after.Tail==original.Tail&&after.DraftConsistent;
                    diagnostic["context"]=context;diagnostic["draftEmpty"]=after!=null&&after.DraftEmpty;
                    diagnostic["foreground"]=Win.GetForegroundWindow()==target;
                    bool activitySame=recommendation.Activity==Interlocked.Read(ref activity);
                    diagnostic["activitySame"]=activitySame;diagnostic["activity"]=Interlocked.Read(ref activity);
                    bool foreground=Win.GetForegroundWindow()==target;
                    // The paste itself can generate extra injected key activity in WeCom.
                    // A same-context empty-to-nonempty draft is the reliable success signal.
                    result.Verified=context&&!after.DraftEmpty&&foreground;
                    result.Reason=result.Verified?null:context&&!after.DraftEmpty?"wecom_foreground_changed_after_paste":
                        context?"wecom_draft_not_observed":"wecom_context_changed_after_paste";
                } catch(Exception error) {failure=error;result.Reason="wecom_exception_"+error.GetType().Name;}
                finally {
                    weComPasteKeys=false;
                    string status="unused";
                    try {
                        if(saved==null&&Clipboard.ContainsText()&&Clipboard.GetText(TextDataFormat.UnicodeText)==recommendation.Text){Clipboard.Clear();status="cleared";}
                        else if(saved!=null&&Clipboard.ContainsText()&&Clipboard.GetText(TextDataFormat.UnicodeText)==recommendation.Text){Clipboard.SetDataObject(saved,true);status="restored";}
                        else if(saved!=null)status="changed_or_unrestored";
                    } catch {status="restore_unconfirmed";}
                    result.Clipboard=status;result.ClipboardEvidence=new{restored=status=="restored"||status=="cleared"};
                    diagnostic["elapsedMs"]=elapsed.ElapsedMilliseconds;
                    if(failure!=null)diagnostic["exceptionCode"]=failure.HResult;
                }
            });
            worker.SetApartmentState(ApartmentState.STA);worker.IsBackground=true;worker.Start();
            if(!worker.Join(3500)){result.Reason="wecom_fill_timeout";return result;}
            return result;
        }
        static NativeFillResult FillNative(Recommendation recommendation,ChatSnapshot original,AutomationElement field,NativeEmojiPlan plan) {
            var result=new NativeFillResult{Reason="native_fill_unconfirmed"};
            var elapsed=Stopwatch.StartNew();var diagnostic=result.Diagnostics;int validation=0;
            Action<string> stage=delegate(string name){diagnostic["stage"]=name;diagnostic["elapsedMs"]=elapsed.ElapsedMilliseconds;};
            {
                Func<bool> fastGuard=delegate {return !stopping&&original.Activity==Interlocked.Read(ref activity)&&fillPointerEpoch==Interlocked.Read(ref pointerEpoch)&&fillCancellation==Interlocked.Read(ref cancellation)&&Win.GetForegroundWindow()==target;};
                string imeFailure=null,contextFailure=null;
                Func<bool> unchanged=delegate {
                    contextFailure=null;imeFailure=null;diagnostic["validationCount"]=++validation;
                    if(stopping){contextFailure="native_stopping";return false;}
                    if(original.Activity!=Interlocked.Read(ref activity)){contextFailure="native_key_activity";return false;}
                    if(fillPointerEpoch!=Interlocked.Read(ref pointerEpoch)){contextFailure="native_pointer_activity";return false;}
                    if(fillCancellation!=Interlocked.Read(ref cancellation)){contextFailure="native_cancelled";return false;}
                    if(Win.GetForegroundWindow()!=target){contextFailure="native_foreground_changed";return false;}
                    var now=reader.Read(Interlocked.Read(ref activity));
                    if(!now.Available){contextFailure="native_snapshot_"+now.Reason;return false;}
                    if(!SameContext(now,recommendation)){contextFailure="native_recommendation_context_changed";return false;}
                    if(now.AccountId!=original.AccountId||now.FriendId!=original.FriendId||now.GroupId!=original.GroupId||now.Layout!=original.Layout){contextFailure="native_identity_or_layout_changed";return false;}
                    if(!FreshPasteIme(now,0,out imeFailure))return false;
                    diagnostic["keyActivityChanged"]=now.Activity!=Interlocked.Read(ref activity);
                    diagnostic["pointerActivityChanged"]=fillPointerEpoch!=Interlocked.Read(ref pointerEpoch);
                    diagnostic["cancelled"]=fillCancellation!=Interlocked.Read(ref cancellation);
                    if(stopping){contextFailure="native_stopping_during_validation";return false;}
                    if(now.Activity!=Interlocked.Read(ref activity)){contextFailure="native_key_activity_during_validation";return false;}
                    if(fillPointerEpoch!=Interlocked.Read(ref pointerEpoch)){contextFailure="native_pointer_activity_during_validation";return false;}
                    if(fillCancellation!=Interlocked.Read(ref cancellation)){contextFailure="native_cancelled_during_validation";return false;}
                    if(Win.GetForegroundWindow()!=target){contextFailure="native_foreground_changed_during_validation";return false;}
                    if(reader.Field==null||!reader.Field.GetRuntimeId().SequenceEqual(field.GetRuntimeId())){contextFailure="native_input_recreated";return false;}
                    if(!field.Current.HasKeyboardFocus||!field.Current.IsEnabled||field.Current.IsOffscreen){contextFailure="native_input_unavailable";return false;}
                    object value;
                    if(!field.TryGetCurrentPattern(ValuePattern.Pattern,out value)||((ValuePattern)value).Current.IsReadOnly){contextFailure="native_input_not_editable";return false;}
                    string draftValue=((ValuePattern)value).Current.Value;int length=draftValue.Length;diagnostic["beforeDraftLength"]=length;
                    if(!String.IsNullOrWhiteSpace(draftValue)){contextFailure="native_input_has_draft";return false;}
                    return true;
                };
                stage("preflight");
                if(!unchanged()){result.Reason=imeFailure??contextFailure??"native_context_changed";return result;}
                stage("clipboard_begin");
                var lease=NativeEmojiClipboard.Begin(plan.Text);
                try {
                    if(!lease.CanInvoke){result.Reason="native_clipboard_unavailable";return result;}
                    stage("shortcut_preflight");
                    if(!unchanged()){result.Reason=imeFailure??contextFailure??"native_context_changed";return result;}
                    if(new[]{16,17,18,91,92,86}.Any(key=>Win.GetAsyncKeyState(key)<0)){result.Reason="native_shortcut_keys_already_down";return result;}
                    using(var shortcut=NativeEmojiPasteShortcut.Prepare(fastGuard)) {
                        if(!shortcut.Prepared){result.Reason=shortcut.Reason;return result;}
                        if(!fastGuard()){result.Reason="native_shortcut_preinvoke_changed";return result;}
                        stage("shortcut_invoke");result.Attempted=true;
                        bool delivered=shortcut.Invoke(lease.MarkInvocationStarted);
                        diagnostic["shortcutInserted"]=shortcut.MainInserted;diagnostic["shortcutObserved"]=shortcut.MainObserved;
                        diagnostic["recoveryInserted"]=shortcut.RecoveryInserted;diagnostic["recoveryObserved"]=shortcut.RecoveryObserved;
                        diagnostic["ctrlDownPassed"]=shortcut.CtrlDownPassed;diagnostic["vDownPassed"]=shortcut.VDownPassed;diagnostic["shortcutKeysReleased"]=shortcut.KeysReleased;
                        if(!delivered){result.Reason=shortcut.Reason;return result;}
                    }
                    stage("verify_draft");
                    var watch=Stopwatch.StartNew();
                    do {
                        var after=reader.Read(Interlocked.Read(ref activity));object value,text;
                        if(!NativeEmojiPlan.SameDraftContext(original,after)) {
                            diagnostic["snapshotAvailable"]=after!=null&&after.Available;
                            diagnostic["sameSession"]=after!=null&&after.Session==original.Session;
                            diagnostic["sameInput"]=after!=null&&after.InputIdentity==original.InputIdentity;
                            diagnostic["sameTail"]=after!=null&&after.Tail==original.Tail;
                            diagnostic["sameRevision"]=after!=null&&after.ContentRevision==original.ContentRevision;
                            result.Reason=after==null||!after.Available?"native_verify_snapshot_unavailable":"native_verify_context_changed";break;
                        }
                        var actual=reader.Field;
                        if(actual!=null&&actual.TryGetCurrentPattern(ValuePattern.Pattern,out value)&&actual.TryGetCurrentPattern(TextPattern.Pattern,out text)) {
                            string observedValue=((ValuePattern)value).Current.Value,observedText=((TextPattern)text).DocumentRange.GetText(-1);
                            diagnostic["valueLength"]=observedValue.Length;diagnostic["documentLength"]=observedText.Length;
                            diagnostic["valueObjects"]=observedValue.Count(c=>c=='\uFFFC');diagnostic["documentObjects"]=observedText.Count(c=>c=='\uFFFC');
                            diagnostic["expectedObjects"]=plan.Count;
                            if(plan.Matches(observedValue,observedText)) {
                                lease.Complete(true);
                                result.Verified=lease.Restored&&lease.Status=="verified_restored";
                                result.Reason=result.Verified?null:"native_clipboard_changed_or_unrestored";break;
                            }
                            result.Reason=observedValue.Length==0&&observedText.Length==0?"native_verify_empty_draft":observedValue==plan.Text?"native_verify_literal_names":"native_verify_content_mismatch";
                        }else result.Reason="native_verify_pattern_unavailable";
                        if(lease.DeadlineElapsed){result.Reason="native_clipboard_publication_expired";break;}
                        if(stopping){result.Reason="native_stopping_after_invoke";break;}
                        if(original.Activity!=Interlocked.Read(ref activity)){result.Reason="native_key_activity_after_invoke";break;}
                        if(fillPointerEpoch!=Interlocked.Read(ref pointerEpoch)){result.Reason="native_pointer_activity_after_invoke";break;}
                        if(fillCancellation!=Interlocked.Read(ref cancellation)){result.Reason="native_cancelled_after_invoke";break;}
                        Thread.Sleep(40);
                    }while(watch.ElapsedMilliseconds<2200);
                } catch(Exception e) {result.Reason="native_exception_"+e.GetType().Name;diagnostic["exceptionCode"]=e.HResult;}
                finally {lease.Dispose();diagnostic["elapsedMs"]=elapsed.ElapsedMilliseconds;diagnostic["clipboardDeadlineElapsed"]=lease.DeadlineElapsed;result.Clipboard=lease.Status;result.ClipboardEvidence=new{sequenceChanged=lease.SequenceChanged,ownerTokenMatches=lease.OwnerTokenMatches,ownerWindowMatches=lease.OwnerWindowMatches};}
            }
            return result;
        }
    }
}
