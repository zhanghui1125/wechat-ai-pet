using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace WeChatPet.Runtime {
    public sealed class ProcessChoice {
        public int Id;public long Birth,Window;public string Platform,Label;
        public override string ToString(){
            string platform=Platform=="wecom"?"企业微信":"微信";
            return platform+(String.IsNullOrEmpty(Label)?"":" · "+Label)+" · "+Id;
        }
    }
    public sealed class DesktopRuntime : IDisposable {
        readonly PetWindow pet;readonly string home;readonly Dispatcher dispatcher;readonly DispatcherTimer timer;
        readonly RecommendationController controller=new RecommendationController();
        readonly RuntimeSettingsPersistence settingsPersistence;
        readonly ObserverFailureGate observerFailures=new ObserverFailureGate();
        readonly FillObservationPolicy fillObservation=new FillObservationPolicy();
        readonly FillNoticePolicy fillNotice=new FillNoticePolicy();
        readonly Stopwatch runtimeClock=Stopwatch.StartNew();
        readonly InlineSuggestionWindow inline;
        public MemoryStore Memories {get;private set;}
        MemoryWindow memoryWindow;
        Process observer;ProcessChoice boundTarget;long lastSnapshot;DateTime started,nextDiscovery,nextTargetCheck,nextWindowCheck;bool discovering,disposed;int observerRevision;
        ProcessChoice foregroundCandidate,autoTarget;DateTime foregroundCandidateSince,nextForegroundCheck;
        Task<bool> observerStopped=Task.FromResult(true);
        public Task<bool> ShutdownCompletion {get{return observerStopped;}}
        string status="等待微信或企业微信";ModelConnectionWindow connectionWindow;
        string generationError,generationErrorSession,generationErrorTail;
        string groupNoticeContext;
        string observerError,observerFailureCode;
        long snapshots,unavailable,generated,fills,injectedFills,fillFailures;DateTime diagnosticsAt=DateTime.MinValue;
        string lastFillMethod,lastFillReason,lastFillClipboard,lastFillSource;long nativeEmojiFills;
        object lastFillClipboardEvidence;
        object lastFillDiagnostics;
        readonly DiagnosticsWriteStatus diagnosticsWrite=new DiagnosticsWriteStatus();
        readonly string runtimeSession=Guid.NewGuid().ToString("N");
        public string DiagnosticsWarning {get{return diagnosticsWrite.ErrorCode==null?null:"本地运行记录暂未保存，故障排查信息可能过期。存储恢复后会自动重试。";}}
        readonly Dictionary<string,long> unavailableReasons=new Dictionary<string,long>();
        readonly DateTime runtimeStarted=DateTime.UtcNow;
        public RuntimeSettings Settings {get;private set;}
        public ModelClient Models {get;private set;}
        public List<ProcessChoice> Candidates=new List<ProcessChoice>();
        public string SettingsError {get{return settingsPersistence.Error;}}
        public string Status {get{return SettingsError??observerError??generationError??fillNotice.Message??status;}}
        public DesktopRuntime(PetWindow pet,string home) {
            this.pet=pet;this.home=home;dispatcher=pet.Dispatcher;string readError;Settings=LocalStore.Load(out readError);
            settingsPersistence=new RuntimeSettingsPersistence(Settings,LocalStore.Save,readError);
            settingsPersistence.TrySave(delegate{});
            Memories=new MemoryStore(LocalStore.Data);
            controller.Paused=Settings.Paused;
            inline=new InlineSuggestionWindow(delegate{return !disposed&&!Settings.Paused&&controller.Connected&&pet.IsPetHidden;},delegate{return controller.Current;},delegate{return controller.Ready;});
            controller.Generate+=Generate;controller.Invalidated+=delegate {fillObservation.Cancel();if(Models!=null)Models.Cancel();SendObserver(new{type="invalidate"});};controller.Changed+=Present;
            pet.PauseChanged+=SetPaused;pet.ListenRequested+=delegate {SetPaused(false);};pet.ConnectionRequested+=OpenConnection;
            pet.MemoryRequested+=OpenMemory;
            pet.RecommendationContextToken=CapturePetRequest;
            pet.RecommendationRequested+=RequestPetAction;
            pet.FillRequested+=RequestFill;
            pet.MinimizedToTaskbar+=HideSettingsForPet;
            timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};timer.Tick+=Tick;timer.Start();
            StartModel();Present();
        }
        async void StartModel() {
            if(SettingsError==LocalStore.SettingsReadFailure){Present();OpenConnection();return;}
            try {
                Models=new ModelClient(home,Settings);
                Models.AuthorizationUrl+=delegate(string provider,string url) {dispatcher.BeginInvoke(new Action(delegate {
                    if(!disposed)try {Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch {status="浏览器未打开，请重试登录";Present();}
                }));};
                await Models.Initialization;ConfigurationChanged(false);
            }catch(Exception e){status=ModelClient.Explain(e);Settings.Validated=false;Present();}
            if(!disposed&&(!Settings.Validated||Models==null||!Models.HasCredential(Settings.Provider)))OpenConnection();
        }
        public void MinimizeToTaskbar() { if(connectionWindow!=null)connectionWindow.Close(); pet.MinimizeToTaskbar(); }
        internal void HideSettingsForPet() {
            if(connectionWindow!=null)connectionWindow.Close();
            if(memoryWindow!=null)memoryWindow.Hide();
        }
        public void OpenAppearance() { if(connectionWindow!=null)connectionWindow.Close(); pet.OpenSettings(); }
        public void OpenConnection() {
            if(connectionWindow!=null){connectionWindow.Activate();return;}
            connectionWindow=new ModelConnectionWindow(this);connectionWindow.Closed+=delegate {connectionWindow=null;};connectionWindow.Show();
        }
        public void OpenMemory() {
            if(memoryWindow!=null){memoryWindow.Restore();return;}
            memoryWindow=new MemoryWindow(Memories,controller.Current,delegate{controller.Invalidate();Present();},delegate{return disposed?null:controller.Current;});
            memoryWindow.Closed+=delegate{memoryWindow=null;};memoryWindow.Show();
        }
        bool SaveSettings() {
            ClearGenerationError();
            bool saved=settingsPersistence.TrySave(delegate {controller.Connected=false;controller.Reset();});
            controller.Paused=Settings.Paused;
            if(saved)controller.Connected=Models!=null&&Models.HasCredential(Settings.Provider)&&Settings.Validated&&!String.IsNullOrWhiteSpace(Settings.Model);
            status=controller.Connected?"正在等待当前私聊的新来信":"请连接并验证模型";
            if(connectionWindow!=null)connectionWindow.RefreshSavedSettings(!saved);
            return saved;
        }
        public bool ConfigurationChanged(bool invalidateValidation) {
            if(invalidateValidation)Settings.Validated=false;
            bool saved=SaveSettings();Present();return saved;
        }
        public void SetPaused(bool value) {
            Settings.Paused=value;SaveSettings();Present();
        }
        public void SelectProcess(ProcessChoice choice) {
            if(choice==null||Settings.SelectedProcess==choice.Id&&Settings.SelectedProcessBirth==choice.Birth)return;
            autoTarget=null;
            Settings.SelectedProcess=choice.Id;Settings.SelectedProcessBirth=choice.Birth;
            if(!SaveSettings()){Present();return;}
            StopObserver();nextDiscovery=DateTime.UtcNow;status="正在切换微信窗口";Present();
        }
        static bool SameTarget(ProcessChoice a,ProcessChoice b) {
            return a!=null&&b!=null&&a.Id==b.Id&&a.Birth==b.Birth&&a.Window==b.Window;
        }
        static ProcessChoice ForegroundMessenger() {
            IntPtr active=Win.GetForegroundWindow();
            if(active==IntPtr.Zero)return null;
            uint pid;
            if(Win.GetWindowThreadProcessId(active,out pid)==0||pid==0)return null;
            try {
                using(var process=Process.GetProcessById((int)pid)) {
                    Win.SameUser(process);
                    string platform=process.ProcessName=="Weixin"?"weixin":process.ProcessName=="WXWork"?"wecom":null;
                    if(platform==null)return null;
                    if(platform=="weixin")AccessibilityBridge.Ensure(process.Id);
                    IntPtr window=platform=="weixin"?(Win.IsWeixinChatWindow(active)?active:IntPtr.Zero):Win.FindVisibleWindow(process.Id,"WeWorkWindow",600,400);
                    if(window==IntPtr.Zero)return null;
                    return new ProcessChoice{Id=process.Id,Birth=process.StartTime.ToUniversalTime().Ticks,Window=window.ToInt64(),Platform=platform,Label=Win.WindowTitle(window)};
                }
            }catch{return null;}
        }
        void AdoptForegroundTarget(ProcessChoice choice) {
            if(choice==null||SameTarget(boundTarget,choice))return;
            StopObserver();
            controller.Reset();
            foregroundCandidate=null;autoTarget=choice;
            nextDiscovery=DateTime.UtcNow;
            status="已切换到"+(choice.Platform=="wecom"?"企业微信":"微信")+"，正在重新连接";
            Present();
        }
        string CapturePetRequest() {
            if(disposed||controller.Current==null)return null;
            string context=PetClickIntent.ContextToken(controller.Current);
            // An unreadable group may show a notice on click, but this token
            // can never authorize a model request.
            if(context==null)return "notice:"+controller.Current.Window+":"+controller.Current.Reason;
            return context+"|"+Settings.Provider+"|"+Settings.Model+"|"+Settings.ReasoningEffort+"|"+Settings.Paused+"|"+controller.Connected;
        }
        void RequestPetAction(string capturedContext) {
            if(String.IsNullOrEmpty(capturedContext))return;
            string currentContext=CapturePetRequest();
            // A read/activity epoch can change between mouse-down and the
            // single-click timer. The click is still an explicit request, so
            // use the latest authoritative chat context instead of dropping it.
            if(currentContext==null) {
                status="聊天窗口正在变化，恢复后会自动生成回复";
                Present();return;
            }
            capturedContext=currentContext;
            if(capturedContext.StartsWith("notice:",StringComparison.Ordinal)) {
                status=controller.Current!=null&&controller.Current.GroupChat?GroupIdleStatus(controller.Current):PrivateIdleStatus(controller.Current);
                groupNoticeContext=capturedContext;pet.ShowRecommendationNotice(status);Present();return;
            }
            if(controller.Current!=null&&!controller.Current.GroupChat) {
                if(!controller.RequestPrivateRecommendation(DateTime.UtcNow.Ticks)) {
                    status=PrivateRetryMessage(controller.LastManualBlockReason);
                    if(controller.LastManualBlockReason!="generating"){groupNoticeContext=capturedContext;pet.ShowRecommendationNotice(status);}
                    Present();
                } else {groupNoticeContext=null;pet.ShowRecommendationNotice(null);}
                return;
            }
            if(!controller.RequestGroupRecommendation(DateTime.UtcNow.Ticks)) {
                status=GroupBlockMessage(controller.LastGroupBlockReason);
                if(controller.LastGroupBlockReason!="group_generating"){groupNoticeContext=capturedContext;pet.ShowRecommendationNotice(status);}
                Present();
            } else {groupNoticeContext=null;pet.ShowRecommendationNotice(null);}
        }
        internal static string PrivateIdleStatus(ChatSnapshot snapshot) {
            if(snapshot==null||!snapshot.Available)return snapshot!=null&&RecommendationController.IsRecoverableGap(snapshot.Reason)?
                "聊天窗口正在刷新，恢复后会自动生成回复":"当前聊天尚未读取，稍后再试";
            if(snapshot.GroupChat)return GroupIdleStatus(snapshot);
            return snapshot.DraftEmpty?"单击熊猫生成回复":"已有草稿，清空后再生成回复";
        }
        internal static string PrivateRetryMessage(string reason) {
            switch(reason) {
                case "paused":return "已暂停推荐，请先恢复";
                case "model_disconnected":return "请连接并验证模型";
                case "generating":return "正在生成回复";
                case "draft_not_empty":return "已有草稿，清空后再生成回复";
                case "composing":return "正在组词，结束输入后再生成";
                case "latest_self":case "no_peer_context":return "还没有可承接的对方消息";
                case "latest_unsupported":return "最后一条消息暂不支持生成回复";
                case "history_position":return "回到聊天最新消息后再生成";
                case "empty_chat":return "当前聊天还没有可回复的消息";
                case "snapshot_stale":return "聊天状态刚变化，稍后再点一次";
                case "tail_changed":case "context_unavailable":return "消息正在变化，稳定后再点一次";
                case "read_unavailable":return "当前聊天尚未读取，稍后再试";
                default:return "暂时无法生成，请确认微信聊天位于前台";
            }
        }
        void RequestFill(string source) {
            if(disposed)return;
            var ready=controller.Ready;
            if(ready==null) {
                status="当前没有可填入的回复";
                Present();return;
            }
            if(Settings.Paused||!controller.Connected||observer==null||controller.Current==null) {
                status=Settings.Paused?"已暂停推荐，请先恢复":!controller.Connected?"请先连接并验证模型":"微信连接尚未就绪";
                Present();return;
            }
            // Re-arming immediately before acceptance closes the small race between
            // a generated recommendation and the observer receiving its state.
            SendObserver(new{type="arm",recommendation=ready});
            SendObserver(new{type="accept",id=ready.Id,source=source??"ui_click"});
            status="正在填入建议";
            Present();
        }
        internal static string FillRefusalMessage(string reason) {
            switch(reason) {
                case "fill_in_progress":return "正在填入，请稍候";
                case "wechat_background":return "请先切回微信窗口，再点击填入";
                case "input_not_focused":return "请先点一下微信输入框，再点击填入";
                case "ime_unknown":case "ime_changed":return "输入法状态未稳定，请结束组词后再点";
                case "snapshot_expired":case "context_changed":return "聊天内容或输入框已变化，等待新建议后再填入";
                case "recommendation_changed":return "回复建议已更新，请点击最新气泡";
                case "wechat_foreground_failed":case "fill_foreground_changed":return "微信窗口未能切到前台，请先点一下微信窗口再试";
                case "fill_input_not_focused":return "微信输入框未获得焦点，请先点一下输入框再试";
                case "fill_ime_unknown":return "输入法状态未稳定，请结束组词后再试";
                case "fill_snapshot_unavailable":case "fill_context_changed":return "聊天状态刚变化，请稍后再点建议气泡";
                case "wecom_foreground_failed":case "wecom_input_focus_failed":return "企业微信窗口未能激活，请先点一下输入框再试";
                default:return "暂时无法填入，请确认微信输入框为空且位于前台";
            }
        }
        internal static string GroupIdleStatus(ChatSnapshot snapshot) {
            return GroupBlockMessage(RecommendationController.GroupBlockReason(snapshot));
        }
        internal static string GroupBlockMessage(string reason) {
            switch(reason) {
                case null:return "点击宠物生成群聊回复";
                case "paused":return "已暂停推荐，请先恢复";
                case "model_disconnected":return "请连接并验证模型";
                case "group_generating":return "正在生成群聊推荐";
                case "group_has_draft":return "已有草稿，清空后再生成群聊回复";
                case "group_draft_inconsistent":return "输入框状态未稳定，稍后再点击";
                case "group_composing":return "正在组词，结束输入后再生成";
                case "group_not_at_end":return "回到群聊最新消息后再生成";
                case "group_empty":return "群聊还没有可回复的消息";
                case "group_latest_self":return "最后一条是你发的，等待群内新来信";
                case "group_latest_unsupported":return "最新群消息类型暂不支持，请选择普通文字来信";
                case "group_latest_text_unavailable":return "最新文字尚未读到，请回到消息底部";
                case "group_latest_text_too_long":return "最新群消息超过读取长度，暂不生成";
                case "group_latest_sender_unavailable":return "最新消息的发言人未确认，暂不生成";
                case "group_history_sender_unavailable":return "历史消息的发言人字段冲突，暂不生成";
                case "group_scope_unavailable":return "群消息的账号或群归属未确认，暂不生成";
                case "group_history_unsupported":return "历史消息类型暂不支持，暂不生成";
                case "group_identity_unavailable":return "群聊身份尚未确认，暂不生成";
                case "group_snapshot_stale":return "群聊读取已过期，稍后重新点击";
                case "group_tail_changed":return "群消息正在变化，稳定后重新点击";
                case "wechat_hidden_or_minimized":return "微信已最小化，恢复后继续";
                case "native_no_supported_messages":return "当前群消息尚无法读取，暂不生成";
                case "input_activity":return "输入状态正在变化，稍后重新点击";
                default:return reason!=null&&reason.StartsWith("native_",StringComparison.Ordinal)?"群消息正在刷新，稍后重新点击":"群聊控件尚未读到，稍后重新点击";
            }
        }
        async void Generate(string id,List<ChatMessage> messages) {
            if(Models==null)return;
            fillNotice.ClearAttempt();
            ClearGenerationError();
            string provider=Settings.Provider,model=Settings.Model;
            bool followUp=controller.PendingFollowUp;
            try {
                var recipient=controller.Current;
                var request=RecommendationMemory.Fields(Memories,recipient,DateTime.UtcNow.Ticks);
                request.Add("provider",provider);request.Add("model",model);request.Add("effort",Settings.ReasoningEffort);request.Add("messages",messages);request.Add("followUp",followUp);
                var raw=await Models.Request("recommend",request);
                if(disposed||provider!=Settings.Provider||model!=Settings.Model)return;
                var result=(Dictionary<string,object>)raw;
                if(controller.Complete(id,Convert.ToString(result["suggestion"]),DateTime.UtcNow.Ticks)){generated++;SendObserver(new{type="arm",recommendation=controller.Ready});}
            }catch(Exception e) {
                if(!disposed&&controller.PendingId==id){
                    var failed=controller.Current;
                    controller.Invalidate();generationError=ModelClient.Explain(e);
                    generationErrorSession=failed==null?null:failed.Session;generationErrorTail=failed==null?null:failed.Tail;
                    status=generationError;Present();
                }
            }
        }
        void ClearGenerationError() {generationError=generationErrorSession=generationErrorTail=null;}
        internal static bool KeepGenerationError(string error,string session,string tail,ChatSnapshot snapshot) {
            return !String.IsNullOrEmpty(error)&&snapshot!=null&&snapshot.Available&&snapshot.DraftEmpty&&snapshot.Session==session&&snapshot.Tail==tail;
        }
        void Tick(object sender,EventArgs args) {
            if(disposed)return;
            if((DateTime.UtcNow-diagnosticsAt).TotalSeconds>=5){diagnosticsAt=DateTime.UtcNow;Memories.Save();SaveDiagnostics();}
            if(observer!=null) {
                bool dead;try{dead=observer.HasExited;}catch{dead=true;}
                bool targetChanged=false;
                if(DateTime.UtcNow>=nextTargetCheck) {
                    nextTargetCheck=DateTime.UtcNow.AddSeconds(1);
                    targetChanged=boundTarget==null||!TargetIdentity.IsCurrent(boundTarget.Id,boundTarget.Birth,boundTarget.Window);
                }
                if(dead||targetChanged||(DateTime.UtcNow-started).TotalSeconds>15&&(DateTime.UtcNow.Ticks-lastSnapshot)>TimeSpan.TicksPerSecond*5) {
                    StopObserver();controller.Reset();status="微信连接中断，正在重新连接";nextDiscovery=DateTime.UtcNow.AddSeconds(3);Present();
                }
            }
            // 微信把聊天开在独立窗口时，主窗口仍可能处于最小化；观察器若还绑在
            // 最小化的窗口上，会一直返回 wechat_hidden_or_minimized。这里改为跟随
            // 当前可见的微信窗口，用户开了哪个聊天窗口就绑哪个。
            if(observer!=null&&boundTarget!=null&&boundTarget.Platform=="weixin"&&DateTime.UtcNow>=nextWindowCheck) {
                nextWindowCheck=DateTime.UtcNow.AddSeconds(1);
                var bound=new IntPtr(boundTarget.Window);
                if(!Win.IsWindowVisible(bound)||Win.IsIconic(bound)) {
                    var visible=Win.FindVisibleWeixinWindows(boundTarget.Id);
                    if(visible.Count>0&&visible[0].ToInt64()!=boundTarget.Window)
                        AdoptForegroundTarget(new ProcessChoice{Id=boundTarget.Id,Birth=boundTarget.Birth,Window=visible[0].ToInt64(),Platform="weixin",Label=Win.WindowTitle(visible[0])});
                }
            }
            if(observer!=null&&boundTarget!=null&&Settings.SelectedProcess==0&&DateTime.UtcNow>=nextForegroundCheck) {
                DateTime now=DateTime.UtcNow;nextForegroundCheck=now.AddMilliseconds(400);
                IntPtr active=Win.GetForegroundWindow();
                ProcessChoice foreground=active.ToInt64()==boundTarget.Window?boundTarget:ForegroundMessenger();
                if(foreground==null||SameTarget(foreground,boundTarget))foregroundCandidate=null;
                else if(!SameTarget(foreground,foregroundCandidate)) {
                    foregroundCandidate=foreground;foregroundCandidateSince=now;
                } else if((now-foregroundCandidateSince).TotalMilliseconds>=800) {
                    AdoptForegroundTarget(foreground);
                }
            } else if(observer==null||Settings.SelectedProcess!=0)foregroundCandidate=null;
            if(observer==null&&!discovering&&observerStopped.IsCompleted&&DateTime.UtcNow>=nextDiscovery) {
                if(observerStopped.Status==TaskStatus.RanToCompletion&&observerStopped.Result)Discover();
                else {status="旧微信连接未释放，请重启桌宠";Present();}
            }
            if(controller.Current!=null&&((controller.Current.Available&&DateTime.UtcNow.Ticks-controller.Current.CapturedTicks>TimeSpan.TicksPerSecond)||DateTime.UtcNow.Ticks-lastSnapshot>TimeSpan.TicksPerSecond)&&
               !fillObservation.DeferStaleObservation(controller.Ready,controller.Current,!Settings.Paused&&controller.Connected,DateTime.UtcNow.Ticks,runtimeClock.Elapsed.Ticks)) {
                controller.Observe(null,DateTime.UtcNow.Ticks);Present();
            }
        }
        async void Discover() {
            discovering=true;nextDiscovery=DateTime.UtcNow.AddSeconds(3);
            try {
                var found=await Task.Run(delegate {
                    var list=new List<ProcessChoice>();
                    foreach(var p in Process.GetProcessesByName("Weixin"))using(p)try {
                        Win.SameUser(p);long birth=p.StartTime.ToUniversalTime().Ticks;
                        var windows=Win.FindWeixinChatWindows(p.Id);
                        if(windows.Count==0&&p.MainWindowHandle!=IntPtr.Zero)windows.Add(p.MainWindowHandle);
                        foreach(IntPtr window in windows)
                            list.Add(new ProcessChoice{Id=p.Id,Birth=birth,Window=window.ToInt64(),Platform="weixin",Label=Win.WindowTitle(window)});
                    }catch { }
                    foreach(var p in Process.GetProcessesByName("WXWork"))using(p)try {
                        Win.SameUser(p);IntPtr window=Win.FindVisibleWindow(p.Id,"WeWorkWindow",600,400);
                        if(window!=IntPtr.Zero)list.Add(new ProcessChoice{Id=p.Id,Birth=p.StartTime.ToUniversalTime().Ticks,Window=window.ToInt64(),Platform="wecom",Label="企业微信"});
                    }catch { }
                    return list;
                });
                if(disposed)return;Candidates=found;if(connectionWindow!=null)connectionWindow.UpdateInstances();
                ProcessChoice choice=autoTarget==null?null:found.FirstOrDefault(p=>SameTarget(p,autoTarget));
                if(autoTarget!=null&&choice==null)autoTarget=null;
                if(choice==null)choice=found.Count==1?found[0]:found.FirstOrDefault(p=>p.Id==Settings.SelectedProcess&&p.Birth==Settings.SelectedProcessBirth);
                // A fresh profile has no saved target. When both messengers are
                // running, start with the requested WeCom integration; an explicit
                // saved selection always wins above.
                if(choice==null&&Settings.SelectedProcess==0&&found.Count>0)choice=found.FirstOrDefault(p=>p.Platform=="wecom")??found[0];
                if(choice==null) {status=found.Count>1?"检测到多个应用，请在连接设置中选择":"等待打开微信或企业微信";Present();return;}
                if(!TargetIdentity.IsCurrent(choice.Id,choice.Birth,choice.Window)){status="微信窗口正在变化，稍后重连";Present();return;}
                string blocked=observerFailures.CodeFor(choice.Id,choice.Birth);
                if(blocked!=null){observerFailureCode=blocked;observerError=ObserverFailureGate.Explain(blocked);Present();return;}
                observerError=observerFailureCode=null;
                Process launching=null;
                try {
                int generation=++observerRevision;
                string observerName="WeChatObserver.exe";
                var process=new Process {StartInfo=new ProcessStartInfo {FileName=Path.Combine(home,observerName),Arguments=choice.Id+" "+choice.Birth+" "+choice.Window+" "+choice.Platform,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false)},EnableRaisingEvents=true};
                launching=process;
                process.OutputDataReceived+=delegate(object s,DataReceivedEventArgs e) {
                    if(e.Data==null||e.Data.Length>20*1024*1024)return;
                    Dictionary<string,object> value;
                    try{value=new JavaScriptSerializer {MaxJsonLength=20*1024*1024}.Deserialize<Dictionary<string,object>>(e.Data);}catch{return;}
                    dispatcher.BeginInvoke(new Action(delegate {if(!disposed&&observerRevision==generation)Observe(value);}));
                };
                process.ErrorDataReceived+=delegate { };
                process.Start();observer=process;launching=null;boundTarget=choice;autoTarget=null;fillNotice.ObserverStarted();nextTargetCheck=DateTime.UtcNow.AddSeconds(1);started=DateTime.UtcNow;lastSnapshot=started.Ticks;process.BeginOutputReadLine();process.BeginErrorReadLine();
                status="正在连接"+(choice.Platform=="wecom"?"企业微信":"微信");Present();
                }catch {if(launching!=null)launching.Dispose();StopObserver();status="无法启动微信观察器，请检查安装位置";Present();}
            }finally {
                discovering=false;
            }
        }
        void Observe(Dictionary<string,object> v) {
            string type=Convert.ToString(v["type"]);
            if(type=="snapshot") {
                var snapshot=new JavaScriptSerializer().ConvertToType<ChatSnapshot>(v["snapshot"]);lastSnapshot=DateTime.UtcNow.Ticks;
                if(!snapshot.Available&&StopForBuildFailure(snapshot.Reason))return;
                if(snapshot.Reason=="target_identity_changed") {
                    StopObserver();controller.Reset();nextDiscovery=DateTime.UtcNow;status="微信已切换，正在重新连接";Present();return;
                }
                snapshots++;if(!snapshot.Available){unavailable++;string reason=snapshot.Reason??"unknown";long n;unavailableReasons.TryGetValue(reason,out n);unavailableReasons[reason]=n+1;}
                fillNotice.Observe(snapshot);
                if(!Settings.Paused){if(snapshot.GroupChat)Memories.ObserveGroup(snapshot,lastSnapshot);else Memories.Observe(snapshot,lastSnapshot);}
                controller.Observe(snapshot,lastSnapshot);
                if(snapshot.Available) {
                    if(KeepGenerationError(generationError,generationErrorSession,generationErrorTail,snapshot))status=generationError;
                    else {ClearGenerationError();status=snapshot.GroupChat?GroupIdleStatus(snapshot):snapshot.DraftEmpty?"等待新来信":"已有草稿，继续自由编辑";}
                }
                else status=snapshot.GroupChat?GroupIdleStatus(snapshot):snapshot.Reason=="private_chat_required"?"请打开普通聊天":snapshot.Reason=="private_latest_unsupported"?"当前最后一条消息暂不支持推荐":snapshot.Reason=="wechat_hidden_or_minimized"?"微信已最小化，恢复后继续":RecommendationController.IsRecoverableGap(snapshot.Reason)?"聊天窗口刷新中，恢复后自动生成回复":"等待读取当前私聊";
                Present();
            }else if(type=="fill_begin") {
                object id,window,ticks;long targetWindow,startedTicks;
                if(v.TryGetValue("id",out id)&&v.TryGetValue("window",out window)&&v.TryGetValue("startedTicks",out ticks)&&
                   Int64.TryParse(Convert.ToString(window),out targetWindow)&&Int64.TryParse(Convert.ToString(ticks),out startedTicks))
                    fillObservation.Begin(Convert.ToString(id),targetWindow,startedTicks,controller.Ready,controller.Current,!Settings.Paused&&controller.Connected,DateTime.UtcNow.Ticks,runtimeClock.Elapsed.Ticks);
            }else if(type=="input_activity") { if(controller.Ready!=null&&controller.Ready.Id==Convert.ToString(v["id"]))controller.Invalidate();
            }else if(type=="accept_refused") {
                status=FillRefusalMessage(Convert.ToString(v["reason"]));Present();
            }else if(type=="fill") {
                object fillId;if(v.TryGetValue("id",out fillId))fillObservation.End(Convert.ToString(fillId));
                controller.Invalidate();bool verified=Convert.ToBoolean(v["verified"]);
                object detail;lastFillMethod=v.TryGetValue("method",out detail)?Convert.ToString(detail):null;lastFillReason=v.TryGetValue("reason",out detail)?Convert.ToString(detail):null;lastFillClipboard=v.TryGetValue("clipboard",out detail)?Convert.ToString(detail):null;lastFillSource=v.TryGetValue("source",out detail)?Convert.ToString(detail):null;
                lastFillClipboardEvidence=v.TryGetValue("clipboardEvidence",out detail)?detail:null;
                lastFillDiagnostics=v.TryGetValue("diagnostics",out detail)?detail:null;
                object attemptValue;bool attempted=v.TryGetValue("attempted",out attemptValue)&&Convert.ToBoolean(attemptValue);
                fillNotice.Record(NativeFillMethod(lastFillMethod),verified,lastFillReason,lastFillClipboard,controller.Current,attempted);
                status=verified?"已填入草稿，发送由你决定":fillNotice.Message??FillRefusalMessage(lastFillReason);
                if(verified&&NativeFillMethod(lastFillMethod))nativeEmojiFills++;
                if(verified){if(Convert.ToString(v["source"])=="injected_test_tab")injectedFills++;else fills++;}else fillFailures++;
                pet.Present(verified?PetState.Filled:PetState.Error,status,true);
            }else if(type=="failure") {string code=Convert.ToString(v["code"]);if(!StopForBuildFailure(code)){status="微信连接未通过："+code;Present();}}
        }
        static bool NativeFillMethod(string method) {return method=="native_clipboard_shortcut"||method=="native_paste_menu";}
        bool StopForBuildFailure(string code) {
            if(boundTarget==null||!observerFailures.Block(boundTarget.Id,boundTarget.Birth,code))return false;
            observerFailureCode=code;observerError=ObserverFailureGate.Explain(code);
            controller.Reset();StopObserver();nextDiscovery=DateTime.UtcNow.AddSeconds(3);Present();return true;
        }
        void Present() {
            if(disposed)return;
            if(groupNoticeContext!=null&&(groupNoticeContext!=CapturePetRequest()||controller.PendingId!=null||controller.Ready!=null||generationError!=null)) {
                groupNoticeContext=null;pet.ShowRecommendationNotice(null);
            }
            pet.SetListening(controller.Connected&&!Settings.Paused&&controller.Current!=null&&controller.Current.Available);
            if(controller.Ready!=null)pet.SetRecommendationHint("单击重新生成；点建议气泡填入");
            else pet.SetRecommendationHint(controller.Current==null?null:
                Settings.Paused?"已暂停推荐":!controller.Connected?"请连接并验证模型":controller.PendingId!=null?"正在生成回复":
                controller.Current.GroupChat?Status:"单击熊猫重新生成");
            if(SettingsError!=null)pet.Present(PetState.Error,SettingsError,true);
            else if(Settings.Paused)pet.Present(PetState.Paused,"已暂停推荐",true);
            else if(controller.Ready!=null)pet.Present(PetState.Ready,controller.Ready.Text,true);
            else if(controller.PendingId!=null)pet.Present(PetState.Thinking,"正在生成推荐",true);
            else pet.Present(controller.Connected&&String.IsNullOrEmpty(generationError)&&String.IsNullOrEmpty(observerError)&&fillNotice.Message==null?PetState.Idle:PetState.Error,Status,true);
            inline.Refresh();
        }
        void SendObserver(object value) {
            if(observer==null)return;
            try {byte[] bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)+"\n");observer.StandardInput.BaseStream.Write(bytes,0,bytes.Length);observer.StandardInput.BaseStream.Flush();}catch { }
        }
        void StopObserver(bool fast=false) {
            fillObservation.Cancel();
            if(observer==null)return;
            string platform=boundTarget==null?null:boundTarget.Platform;
            var p=observer;observer=null;boundTarget=null;observerRevision++;
            // WeCom has no global keyboard-hook guardian. Once its observer exits,
            // a short settling period is sufficient before reconnecting.
            bool guardianLease=platform!=null&&platform!="wecom";
            // fast: 退出程序时不再等守护租约自然过期（6.5s），观察器一收尾就走。
            observerStopped=guardianLease?ObserverShutdown.StopAsync(p,fast?1200:9000,fast?600:2000,fast?0:6500):ObserverShutdown.StopAsync(p,fast?800:3000,fast?400:1000,fast?0:250);
        }
        void SaveDiagnostics() {
            var last=controller.Current==null||controller.Current.Messages==null?null:controller.Current.Messages.LastOrDefault();
            string previousWarning=DiagnosticsWarning;
            diagnosticsWrite.TryWrite(delegate {LocalStore.Write(Path.Combine(LocalStore.Data,"health.json"),new JavaScriptSerializer().Serialize(new{
                schemaVersion=2,appProcessId=Process.GetCurrentProcess().Id,runtimeSession=runtimeSession,runtimeStartedAtUtc=runtimeStarted.ToString("o"),
                appModuleId=typeof(DesktopRuntime).Assembly.ManifestModule.ModuleVersionId.ToString("D"),sendActionsMeaning="application_does_not_send_messages",
                updatedAt=DateTime.UtcNow.ToString("o"),uptimeSeconds=(long)(DateTime.UtcNow-runtimeStarted).TotalSeconds,snapshots=snapshots,unavailable=unavailable,incomingTriggers=controller.TriggerCount,
                targetProcessId=boundTarget==null?0:boundTarget.Id,targetProcessBirth=boundTarget==null?0:boundTarget.Birth,targetWindow=boundTarget==null?0:boundTarget.Window,
                targetPlatform=boundTarget==null?null:boundTarget.Platform,targetLabel=boundTarget==null?null:boundTarget.Label,
                memoryEnabled=Memories.Data.Enabled,memoryIdentityAvailable=MemoryStore.Identified(controller.Current)||MemoryStore.GroupIdentified(controller.Current),memoryFriends=Memories.Data.Friends.Count,memoryGroups=Memories.Data.Groups.Count,memoryError=Memories.Error,
                groupChat=controller.Current!=null&&controller.Current.GroupChat,groupMessages=controller.Current==null?0:controller.Current.GroupMessageCount,groupMembers=controller.Current==null?0:controller.Current.GroupMemberCount,groupSelfMessages=controller.Current==null?0:controller.Current.GroupSelfMessages,groupVerifiedMessages=controller.Current==null?0:controller.Current.GroupVerifiedMessages,groupEmptySenderMessages=controller.Current==null?0:controller.Current.GroupEmptySenderMessages,
                memorySchemaVersion=Memories.Data.Version,memoryReplyPreferenceFriends=Memories.Data.Friends.Count(f=>!String.IsNullOrWhiteSpace(f.Preferences)),memoryExpressionExampleFriends=Memories.Data.Friends.Count(f=>f.ExpressionExamples!=null&&f.ExpressionExamples.Count>0),legacyReplyPreferencePending=!String.IsNullOrWhiteSpace(Memories.Data.LegacyPreferences),
                generated=generated,physicalTabFills=fills,injectedTestTabFills=injectedFills,unconfirmedFills=fillFailures,usesOcr=false,sendActions=0,
                nativeEmojiFills=nativeEmojiFills,lastFillMethod=lastFillMethod,lastFillReason=lastFillReason,lastFillClipboard=lastFillClipboard,lastFillSource=lastFillSource,
                lastFillClipboardEvidence=lastFillClipboardEvidence,
                lastFillDiagnostics=lastFillDiagnostics,
                available=controller.Current!=null&&controller.Current.Available,reason=controller.Current==null?"waiting":controller.Current.Reason,
                currentChatKind=controller.Current==null?"unknown":controller.Current.GroupChat?"group":"private",
                currentMessageCount=controller.Current==null||controller.Current.Keys==null?0:controller.Current.Keys.Length,
                draftEmpty=controller.Current!=null&&controller.Current.DraftEmpty,imeAllowsTab=controller.Current!=null&&controller.Current.ImeAllowed,
                composing=controller.Current!=null&&controller.Current.Composing,inputFocused=controller.Current!=null&&controller.Current.Focused,
                wechatForeground=controller.Current!=null&&controller.Current.Foreground,recommendationReady=controller.Ready!=null,generating=controller.PendingId!=null,
                petMinimized=pet.WindowState==System.Windows.WindowState.Minimized,petInTray=pet.IsInTray,inlineVisible=inline.IsVisible,
                inlineDecision=inline.LastDecision,petVisible=pet.IsVisible,
                latestRole=last==null?null:last.Role,latestType=last==null?0:last.Type,latestHasText=last!=null&&!String.IsNullOrEmpty(last.Text),latestHasImage=last!=null&&last.Image!=null,latestHasVideo=ReplyContext.IsVideo(last),latestImageMime=last==null||last.Image==null?null:last.Image.Mime,latestAgeSeconds=last==null?-1:(long)(DateTime.UtcNow.Ticks-last.CreatedTicks)/TimeSpan.TicksPerSecond,
                triggerDecision=controller.LastDecision,groupBlockReason=controller.Current!=null&&controller.Current.GroupChat?RecommendationController.GroupBlockReason(controller.Current):null,lastGroupRequestBlock=controller.LastGroupBlockReason,emptyChatObservations=controller.EmptyChatObservations,lastTriggerOrigin=controller.LastTriggerOrigin,unavailableReasons=unavailableReasons,
                status=status,candidates=Candidates.Count,discovering=discovering,observerRunning=observer!=null,
                provider=Settings.Provider,validated=Settings.Validated,paused=Settings.Paused,generationError=generationError,observerFailure=observerFailureCode,settingsError=SettingsError}),false);},DateTime.UtcNow.Ticks);
            if(previousWarning!=DiagnosticsWarning&&connectionWindow!=null)connectionWindow.RefreshDiagnosticNotice();
        }
        public void Dispose() {
            if(disposed)return;disposed=true;pet.RecommendationContextToken=null;pet.RecommendationRequested-=RequestPetAction;pet.FillRequested-=RequestFill;timer.Stop();inline.Stop();controller.Reset();StopObserver(true);
            Memories.Save();SaveDiagnostics();if(memoryWindow!=null)memoryWindow.Close();if(connectionWindow!=null)connectionWindow.Close();if(Models!=null)Models.Dispose();
        }
    }
}
