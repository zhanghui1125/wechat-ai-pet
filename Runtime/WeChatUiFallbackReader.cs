using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace WeChatPet.Runtime {
    // Version-independent fallback built on WeChat's accessibility tree. It is
    // deliberately conservative: if a bubble side cannot be established, that
    // row is never promoted to a peer message eligible for automatic reply.
    internal sealed class WeChatUiFallbackReader {
        const int MaxVisibleMessages = 60;
        const int MaxCachedRows = 600;
        const int MaxTextLength = 2000;
        readonly Dictionary<string,string> roles = new Dictionary<string,string>();
        readonly Dictionary<string,long> firstSeen = new Dictionary<string,long>();
        readonly Dictionary<string,ChatImage> images = new Dictionary<string,ChatImage>();
        string listIdentity;

        internal AutomationElement Field { get; private set; }

        internal ChatSnapshot Read(
            ChatSnapshot snapshot,
            IntPtr window,
            AutomationElement title,
            AutomationElement input,
            AutomationElement list,
            AutomationElement groupCount,
            string contact,
            bool group,
            string draft,
            string document) {
            Field=input;
            int[] listId=list.GetRuntimeId();
            string listTag=String.Join(".",listId);
            string identity=contact+"|"+listTag;
            if(!String.Equals(identity,listIdentity,StringComparison.Ordinal)) {
                listIdentity=identity;roles.Clear();firstSeen.Clear();images.Clear();
            }

            var records=new List<UiRow>();
            var walker=TreeWalker.RawViewWalker;
            int inspected=0;
            for(var element=walker.GetFirstChild(list);element!=null&&inspected++<MaxVisibleMessages;element=walker.GetNextSibling(element)) {
                UiRow row=UiRow.Create(element,contact);
                if(row!=null)records.Add(row);
            }
            if(inspected>=MaxVisibleMessages) {
                snapshot.Reason="ui_message_row_limit";
                return snapshot;
            }

            bool needRoles=records.Any(r=>!r.System&&!roles.ContainsKey(r.Key));
            Bitmap screen=needRoles?Capture(window):null;
            try {
                foreach(var row in records.Where(r=>!r.System&&!roles.ContainsKey(r.Key))) {
                    string role=screen==null?null:ClassifyRole(screen,window,row.Element,row.Image);
                    if(role!=null)roles[row.Key]=role;
                    Trim(roles,MaxCachedRows);
                }
            } finally {
                if(screen!=null)screen.Dispose();
            }

            long now=DateTime.UtcNow.Ticks;
            var messages=new List<ChatMessage>();
            foreach(var row in records) {
                if(!firstSeen.ContainsKey(row.Key))firstSeen[row.Key]=now;
                string role;
                roles.TryGetValue(row.Key,out role);
                ChatImage image=null;
                if(row.Image&&row.Type==ReplyContext.ImageType) {
                    if(!images.TryGetValue(row.Key,out image)) {
                        string ignored;
                        ChatImageCapture.TryCapture(window,row.Element,out image,out ignored);
                        if(image!=null) {
                            images[row.Key]=image;
                            Trim(images,12);
                        }
                    }
                }
                messages.Add(new ChatMessage {
                    Key=row.Key,Text=row.Text,Type=row.Type,Role=row.System?null:role,
                    LocalId=StableId(row.Key),CreatedTicks=firstSeen[row.Key],Image=image
                });
            }
            Trim(firstSeen,MaxCachedRows);

            int end=messages.Count;
            while(end>0&&ReplyContext.IsSystemNotice(messages[end-1].Type))end--;
            if(end!=messages.Count)messages=messages.Take(end).ToList();

            snapshot.Messages=messages;
            snapshot.Keys=messages.Select(m=>m.Key).ToArray();
            snapshot.Tail=snapshot.Keys.Length==0?null:snapshot.Keys[snapshot.Keys.Length-1];
            snapshot.Session=Hash("ui-session:"+identity);
            snapshot.Contact=contact;
            snapshot.GroupChat=group;
            snapshot.InputIdentity=contact+"|"+String.Join(".",input.GetRuntimeId());
            snapshot.ViewIdentity=snapshot.InputIdentity+"|"+listTag;
            snapshot.ContentRevision=Revision(messages);

            if(String.Join(".",list.GetRuntimeId())!=listTag||contact!=title.Current.Name||
               contact!=WeChatReader.InputName(input)) {
                snapshot.Reason="ui_chat_controls_changed";
                return snapshot;
            }

            var inputBounds=input.Current.BoundingRectangle;
            Win.ScreenRect windowBounds;
            if(!inputBounds.IsEmpty&&Win.PhysicalBounds(window,out windowBounds)) {
                snapshot.InputLeft=inputBounds.Left;snapshot.InputTop=inputBounds.Top;
                snapshot.InputWidth=inputBounds.Width;snapshot.InputHeight=inputBounds.Height;
                snapshot.WindowLeft=windowBounds.Left;snapshot.WindowTop=windowBounds.Top;
                snapshot.WindowWidth=windowBounds.Right-windowBounds.Left;snapshot.WindowHeight=windowBounds.Bottom-windowBounds.Top;
            }

            snapshot.AtEnd=AtEnd(list);
            snapshot.DraftConsistent=draft==document;
            snapshot.DraftEmpty=String.IsNullOrWhiteSpace(draft)&&snapshot.DraftConsistent&&
                input.Current.IsEnabled&&!input.Current.IsOffscreen;
            snapshot.Foreground=Win.GetForegroundWindow()==window;
            snapshot.Focused=input.Current.HasKeyboardFocus;
            uint owner;
            snapshot.Layout=Win.GetKeyboardLayout(Win.GetWindowThreadProcessId(window,out owner)).ToInt64();
            snapshot.Composing=false;
            snapshot.ImeAllowed=snapshot.Foreground&&snapshot.Focused&&snapshot.DraftConsistent;
            snapshot.Available=true;
            snapshot.Reason=messages.Count==0?"empty_chat":"ok";
            snapshot.CapturedTicks=DateTime.UtcNow.Ticks;

            return snapshot;
        }

        sealed class UiRow {
            internal AutomationElement Element;
            internal string Key,Text;
            internal uint Type;
            internal bool Image,System;

            internal static UiRow Create(AutomationElement element,string contact) {
                string className=element.Current.ClassName;
                if(String.IsNullOrEmpty(className)||!className.StartsWith("mmui::Chat",StringComparison.Ordinal)||
                   !className.EndsWith("ItemView",StringComparison.Ordinal))return null;
                int[] runtimeId=element.GetRuntimeId();
                if(runtimeId==null||runtimeId.Length==0)return null;
                string name=element.Current.Name??"";
                uint type;
                bool system=false,image=false;
                if(className=="mmui::ChatTextItemView")type=ReplyContext.TextType;
                else if(className=="mmui::ChatImageItemView") {
                    type=name.IndexOf("视频",StringComparison.Ordinal)>=0?ReplyContext.VideoType:ReplyContext.ImageType;
                    image=true;
                } else if(className=="mmui::ChatItemView") {
                    type=10000;
                    system=true;
                } else type=0;
                string text=type==ReplyContext.ImageType?"[图片消息]":
                    type==ReplyContext.VideoType?"[视频消息]":name;
                if(text.Length>MaxTextLength)text="";
                string tag=contact+"|"+String.Join(".",runtimeId)+"|"+className;
                return new UiRow{Element=element,Key=Hash(tag),Text=text,Type=type,Image=image,System=system};
            }
        }

        static Bitmap Capture(IntPtr window) {
            IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
            try {
                Win.ScreenRect bounds;
                if(!Win.PhysicalBounds(window,out bounds))return null;
                int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
                if(width<300||height<250||width>10000||height>10000)return null;
                var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);
                using(var graphics=Graphics.FromImage(bitmap)) {
                    graphics.Clear(Color.White);
                    IntPtr dc=graphics.GetHdc();
                    bool printed;
                    try{printed=PrintWindow(window,dc,2);}finally{graphics.ReleaseHdc(dc);}
                    if(printed)return bitmap;
                }
                bitmap.Dispose();
                return null;
            } catch {
                return null;
            } finally {
                if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);
            }
        }

        static string ClassifyRole(Bitmap screen,IntPtr window,AutomationElement element,bool image) {
            try {
                Win.ScreenRect windowBounds;
                if(!Win.PhysicalBounds(window,out windowBounds))return null;
                var bounds=element.Current.BoundingRectangle;
                int left=Math.Max(0,(int)Math.Floor(bounds.Left)-windowBounds.Left);
                int top=Math.Max(0,(int)Math.Floor(bounds.Top)-windowBounds.Top);
                int right=Math.Min(screen.Width,(int)Math.Ceiling(bounds.Right)-windowBounds.Left);
                int bottom=Math.Min(screen.Height,(int)Math.Ceiling(bounds.Bottom)-windowBounds.Top);
                int width=right-left,height=bottom-top;
                if(width<30||height<14)return null;
                int step=Math.Max(2,width/260);
                int ink=0,greenLeft=0,greenRight=0,minX=width,maxX=-1;
                long weightedX=0;
                for(int y=Math.Max(4,height/12);y<height-Math.Max(4,height/12);y+=step) {
                    for(int x=Math.Max(4,width/50);x<Math.Min(width-4,width*49/50);x+=step) {
                        Color pixel=screen.GetPixel(left+x,top+y);
                        int brightest=Math.Max(pixel.R,Math.Max(pixel.G,pixel.B));
                        int darkest=Math.Min(pixel.R,Math.Min(pixel.G,pixel.B));
                        int brightness=(pixel.R+pixel.G+pixel.B)/3;
                        bool green=pixel.G>145&&pixel.G>pixel.R+20&&pixel.G>pixel.B+35;
                        if(brightness<190||brightest-darkest>42||green) {
                            ink++;weightedX+=x;
                            if(x<minX)minX=x;if(x>maxX)maxX=x;
                            if(green) {
                                if(x<width/2)greenLeft++;else greenRight++;
                            }
                        }
                    }
                }
                if(ink<8)return null;
                double center=weightedX/(double)ink/width;
                int shortEdge=Math.Max(40,image?width/7:width/12);
                if(!image&&greenRight>=shortEdge&&greenRight>greenLeft*2)return "self";
                if(!image&&greenLeft>=shortEdge&&greenLeft>greenRight*2)return "peer";
                if(center<0.47)return "peer";
                if(center>0.53)return "self";
                if(maxX<width*0.78)return "peer";
                if(minX>width*0.22)return "self";
                return null;
            } catch {
                return null;
            }
        }

        static bool AtEnd(AutomationElement list) {
            try {
                var top=list;
                var parent=TreeWalker.RawViewWalker.GetParent(top);
                for(int depth=0;parent!=null&&depth<12;depth++) {
                    top=parent;parent=TreeWalker.RawViewWalker.GetParent(top);
                }
                var unread=top.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty,"mmui::UnreadBarView"));
                foreach(AutomationElement item in unread)
                    if(!item.Current.IsOffscreen)return false;
            } catch {}
            object pattern;
            try {
                if(list.TryGetCurrentPattern(ScrollPattern.Pattern,out pattern)) {
                    var scroll=(ScrollPattern)pattern;
                    if(!scroll.Current.VerticallyScrollable)return true;
                    double percent=scroll.Current.VerticalScrollPercent;
                    return percent!=ScrollPattern.NoScroll&&percent>=99.0;
                }
            } catch {}
            try {
                var parent=TreeWalker.RawViewWalker.GetParent(list);
                if(parent==null)return true;
                var scrollBars=parent.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ScrollBar));
                foreach(AutomationElement bar in scrollBars) {
                    if(bar.Current.Orientation!=OrientationType.Vertical)continue;
                    object valuePattern;
                    if(bar.TryGetCurrentPattern(RangeValuePattern.Pattern,out valuePattern)) {
                        var value=(RangeValuePattern)valuePattern;
                        if(value.Current.Maximum<=value.Current.Minimum)return true;
                        return value.Current.Value>=value.Current.Maximum-0.5;
                    }
                }
            } catch {}
            return true;
        }

        static string Revision(List<ChatMessage> messages) {
            object evidence=messages.Select(m=>new {
                m.Key,m.Type,m.Role,m.LocalId,m.CreatedTicks,m.Text,
                image=m.Image==null?null:new{m.Image.Mime,m.Image.Fingerprint}
            }).ToArray();
            return Hash(new JavaScriptSerializer().Serialize(evidence));
        }

        static uint StableId(string value) {
            uint hash=2166136261;
            foreach(char c in value??"") {hash^=c;hash*=16777619;}
            return hash==0?1:hash;
        }

        static string Hash(string value) {
            using(var sha=SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value??"")));
        }

        static void Trim<T>(Dictionary<string,T> values,int limit) {
            if(values.Count<=limit)return;
            foreach(string key in values.Keys.Take(values.Count-limit).ToArray())values.Remove(key);
        }

        [DllImport("user32.dll")]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    }
}
