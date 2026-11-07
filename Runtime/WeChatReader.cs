using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace WeChatPet.Runtime {
    public sealed class WeChatReader : IChatReader {
        readonly int pid; readonly long birth;readonly IntPtr window;
        readonly Dictionary<string,ChatImage> imageCache=new Dictionary<string,ChatImage>();
        public AutomationElement Field { get; private set; }
        public string UiDiagnostics { get; private set; }
        internal NativeMessageReader.Frame NativeFrame {get;private set;}
        static long Birth(int pid){using(var p=Process.GetProcessById(pid))return p.StartTime.ToUniversalTime().Ticks;}
        public WeChatReader(int pid,long hwnd):this(pid,Birth(pid),hwnd) { }
        public WeChatReader(int pid,long birth,long hwnd) { this.pid=pid;this.birth=birth;window=new IntPtr(hwnd); }
        public ChatSnapshot Read(long activity) {
            var s=new ChatSnapshot { Reason="controls_unavailable", Window=window.ToInt64(), Activity=activity, CapturedTicks=DateTime.UtcNow.Ticks };
            Field=null;NativeFrame=null;UiDiagnostics="nodes=0,title=0,input=0,list=0";
            if(!TargetIdentity.IsCurrent(pid,birth,window.ToInt64())) {s.Reason="target_identity_changed";return s;}
            if(!Win.IsWindowVisible(window)||Win.IsIconic(window)) { s.Reason="wechat_hidden_or_minimized"; return s; }
            AutomationElement root=AutomationElement.FromHandle(window), title=null,input=null,list=null,groupCount=null;
            TreeWalker w=TreeWalker.RawViewWalker; var q=new Queue<AutomationElement>();q.Enqueue(root); int count=0;
            int titleCount=0,inputCount=0,listCount=0;
            while(q.Count>0&&count++<1000) {
                var e=q.Dequeue(); var c=e.Current; string id=c.AutomationId; int dot=id.LastIndexOf('.'); if(dot>=0) id=id.Substring(dot+1);
                if(id=="current_chat_name_label"&&c.ClassName=="mmui::XTextView") { titleCount++;if(title!=null) return s; title=e; }
                if(id=="current_chat_count_label"&&c.ControlType==ControlType.Text) { if(groupCount!=null)return s;groupCount=e; }
                if(id=="chat_input_field"&&c.ClassName=="mmui::ChatInputField") { inputCount++;if(input!=null) return s; input=e; }
                if(id=="chat_message_list"&&c.ClassName=="mmui::RecyclerListView") { listCount++;if(list!=null) return s; list=e;continue; }
                if(c.ClassName=="mmui::ChatSessionList"||c.ClassName=="mmui::ChatSessionCell")continue;
                for(var child=w.GetFirstChild(e);child!=null;child=w.GetNextSibling(child))q.Enqueue(child);
            }
            UiDiagnostics="nodes="+count+",title="+titleCount+",input="+inputCount+",list="+listCount+",remaining="+q.Count;
            if(q.Count!=0||title==null||input==null||list==null)return s;
            string contact=title.Current.Name; int[] listId=list.GetRuntimeId();
            object vp,tp;
            if(listId.Length<4){s.Reason="list_runtime_id_unavailable";return s;}
            if(!input.TryGetCurrentPattern(ValuePattern.Pattern,out vp)){s.Reason="input_value_pattern_unavailable";return s;}
            if(!input.TryGetCurrentPattern(TextPattern.Pattern,out tp)){s.Reason="input_text_pattern_unavailable";return s;}
            var value=(ValuePattern)vp; string draft=value.Current.Value; string text=((TextPattern)tp).DocumentRange.GetText(-1);
            string groupCountIdentity=null,groupCountText=null;
            if(groupCount!=null&&!groupCount.Current.IsOffscreen) {
                string members=groupCount.Current.Name;
                if(HasGroupCount(members)) {
                    string groupId=String.Join(".",groupCount.GetRuntimeId());
                    if(!String.IsNullOrEmpty(contact)&&contact==title.Current.Name&&contact==InputName(input)&&
                       String.Join(".",listId)==String.Join(".",list.GetRuntimeId())&&
                       groupId==String.Join(".",groupCount.GetRuntimeId())&&members==groupCount.Current.Name&&!groupCount.Current.IsOffscreen) {
                        s.GroupChat=true;groupCountIdentity=groupId;groupCountText=members;
                    }
                    if(!s.GroupChat){s.Reason="group_count_binding_changed";return s;}
                }
            }
            var ids=new List<int>();var texts=new List<string>();var elements=new List<AutomationElement>();var imageRows=new List<bool>(); int rows=0;
            for(var row=w.GetFirstChild(list);row!=null&&rows++<200;row=w.GetNextSibling(row)) {
                string rowClass=row.Current.ClassName;bool image=rowClass=="mmui::ChatImageItemView";
                if(!image&&rowClass!="mmui::ChatTextItemView")continue;
                int[] rid=row.GetRuntimeId();ids.Add(rid[rid.Length-1]);texts.Add(row.Current.Name);elements.Add(row);imageRows.Add(image);
            }
            if(rows>=200){s.Reason="message_row_limit";return s;}
            var nf=NativeMessageReader.Sample(pid,listId[listId.Length-1],ids.ToArray());
            NativeFrame=nf;
            if(!nf.Valid) { s.Reason="native_"+nf.Reason;return s; }
            bool empty=nf.Count==0&&nf.Model.Count==0&&ids.Count==0;
            if(nf.Model.Count==0&&!empty) { s.Reason="native_no_supported_messages";return s; }
            if(!s.GroupChat&&!empty&&!PrivateTailKnown(nf)) { s.Reason="private_latest_unsupported";return s; }
            string session=empty?null:nf.Model[nf.Model.Count-1].SessionTag;
            var identity=empty?null:nf.Model[nf.Model.Count-1];
            if(s.GroupChat) {
                s.GroupMessageCount=nf.Model.Count;s.GroupMemberCount=nf.Model.Where(r=>r.GroupSenderKnown).Select(r=>r.SenderTag).Distinct().Count();
                s.GroupSelfMessages=nf.Model.Count(r=>r.GroupSenderKnown&&!r.Peer);s.GroupVerifiedMessages=nf.Model.Count(r=>r.GroupSenderKnown);
                s.GroupEmptySenderMessages=nf.Model.Count(r=>r.SenderEvidence=="empty");
                string groupReason=GroupFrameReason(nf);
                if(groupReason!=null){s.Reason=groupReason;return s;}
                s.AccountId=identity.AccountId;s.GroupId=identity.GroupId;
            }
            else if(nf.Model.Any(r=>r.GroupChat)){s.Reason="chat_kind_changed";return s;}
            else if(identity!=null&&MemoryIdentity.Valid(identity.AccountId)&&MemoryIdentity.Valid(identity.FriendId)&&nf.Model.All(r=>r.AccountId==identity.AccountId&&r.FriendId==identity.FriendId)) {
                s.AccountId=identity.AccountId;s.FriendId=identity.FriendId;
            }
            if(!s.GroupChat&&nf.Model.Any(r=>r.SessionTag!=session||!r.DirectionKnown)) { s.Reason="private_chat_required";return s; }
            if(nf.Visible.Count!=ids.Count){s.Reason="visible_message_binding_changed";return s;}
            var visibleText=new Dictionary<string,string>();var visibleImages=new Dictionary<string,ChatImage>();
            string requiredImageKey=nf.Model.Count>0&&nf.Model[nf.Model.Count-1].Type==3?nf.Model[nf.Model.Count-1].KeyTag:null;
            string requiredVideoKey=nf.Model.Count>0&&nf.Model[nf.Model.Count-1].Type==ReplyContext.VideoType?nf.Model[nf.Model.Count-1].KeyTag:null;
            for(int i=0;i<ids.Count;i++) {
                int[] rid=elements[i].GetRuntimeId();var nr=nf.Visible[i];
                if(imageRows[i]) {
                    if(!TryMapVisibleImage(ids[i],texts[i],rid[rid.Length-1],elements[i],nr)){s.Reason="visible_image_binding_changed";return s;}
                    if(nr.Type==3&&nr.KeyTag!=requiredImageKey)continue;
                    if(nr.Type==ReplyContext.VideoType&&nr.KeyTag!=requiredVideoKey)continue;
                    ChatImage image;
                    if(!imageCache.TryGetValue(nr.KeyTag,out image)) {
                        string reason;
                        if(!ChatImageCapture.TryCapture(window,elements[i],out image,out reason)) {
                            // A video cover is useful visual context, but its absence must
                            // not suppress a reply to the confirmed video message.
                            if(nr.Type!=ReplyContext.VideoType){s.Reason=reason??"image_capture_failed";return s;}
                            image=null;
                        }
                        if(image!=null) {
                            imageCache[nr.KeyTag]=image;
                            if(imageCache.Count>12) {
                                string stale=imageCache.Keys.FirstOrDefault();
                                if(stale!=null)imageCache.Remove(stale);
                            }
                        }
                    }
                    if(image!=null)visibleImages[nr.KeyTag]=image;
                    if(elements[i].Current.ClassName!="mmui::ChatImageItemView"){s.Reason="image_row_changed_during_capture";return s;}
                } else if(!TryMapVisibleText(ids[i],texts[i],rid[rid.Length-1],elements[i].Current.Name,nr,visibleText)) {
                    s.Reason="visible_text_binding_changed";return s;
                }
            }
            if(String.Join(".",listId)!=String.Join(".",list.GetRuntimeId())||contact!=title.Current.Name||contact!=InputName(input)){s.Reason="chat_controls_changed_during_read";return s;}
            if(s.GroupChat&&(groupCount==null||groupCount.Current.IsOffscreen||groupCountText!=groupCount.Current.Name||groupCountIdentity!=String.Join(".",groupCount.GetRuntimeId()))){s.Reason="group_count_changed_during_read";return s;}
            string contentRevision;s.Messages=ProjectMessages(nf,visibleText,visibleImages,s.GroupChat,out contentRevision);
            int keyCount=nf.Model.Count;
            while(keyCount>0&&ReplyContext.IsSystemNotice(nf.Model[keyCount-1].Type))keyCount--;
            s.Session=session;s.Contact=contact;s.Keys=nf.Model.Take(keyCount).Select(r=>r.KeyTag).ToArray();s.Tail=s.Keys.Length==0?null:s.Keys[s.Keys.Length-1];
            s.ContentRevision=contentRevision;
            s.InputIdentity=contact+"|"+String.Join(".",input.GetRuntimeId());
            s.ViewIdentity=s.InputIdentity+"|"+String.Join(".",listId);
            var inputBounds=input.Current.BoundingRectangle;Win.ScreenRect windowBounds;
            if(!inputBounds.IsEmpty&&Win.PhysicalBounds(window,out windowBounds)) {
                s.InputLeft=inputBounds.Left;s.InputTop=inputBounds.Top;s.InputWidth=inputBounds.Width;s.InputHeight=inputBounds.Height;
                s.WindowLeft=windowBounds.Left;s.WindowTop=windowBounds.Top;s.WindowWidth=windowBounds.Right-windowBounds.Left;s.WindowHeight=windowBounds.Bottom-windowBounds.Top;
            }
            s.AtEnd=nf.AtEnd;s.DraftConsistent=draft==text;s.DraftEmpty=String.IsNullOrWhiteSpace(draft)&&s.DraftConsistent&&!value.Current.IsReadOnly&&input.Current.IsEnabled&&!input.Current.IsOffscreen;
            s.Foreground=Win.GetForegroundWindow()==window;s.Focused=input.Current.HasKeyboardFocus;
            uint owner; s.Layout=Win.GetKeyboardLayout(Win.GetWindowThreadProcessId(window,out owner)).ToInt64();
            if(owner!=pid)return s;
            if(s.Foreground) {
                var clock=Stopwatch.StartNew(); var ime=QtCompositionGate.Sample(window.ToInt64());
                s.Composing=Convert.ToString(ime["compositionState"])=="active_reported";
                s.ImeAllowed=clock.ElapsedMilliseconds<=100&&ImeAllowed(ime,s.Layout);
            }
            s.Available=true;s.Reason=empty?"empty_chat":"ok";s.CapturedTicks=DateTime.UtcNow.Ticks;Field=input;return s;
        }
        static bool GroupScopeKnown(NativeMessageReader.Row row) {
            return row!=null&&row.Supported&&row.GroupChat&&row.GroupScopeKnown&&MemoryIdentity.Valid(row.AccountId)&&MemoryIdentity.Valid(row.GroupId)&&
                !String.IsNullOrEmpty(row.SessionTag)&&!String.IsNullOrEmpty(row.KeyTag)&&String.IsNullOrEmpty(row.FriendId)&&String.IsNullOrEmpty(row.MemoryKey);
        }
        internal static bool PrivateTailKnown(NativeMessageReader.Frame frame) {
            // The native adapter omits unknown row classes. An older readable
            // peer message must not become the tail when the actual last row
            // was omitted; its sender and type have not been established.
            if(frame==null||!frame.Valid||frame.Count<=0||frame.Model==null||frame.Model.Count==0)return false;
            var latest=frame.Model[frame.Model.Count-1];
            return latest!=null&&latest.Supported&&latest.ModelIndex==frame.Count-1;
        }
        internal bool FocusInput() {
            if(Field==null||!Win.ActivateWindow(window))return false;
            try {
                var bounds=Field.Current.BoundingRectangle;
                if(!bounds.IsEmpty)
                    Win.Click(window,(int)Math.Round(bounds.Left+bounds.Width/2),(int)Math.Round(bounds.Top+bounds.Height/2));
                Field.SetFocus();
                return true;
            }catch{return false;}
        }
        static bool GroupSenderKnown(NativeMessageReader.Row row) {
            return row.GroupSenderKnown&&row.DirectionKnown&&MemoryIdentity.Valid(row.SenderId)&&row.SenderEvidence==(row.Peer?"member":"account");
        }
        internal static string GroupFrameReason(NativeMessageReader.Frame frame) {
            if(frame==null||!frame.Valid||frame.Model==null||frame.Model.Count==0)return "group_scope_unavailable";
            var latest=frame.Model[frame.Model.Count-1];
            if(latest==null||latest.ModelIndex!=frame.Count-1)return "group_latest_unsupported";
            if(!GroupScopeKnown(latest))return "group_scope_unavailable";
            // Never recover an older message as the tail when the actual last sender is unknown.
            if(!GroupSenderKnown(latest))return "group_latest_sender_unavailable";
            int previous=-1;
            foreach(var row in frame.Model) {
                if(!GroupScopeKnown(row)||row.AccountId!=latest.AccountId||row.GroupId!=latest.GroupId||row.SessionTag!=latest.SessionTag||
                   row.ModelIndex<=previous||row.ModelIndex>latest.ModelIndex)return "group_scope_unavailable";
                previous=row.ModelIndex;
                if(GroupSenderKnown(row))continue;
                // A missing member may be omitted only in older rows whose account/room is proved.
                // A nonempty conflicting/invalid member is never treated as a missing one.
                if(row.ModelIndex>=latest.ModelIndex||row.GroupSenderKnown||row.DirectionKnown||row.SenderEvidence!="empty"||!String.IsNullOrEmpty(row.SenderId))
                    return "group_history_sender_unavailable";
            }
            return null;
        }
        internal static List<ChatMessage> ProjectMessages(NativeMessageReader.Frame frame,IDictionary<string,string> visibleText,IDictionary<string,ChatImage> visibleImages,bool group,out string revision) {
            if(group){string reason=GroupFrameReason(frame);if(reason!=null)throw new InvalidOperationException(reason);}
            var all=new List<ChatMessage>();var included=new List<ChatMessage>();
            foreach(var row in frame.Model) {
                string body;visibleText.TryGetValue(row.KeyTag,out body);ChatImage image;visibleImages.TryGetValue(row.KeyTag,out image);
                if(row.Type==ReplyContext.VideoType)body="[视频消息]";
                bool include=!group||GroupSenderKnown(row);
                var message=new ChatMessage {Key=row.KeyTag,MemoryKey=row.MemoryKey,SenderId=row.SenderId,Type=row.Type,LocalId=row.LocalId,
                    CreatedTicks=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(row.Created).Ticks,Role=include?(row.Peer?"peer":"self"):null,Text=body??"",Image=image};
                all.Add(message);if(include)included.Add(message);
            }
            int replyableEnd=included.Count;
            while(replyableEnd>0&&ReplyContext.IsSystemNotice(included[replyableEnd-1].Type))replyableEnd--;
            if(replyableEnd!=included.Count)included=included.Take(replyableEnd).ToList();
            // Hash every row before omission. Removed/edited senderless history must still
            // invalidate an older recommendation; only verified rows reach the model.
            object evidence=group?(object)new {Messages=all.Select(m=>new {m.Key,m.MemoryKey,m.SenderId,m.Type,m.LocalId,m.CreatedTicks,m.Role,m.Text,image=m.Image==null?null:new {m.Image.Mime,m.Image.Fingerprint}}),Rows=frame.Model.Select(r=>new {r.KeyTag,r.SessionTag,r.AccountId,r.GroupId,r.SenderTag,r.SenderEvidence,r.GroupScopeKnown,r.GroupSenderKnown,r.ModelIndex}).ToArray()}:all.Select(m=>new {m.Key,m.MemoryKey,m.SenderId,m.Type,m.LocalId,m.CreatedTicks,m.Role,m.Text,image=m.Image==null?null:new {m.Image.Mime,m.Image.Fingerprint}}).ToArray();
            using(var sha=SHA256.Create())revision=Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(evidence))));
            return included;
        }
        internal static bool HasGroupCount(string text) {
            if(String.IsNullOrEmpty(text)||!Regex.IsMatch(text,@"^\([0-9]{1,6}\)$"))return false;
            int members;return Int32.TryParse(text.Substring(1,text.Length-2),out members)&&members>=2;
        }
        internal static bool TryMapVisibleText(int expectedId,string expectedText,int currentId,string currentText,NativeMessageReader.Row row,Dictionary<string,string> visibleText) {
            if(row==null||currentId!=expectedId||row.AccessibleId!=expectedId||currentText!=expectedText||String.IsNullOrEmpty(row.KeyTag))return false;
            // Quoted and rich-content messages can share ChatTextItemView with plain text.
            // Keep their native identity/type, but never treat their UI label as message text.
            if((row.Type==ReplyContext.TextType||row.Type==ReplyContext.RichTextType)&&expectedText!=null&&expectedText.Length<=2000)visibleText[row.KeyTag]=expectedText;
            return true;
        }
        internal static bool TryMapVisibleImage(int expectedId,string expectedText,int currentId,AutomationElement element,NativeMessageReader.Row row) {
            if(row==null||currentId!=expectedId||row.AccessibleId!=expectedId||String.IsNullOrEmpty(row.KeyTag)||
               row.Type!=3&&row.Type!=ReplyContext.VideoType)return false;
            var current=element.Current;
            return current.ClassName=="mmui::ChatImageItemView"&&!current.IsOffscreen&&current.Name==expectedText;
        }
        public static string InputName(AutomationElement input) { return Regex.Replace(input.Current.Name,@"\s*(?:按住鼠标\s*语音输入文字|长按鼠标\s*语音输入)\s*$",""); }
        public static bool ImeAllowed(Dictionary<string,object> ime,long layout) {
            object sampled;
            return Convert.ToBoolean(ime["valid"])&&!Convert.ToBoolean(ime["sampleStaleForTab"])&&Convert.ToString(ime["compositionState"])=="inactive_reported"&&ime.TryGetValue("keyboardLayoutValue",out sampled)&&Convert.ToInt64(sampled)==layout;
        }
    }
}
