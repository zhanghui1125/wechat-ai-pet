using Accessibility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace WeChatPet.Runtime {
    internal static class WeComMsaa {
        const int RoleList=33,RoleListItem=34,RoleStaticText=41,RoleGrouping=20;
        [DllImport("oleacc.dll")]
        static extern int AccessibleObjectFromWindow(IntPtr window,uint objectId,ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
        [DllImport("oleacc.dll")]
        static extern int AccessibleChildren([MarshalAs(UnmanagedType.Interface)] IAccessible container,
            int start,int count,[Out] object[] children,out int obtained);

        internal sealed class Node {
            internal readonly IAccessible Accessible;internal readonly int ChildId;
            internal Node(IAccessible accessible,int childId){Accessible=accessible;ChildId=childId;}
            internal static Node Attach(IAccessible accessible,object child) {
                if(child is int)return new Node(accessible,(int)child);
                var value=child as IAccessible;
                return value==null?null:new Node(value,0);
            }
            internal IAccessible Container {
                get {
                    if(ChildId==0)return Accessible;
                    try{return Accessible.get_accChild(ChildId) as IAccessible;}catch{return null;}
                }
            }
            internal int Role {get{try{return Convert.ToInt32(Accessible.get_accRole(ChildId));}catch{return -1;}}}
            internal string Name {get{try{return Accessible.get_accName(ChildId)??"";}catch{return "";}}}
            internal string Value {get{try{return Accessible.get_accValue(ChildId)??"";}catch{return "";}}}
            internal Rectangle Bounds {
                get {
                    try {
                        int left,top,width,height;
                        Accessible.accLocation(out left,out top,out width,out height,ChildId);
                        return new Rectangle(left,top,width,height);
                    } catch{return Rectangle.Empty;}
                }
            }
            internal List<Node> Children() {
                var result=new List<Node>();
                IAccessible container=Container;
                if(container==null)return result;
                int count;
                try{count=container.accChildCount;}catch{return result;}
                if(count<=0||count>2000)return result;
                var values=new object[count];int obtained;
                try {if(AccessibleChildren(container,0,count,values,out obtained)!=0)return result;}
                catch{return result;}
                for(int i=0;i<obtained;i++){Node node=Attach(container,values[i]);if(node!=null)result.Add(node);}
                return result;
            }
        }

        internal sealed class Layout {
            internal Node ConversationList,MessageList,TitlePanel,Input;
        }

        internal sealed class Item {
            internal string Name;internal Rectangle Bounds;
        }

        internal static Node Root(IntPtr window) {
            var iid=new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            IAccessible value;
            if(AccessibleObjectFromWindow(window,0,ref iid,out value)!=0||value==null)
                throw new InvalidOperationException("wecom_accessibility_unavailable");
            return new Node(value,0);
        }

        internal static Layout Discover(IntPtr window) {
            Node root=Root(window);
            Win.ScreenRect physical;Win.PhysicalBounds(window,out physical);
            var screen=Rectangle.FromLTRB(physical.Left,physical.Top,physical.Right,physical.Bottom);
            var layout=new Layout();
            var queue=new Queue<KeyValuePair<Node,int>>();queue.Enqueue(new KeyValuePair<Node,int>(root,0));
            int visited=0;var trace=new StringBuilder();
            while(queue.Count>0&&visited++<3000) {
                var pair=queue.Dequeue();Node node=pair.Key;
                int role=node.Role;string name=node.Name;Rectangle bounds=node.Bounds;
                bool visible=bounds.Width>2&&bounds.Height>2&&screen.IntersectsWith(bounds);
                if(visited<=12)trace.Append("[d").Append(pair.Value).Append(' ').Append(role).Append(' ').Append(bounds).Append(' ').Append(visible?1:0).Append(' ').Append((name??"").Length>18?name.Substring(0,18):name).Append(']');
                if(role==RoleList&&name=="会话")layout.ConversationList=node;
                else if(role==RoleList&&name=="消息")layout.MessageList=node;
                else if(role==RoleGrouping&&name=="聊天信息")layout.TitlePanel=node;
                else if(role==22&&name=="消息发送")layout.Input=node;
                if(!visible||pair.Value>=48)continue;
                foreach(Node child in node.Children())queue.Enqueue(new KeyValuePair<Node,int>(child,pair.Value+1));
            }
            if(layout.MessageList==null||layout.Input==null)throw new InvalidOperationException(
                "wecom_controls_unavailable_visited_"+visited+"_conv_"+(layout.ConversationList==null?0:1)+
                "_msg_"+(layout.MessageList==null?0:1)+"_input_"+(layout.Input==null?0:1)+"_screen_"+screen.Width+"x"+screen.Height+"_"+trace);
            return layout;
        }

        internal static List<Item> VisibleListItems(Node list) {
            var result=new List<Item>();
            if(list==null)return result;
            foreach(Node child in list.Children()) {
                if(child.Role!=RoleListItem)continue;
                string name=child.Name;Rectangle bounds=child.Bounds;
                if(String.IsNullOrWhiteSpace(name)||bounds.Width<4||bounds.Height<4)continue;
                result.Add(new Item{Name=name,Bounds=bounds});
                if(result.Count>=200)break;
            }
            return result;
        }

        internal static string Title(Node panel) {
            if(panel==null)return null;
            var queue=new Queue<KeyValuePair<Node,int>>();queue.Enqueue(new KeyValuePair<Node,int>(panel,0));
            int visited=0;
            while(queue.Count>0&&visited++<300) {
                var pair=queue.Dequeue();Node node=pair.Key;
                string name=node.Name;Rectangle bounds=node.Bounds;
                bool visible=bounds.Width>2&&bounds.Height>2;
                if(visible&&node.Role==RoleStaticText&&!String.IsNullOrWhiteSpace(name)&&name!="聊天信息"&&name!="应用提醒")
                    return name.Trim();
                if(!visible||pair.Value>=12)continue;
                foreach(Node child in node.Children())queue.Enqueue(new KeyValuePair<Node,int>(child,pair.Value+1));
            }
            return null;
        }

        internal static bool IsGroup(Node panel) {
            if(panel==null)return false;
            var queue=new Queue<KeyValuePair<Node,int>>();queue.Enqueue(new KeyValuePair<Node,int>(panel,0));
            int visited=0;
            while(queue.Count>0&&visited++<400) {
                var pair=queue.Dequeue();Node node=pair.Key;
                string name=node.Name;Rectangle bounds=node.Bounds;
                bool visible=bounds.Width>2&&bounds.Height>2;
                if(visible&&(name=="群发消息"||name=="发公告"||name=="群工具"||name=="添加群机器人"||
                    Regex.IsMatch(name,@"^\([0-9]{1,6}\)$")))return true;
                if(!visible||pair.Value>=12)continue;
                foreach(Node child in node.Children())queue.Enqueue(new KeyValuePair<Node,int>(child,pair.Value+1));
            }
            return false;
        }
    }

    internal sealed class WeComScreenAnalysis {
        internal Dictionary<string,string> Directions=new Dictionary<string,string>();
        internal Dictionary<string,ChatImage> Images=new Dictionary<string,ChatImage>();
        internal bool DraftEmpty;
        internal string Reason;
    }

    internal static class WeComScreen {
        [StructLayout(LayoutKind.Sequential)]struct Rect {public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out Rect rect);
        [DllImport("user32.dll")]static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
        [DllImport("user32.dll")]static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        internal static WeComScreenAnalysis Analyze(IntPtr window,string session,IList<WeComMsaa.Item> rows,
            IList<string> keys,Rectangle inputBounds) {
            var result=new WeComScreenAnalysis{Reason="screen_unavailable",DraftEmpty=false};
            IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
            try {
                Rect bounds;
                if(!GetWindowRect(window,out bounds))return result;
                int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
                if(width<300||height<200)return result;
                using(var image=new Bitmap(width,height,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using(var graphics=Graphics.FromImage(image)) {
                    IntPtr dc=graphics.GetHdc();
                    bool printed;
                    try{printed=PrintWindow(window,dc,2);}finally{graphics.ReleaseHdc(dc);}
                    if(!printed){result.Reason="screen_print_failed";return result;}
                    var origin=new Rectangle(bounds.Left,bounds.Top,width,height);
                    for(int i=0;i<rows.Count;i++) {
                        Rectangle row=Rectangle.Intersect(rows[i].Bounds,origin);
                        if(row.Width<20||row.Height<20)continue;
                        row.Offset(-bounds.Left,-bounds.Top);
                        string direction=IsImageMessage(rows[i].Name)?"unknown":Direction(image,row);
                        if(IsImageMessage(rows[i].Name)) {
                            int topInset=Math.Max(8,row.Height/8);
                            Rectangle mediaRow=Rectangle.FromLTRB(row.Left+8,row.Top+topInset,row.Right-8,row.Bottom-8);
                            Color background=BackgroundColor(image,mediaRow);
                            int middle=mediaRow.Left+mediaRow.Width/2;
                            Rectangle leftHalf=Rectangle.FromLTRB(mediaRow.Left,mediaRow.Top,middle,mediaRow.Bottom);
                            Rectangle rightHalf=Rectangle.FromLTRB(middle,mediaRow.Top,mediaRow.Right,mediaRow.Bottom);
                            int leftScore=ContentScore(image,leftHalf,background);
                            int rightScore=ContentScore(image,rightHalf,background);
                            if(rightScore>leftScore*1.2) {
                                direction="self";mediaRow=rightHalf;
                            } else if(leftScore>rightScore*1.2) {
                                direction="peer";mediaRow=leftHalf;
                            }
                            Rectangle content;
                            if(ChatImageCapture.FindContent(image,mediaRow,background,out content)) {
                                ChatImage imageValue;string ignored;
                                if(ChatImageCapture.TryEncodeContent(image,content,row,out imageValue,out ignored)&&imageValue!=null)
                                    result.Images[keys[i]]=imageValue;
                            }
                        }
                        result.Directions[keys[i]]=direction;
                    }
                    Rectangle input=Rectangle.Intersect(inputBounds,origin);
                    if(input.Width<80||input.Height<20){result.Reason="input_unavailable";return result;}
                    int editorTop=input.Top-bounds.Top+72;
                    var editor=new Rectangle(input.Left-bounds.Left+24,editorTop,
                        Math.Max(1,input.Width-48),Math.Min(500,Math.Max(1,height-editorTop-120)));
                    editor.Intersect(new Rectangle(0,0,width,height));
                    result.DraftEmpty=EditorEmpty(image,editor);
                    result.Reason="ok";
                    return result;
                }
            } catch {result.Reason="screen_exception";return result;}
            finally {if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
        }

        static string Direction(Bitmap image,Rectangle row) {
            int left=row.Right,right=-1,count=0;
            int blueLeft=row.Right,blueRight=-1,blueCount=0;
            Color background=BackgroundColor(image,row);
            int startX=row.Left+8,endX=row.Right-8,startY=row.Top+5,endY=row.Bottom-5;
            for(int y=startY;y<endY;y+=2)for(int x=startX;x<endX;x+=2) {
                Color pixel=image.GetPixel(x,y);
                if(pixel.B-pixel.R>12&&pixel.B-pixel.G>3&&pixel.B>150) {
                    if(x<blueLeft)blueLeft=x;if(x>blueRight)blueRight=x;blueCount++;
                }
                int delta=Math.Max(Math.Abs(pixel.R-background.R),Math.Max(Math.Abs(pixel.G-background.G),Math.Abs(pixel.B-background.B)));
                if(delta<16)continue;
                if(x<left)left=x;if(x>right)right=x;count++;
            }
            if(blueCount>=20&&blueRight>=blueLeft) {
                double blueCenter=((blueLeft+blueRight)/2.0-row.Left)/row.Width;
                if(blueCenter>=0.55)return "self";
            }
            if(right<left||count<8)return "unknown";
            // WeCom's default incoming bubble is neutral while the outgoing bubble
            // is blue. Once no right-side outgoing bubble is present, a rendered
            // message row is incoming even if window chrome skews the bounding box.
            return "peer";
        }

        static Color BackgroundColor(Bitmap image,Rectangle row) {
            var counts=new Dictionary<int,int>();var samples=new Dictionary<int,Color>();
            int stepX=Math.Max(8,row.Width/40),stepY=Math.Max(8,row.Height/24);
            for(int y=row.Top;y<row.Bottom;y+=stepY)for(int x=row.Left;x<row.Right;x+=stepX) {
                Color color=image.GetPixel(x,y);
                int key=((color.R>>4)<<8)|((color.G>>4)<<4)|(color.B>>4);
                int count;counts.TryGetValue(key,out count);counts[key]=count+1;
                if(!samples.ContainsKey(key))samples[key]=Color.FromArgb(color.R,color.G,color.B);
            }
            int best=-1;foreach(var pair in counts)if(best<0||pair.Value>counts[best])best=pair.Key;
            return best<0?Color.White:samples[best];
        }

        static int ContentScore(Bitmap image,Rectangle area,Color background) {
            int count=0;
            for(int y=area.Top;y<area.Bottom;y+=3)for(int x=area.Left;x<area.Right;x+=3) {
                Color color=image.GetPixel(x,y);
                int delta=Math.Max(Math.Abs(color.R-background.R),Math.Max(Math.Abs(color.G-background.G),Math.Abs(color.B-background.B)));
                if(delta>=16)count++;
            }
            return count;
        }

        internal static bool IsImageMessage(string text) {
            string value=(text??"").Trim();
            return Regex.IsMatch(value,@"^[\[【(（]?\s*(?:图片|照片|image|photo)\s*[\]】)）]?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        }

        static bool EditorEmpty(Bitmap image,Rectangle editor) {
            int count=0;
            Color background=image.GetPixel(editor.Left+8,editor.Top+8);
            for(int y=editor.Top+8;y<editor.Bottom-8;y+=2)
                for(int x=editor.Left+8;x<editor.Right-8;x+=2) {
                    Color pixel=image.GetPixel(x,y);
                    int delta=Math.Max(Math.Abs(pixel.R-background.R),Math.Max(Math.Abs(pixel.G-background.G),Math.Abs(pixel.B-background.B)));
                    if(delta>38&&++count>12)return false;
                }
            return true;
        }
    }

    public sealed class WeComReader : IChatReader {
        readonly int pid;readonly long birth,windowValue;readonly IntPtr window;
        WeComMsaa.Layout layout;string rawAccount,accountTag;
        string screenSignature;DateTime screenAt,accountChecked;WeComScreenAnalysis screen;
        public AutomationElement Field {get{return null;}}
        public string UiDiagnostics {get;private set;}
        public WeComReader(int pid,long birth,long hwnd) {
            this.pid=pid;this.birth=birth;windowValue=hwnd;window=new IntPtr(hwnd);
            rawAccount=Account(pid);
            accountTag=NativeMessageReader.MemoryIds==null?HashHex("wecom-account:"+rawAccount):NativeMessageReader.MemoryIds.Account(rawAccount);
        }

        public ChatSnapshot Read(long activity) {
            var snapshot=new ChatSnapshot{Window=windowValue,Activity=activity,CapturedTicks=DateTime.UtcNow.Ticks};
            UiDiagnostics="nodes=0,title=0,messages=0,input=0,screen=0";
            if(!TargetIdentity.IsCurrent(pid,birth,windowValue)){snapshot.Reason="target_identity_changed";return snapshot;}
            if(!Win.IsWindowVisible(window)||Win.IsIconic(window)){snapshot.Reason="wechat_hidden_or_minimized";return snapshot;}
            try {
                if(rawAccount=="wecom:local"&&(DateTime.UtcNow-accountChecked).TotalSeconds>=3) {
                    accountChecked=DateTime.UtcNow;string resolved=Account(pid);
                    if(resolved!="wecom:local") {
                        rawAccount=resolved;
                        accountTag=NativeMessageReader.MemoryIds==null?HashHex("wecom-account:"+rawAccount):NativeMessageReader.MemoryIds.Account(rawAccount);
                    }
                }
                if(layout==null||layout.MessageList==null||layout.MessageList.Role!=33||layout.MessageList.Name!="消息")
                    layout=WeComMsaa.Discover(window);
                string contact=NormalizeTitle(WeComMsaa.Title(layout.TitlePanel));
                if(String.IsNullOrWhiteSpace(contact)){snapshot.Reason="wecom_title_unavailable";return snapshot;}
                bool group=WeComMsaa.IsGroup(layout.TitlePanel);
                var rows=WeComMsaa.VisibleListItems(layout.MessageList);
                var useful=new List<WeComMsaa.Item>();var keys=new List<string>();
                var occurrences=new Dictionary<string,int>();
                foreach(var row in rows) {
                    if(IsTimeSeparator(row.Name))continue;
                    string stable=contact+"|"+row.Name;
                    int occurrence;
                    occurrences.TryGetValue(stable,out occurrence);occurrences[stable]=occurrence+1;
                    stable+="|"+occurrence.ToString();
                    useful.Add(row);keys.Add(Key(stable));
                }
                string signature=contact+"|"+activity+"|"+String.Join("\n",useful.Select(r=>r.Name+"@"+r.Bounds.X+","+r.Bounds.Y+","+r.Bounds.Width+","+r.Bounds.Height));
                if(screen==null||signature!=screenSignature||(DateTime.UtcNow-screenAt).TotalMilliseconds>=800) {
                    screen=WeComScreen.Analyze(window,contact,useful,keys,layout.Input.Bounds);
                    screenSignature=signature;screenAt=DateTime.UtcNow;
                }
                var messages=new List<ChatMessage>();
                for(int i=0;i<useful.Count;i++) {
                    string direction;
                    if(!screen.Directions.TryGetValue(keys[i],out direction))direction="unknown";
                    bool typed=(direction=="peer"||direction=="self")&&!IsSystemMessage(useful[i].Name)&&!IsAutomatedContact(contact);
                    bool video=typed&&IsVideoMessage(useful[i].Name);
                    bool image=typed&&WeComScreen.IsImageMessage(useful[i].Name);
                    ChatImage picture;screen.Images.TryGetValue(keys[i],out picture);
                    string stable=contact+"|"+useful[i].Name+"|"+keys[i];
                    string memoryKey=NativeMessageReader.MemoryIds==null?null:NativeMessageReader.MemoryIds.Message(rawAccount,contact,stable);
                    string friendTag=NativeMessageReader.MemoryIds==null?HashHex("wecom-friend:"+rawAccount+"|"+contact):NativeMessageReader.MemoryIds.Friend(rawAccount,contact);
                    messages.Add(new ChatMessage{
                        Key=keys[i],MemoryKey=memoryKey,Type=video?ReplyContext.VideoType:image?ReplyContext.ImageType:typed?1u:0u,Role=direction,
                        SenderId=direction=="peer"?friendTag:direction=="self"?accountTag:null,
                        SenderName=direction=="peer"?contact:null,Text=video?"[视频消息]":image?"[图片消息]":useful[i].Name,Image=picture,
                        LocalId=(uint)(i+1),
                        CreatedTicks=DateTime.UtcNow.Ticks-(useful.Count-i-1)*TimeSpan.TicksPerSecond
                    });
                }
                int messageEnd=messages.Count;
                while(messageEnd>0&&IsSystemMessage(useful[messageEnd-1].Name))messageEnd--;
                if(messageEnd!=messages.Count) {
                    messages=messages.Take(messageEnd).ToList();keys=keys.Take(messageEnd).ToList();
                }
                snapshot.AccountId=accountTag;
                snapshot.GroupChat=group;
                snapshot.GroupId=group?(NativeMessageReader.MemoryIds==null?HashHex("wecom-group:"+rawAccount+"|"+contact):NativeMessageReader.MemoryIds.Group(rawAccount,contact)):null;
                snapshot.FriendId=group?null:(NativeMessageReader.MemoryIds==null?HashHex("wecom-friend:"+rawAccount+"|"+contact):NativeMessageReader.MemoryIds.Friend(rawAccount,contact));
                snapshot.Contact=contact;
                snapshot.Session=Tag("wecom-session:"+rawAccount+"|"+contact);
                snapshot.InputIdentity=contact+"|wecom-input";
                snapshot.ViewIdentity=accountTag+"|"+contact;
                snapshot.Keys=keys.ToArray();
                snapshot.Messages=messages;
                snapshot.Tail=snapshot.Keys.Length==0?null:snapshot.Keys[snapshot.Keys.Length-1];
                snapshot.ContentRevision=Revision(keys,messages);
                snapshot.AtEnd=true;
                snapshot.DraftEmpty=screen.DraftEmpty;
                snapshot.DraftConsistent=screen.Reason=="ok";
                snapshot.Foreground=Win.GetForegroundWindow()==window;
                snapshot.Focused=snapshot.Foreground;
                uint owner;snapshot.Layout=Win.GetKeyboardLayout(Win.GetWindowThreadProcessId(window,out owner)).ToInt64();
                snapshot.ImeAllowed=snapshot.Foreground;
                snapshot.Composing=false;
                Win.ScreenRect input=layout.Input.BoundsAsScreenRect();Win.ScreenRect windowBounds;
                if(Win.PhysicalBounds(window,out windowBounds)) {
                    snapshot.InputLeft=input.Left;snapshot.InputTop=input.Top;snapshot.InputWidth=input.Right-input.Left;snapshot.InputHeight=input.Bottom-input.Top;
                    snapshot.WindowLeft=windowBounds.Left;snapshot.WindowTop=windowBounds.Top;snapshot.WindowWidth=windowBounds.Right-windowBounds.Left;snapshot.WindowHeight=windowBounds.Bottom-windowBounds.Top;
                }
                if(group){snapshot.Reason="wecom_group_not_supported";snapshot.Available=false;}
                else {snapshot.Available=true;snapshot.Reason=messages.Count==0?"empty_chat":"ok";}
                UiDiagnostics="title=1,messages="+messages.Count+",input=1,screen="+screen.Reason+",draftEmpty="+snapshot.DraftEmpty;
                snapshot.CapturedTicks=DateTime.UtcNow.Ticks;
                return snapshot;
            } catch(Exception error) {
                layout=null;
                string reason=String.IsNullOrWhiteSpace(error.Message)?error.GetType().Name:error.Message;
                snapshot.Reason="wecom_"+reason.Replace('\n','_').Replace('\r','_');return snapshot;
            }
        }

        internal bool FocusInput() {
            if(layout==null||layout.Input==null||Win.GetForegroundWindow()!=window)return false;
            Rectangle bounds=layout.Input.Bounds;
            if(bounds.Width<50||bounds.Height<20)return false;
            int x=bounds.Left+Math.Min(700,bounds.Width/2),y=bounds.Top+bounds.Height+90;
            Win.Click(window,x,y);
            return true;
        }
        internal void InvalidateScreen(){screenSignature=null;screenAt=DateTime.MinValue;screen=null;}

        static bool IsTimeSeparator(string text) {
            string value=(text??"").Trim();
            return Regex.IsMatch(value,@"^(?:今天|昨天|前天|星期[一二三四五六日天])?\s*\d{1,2}:\d{2}$")||
                Regex.IsMatch(value,@"^\d{1,2}月\d{1,2}日(?:\s+\d{1,2}:\d{2})?$");
        }
        static bool IsVideoMessage(string text) {
            string value=(text??"").Trim();
            return Regex.IsMatch(value,@"^(?:[\[【(（]?\s*[Vv]ideo\s*[\]】)）]?|[\[【(（]?\s*视频(?:消息)?\s*[\]】)）]?)(?:\s*\d{1,2}:\d{2})?$",
                RegexOptions.CultureInvariant);
        }
        static bool IsSystemMessage(string text) {
            string value=(text??"").Trim();
            return value.StartsWith("我通过了你的联系人验证请求",StringComparison.Ordinal)||
                value=="你已添加了对方，现在可以开始聊天了"||value=="以上是打招呼的内容"||
                value.EndsWith("撤回了一条消息",StringComparison.Ordinal)||
                value.Contains("由于工作变更")||value.Contains("将在24小时后添加为你的企业微信联系人")||
                (value.Contains("接替")&&value.Contains("继续为你提供"))||
                value.StartsWith("已将",StringComparison.Ordinal)&&value.Contains("移出群聊")||
                value.StartsWith("邀请",StringComparison.Ordinal)&&value.Contains("加入了群聊");
        }
        static bool IsAutomatedContact(string contact) {
            return contact=="文件传输助手"||contact=="企业微信团队"||contact=="行业资讯"||contact.StartsWith("企业微信团队 ",StringComparison.Ordinal);
        }
        static string NormalizeTitle(string value) {
            if(String.IsNullOrWhiteSpace(value))return null;
            return Regex.Replace(value.Trim(),@"\s+@","@");
        }
        static string Key(string value){return Tag(value);}
        static string Tag(string value){using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));}
        static string HashHex(string value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
        static string Revision(IList<string> keys,IList<ChatMessage> messages) {
            var text=new StringBuilder();
            for(int i=0;i<keys.Count;i++) {
                ChatImage image=messages[i].Image;
                text.Append(keys[i]).Append('|').Append(messages[i].Role).Append('|').Append(messages[i].Text)
                    .Append('|').Append(image==null?null:image.Fingerprint).Append('\n');
            }
            using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }
        static string Account(int rootPid) {
            try {
                var rows=new List<Proc>();var parents=new Dictionary<int,int>();
                using(var searcher=new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,CommandLine FROM Win32_Process WHERE Name='FlutterPlugins.exe' OR Name='WXWork.exe'")) {
                    foreach(ManagementObject item in searcher.Get()) {
                        int id=Convert.ToInt32(item["ProcessId"]),parent=Convert.ToInt32(item["ParentProcessId"]);
                        string command=Convert.ToString(item["CommandLine"]);
                        parents[id]=parent;rows.Add(new Proc{Id=id,Parent=parent,Command=command});
                    }
                }
                foreach(var row in rows) {
                    Match match=Regex.Match(row.Command??"","(?:^|\\s)--uin=(\\d+)");
                    if(!match.Success||!Descends(row.Id,rootPid,parents))continue;
                    return "wecom:"+match.Groups[1].Value;
                }
            } catch {}
            return "wecom:local";
        }
        static bool Descends(int pid,int root,Dictionary<int,int> parents) {
            for(int i=0;i<8&&pid>0;i++) {if(pid==root)return true;if(!parents.TryGetValue(pid,out pid))return false;}
            return false;
        }
        sealed class Proc {public int Id,Parent;public string Command;}
    }

    internal static class WeComNodeBounds {
        internal static Win.ScreenRect BoundsAsScreenRect(this WeComMsaa.Node node) {
            Rectangle value=node.Bounds;
            return new Win.ScreenRect{Left=value.Left,Top=value.Top,Right=value.Right,Bottom=value.Bottom};
        }
    }
}
