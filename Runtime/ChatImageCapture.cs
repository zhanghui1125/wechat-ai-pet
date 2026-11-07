using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;

namespace WeChatPet.Runtime {
    // Captures only the content area of a visible WeChat image row. PrintWindow
    // deliberately excludes unrelated top-level windows such as the desktop pet.
    internal static class ChatImageCapture {
        const int MaxEncodedBytes = 3500000;
        const int ColorThreshold = 18;
        const int ContentMargin = 3;

        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect rect);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        internal static bool TryCapture(IntPtr window,AutomationElement element,out ChatImage image,out string reason) {
            image=null;reason=null;
            IntPtr oldContext=SetThreadDpiAwarenessContext(new IntPtr(-4));
            try {
                if(window==IntPtr.Zero||element==null){reason="image_window_unavailable";return false;}
                var current=element.Current;
                if(current.ClassName!="mmui::ChatImageItemView"||current.IsOffscreen){reason="image_row_unavailable";return false;}
                var item=current.BoundingRectangle;
                Rect target;
                if(!GetWindowRect(window,out target)){reason="image_window_bounds_unavailable";return false;}
                int windowWidth=target.Right-target.Left,windowHeight=target.Bottom-target.Top;
                var itemRect=Rectangle.FromLTRB((int)Math.Floor(item.Left),(int)Math.Floor(item.Top),(int)Math.Ceiling(item.Right),(int)Math.Ceiling(item.Bottom));
                var windowRect=Rectangle.FromLTRB(target.Left,target.Top,target.Right,target.Bottom);
                var visibleRect=Rectangle.Intersect(itemRect,windowRect);
                if(windowWidth<=0||windowHeight<=0||visibleRect.Width<20||visibleRect.Height<20){reason="image_row_outside_window";return false;}
                int itemX=visibleRect.Left-target.Left,itemY=visibleRect.Top-target.Top;
                int itemWidth=visibleRect.Width,itemHeight=visibleRect.Height;

                using(var screen=new Bitmap(windowWidth,windowHeight,PixelFormat.Format32bppArgb))
                using(var graphics=Graphics.FromImage(screen)) {
                    graphics.Clear(Color.White);
                    IntPtr dc=graphics.GetHdc();
                    bool printed;
                    try {printed=PrintWindow(window,dc,2);}
                    finally {graphics.ReleaseHdc(dc);}
                    if(!printed){reason="image_print_window_failed";return false;}

                    int inset=Math.Max(96,Math.Min(160,itemWidth/8));
                    if(itemWidth<=inset*2+24){reason="image_row_too_narrow";return false;}
                    Color background=screen.GetPixel(itemX+itemWidth-4,itemY+4);
                    Rectangle content;
                    if(!FindContent(screen,new Rectangle(itemX+inset,itemY+2,itemWidth-inset*2,itemHeight-4),background,out content)) {
                        reason="image_content_not_found";return false;
                    }
                    return TryEncodeContent(screen,content,new Rectangle(itemX,itemY,itemWidth,itemHeight),out image,out reason);
                }
            } catch {reason="image_capture_exception";return false;}
            finally {if(oldContext!=IntPtr.Zero)SetThreadDpiAwarenessContext(oldContext);}
        }

        internal static bool TryEncodeContent(Bitmap screen,Rectangle content,Rectangle bounds,out ChatImage image,out string reason) {
            image=null;reason=null;
            try {
                content.Inflate(ContentMargin,ContentMargin);
                content.Intersect(bounds);
                if(content.Width<20||content.Height<20){reason="image_content_too_small";return false;}
                using(var crop=screen.Clone(content,PixelFormat.Format32bppArgb))
                using(var normalized=NormalizeSize(crop)) {
                    byte[] bytes;
                    string mime;
                    if(!EncodePng(normalized,out bytes)){reason="image_encode_failed";return false;}
                    mime="image/png";
                    if(bytes.Length>MaxEncodedBytes) {
                        if(!EncodeJpeg(normalized,out bytes)){reason="image_encode_too_large";return false;}
                        mime="image/jpeg";
                    }
                    if(bytes.Length>MaxEncodedBytes){reason="image_too_large_after_compression";return false;}
                    using(var sha=SHA256.Create())image=new ChatImage{Mime=mime,Data=Convert.ToBase64String(bytes),Fingerprint=Convert.ToBase64String(sha.ComputeHash(bytes))};
                    return true;
                }
            } catch {reason="image_encode_exception";return false;}
        }

        internal static bool FindContent(Bitmap bitmap,Rectangle search,Color background,out Rectangle bounds) {
            bounds=Rectangle.Empty;
            BitmapData data=bitmap.LockBits(search,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try {
                int stride=Math.Abs(data.Stride),length=stride*search.Height;
                byte[] pixels=new byte[length];
                Marshal.Copy(data.Scan0,pixels,0,length);
                int minX=search.Width,minY=search.Height,maxX=-1,maxY=-1;
                for(int y=0;y<search.Height;y++) {
                    int row=y*stride;
                    for(int x=0;x<search.Width;x++) {
                        int offset=row+x*4;
                        int delta=Math.Max(Math.Abs(pixels[offset+2]-background.R),
                            Math.Max(Math.Abs(pixels[offset+1]-background.G),Math.Abs(pixels[offset]-background.B)));
                        if(delta<=ColorThreshold)continue;
                        if(x<minX)minX=x;if(x>maxX)maxX=x;
                        if(y<minY)minY=y;if(y>maxY)maxY=y;
                    }
                }
                if(maxX<minX||maxY<minY)return false;
                bounds=new Rectangle(search.Left+minX,search.Top+minY,maxX-minX+1,maxY-minY+1);
                return true;
            } finally {bitmap.UnlockBits(data);}
        }

        static Bitmap NormalizeSize(Bitmap source) {
            int longest=Math.Max(source.Width,source.Height);
            if(longest<80)return new Bitmap(source);
            double scale=longest<1000?Math.Min(2.5,1400.0/longest):1.0;
            if(scale<=1.01)return new Bitmap(source);
            int width=Math.Max(1,(int)Math.Round(source.Width*scale));
            int height=Math.Max(1,(int)Math.Round(source.Height*scale));
            var result=new Bitmap(width,height,PixelFormat.Format32bppArgb);
            using(var graphics=Graphics.FromImage(result)) {
                graphics.Clear(Color.White);
                graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;
                graphics.SmoothingMode=SmoothingMode.HighQuality;
                graphics.DrawImage(source,new Rectangle(0,0,width,height));
            }
            return result;
        }

        static bool EncodePng(Bitmap bitmap,out byte[] bytes) {
            bytes=null;
            try {using(var stream=new MemoryStream()) {bitmap.Save(stream,ImageFormat.Png);bytes=stream.ToArray();return bytes.Length>0;}}
            catch {return false;}
        }

        static bool EncodeJpeg(Bitmap bitmap,out byte[] bytes) {
            bytes=null;
            try {
                using(var stream=new MemoryStream()) {
                    ImageCodecInfo codec=null;
                    foreach(var item in ImageCodecInfo.GetImageEncoders())if(item.FormatID==ImageFormat.Jpeg.Guid){codec=item;break;}
                    if(codec==null)return false;
                    using(var parameters=new EncoderParameters(1)) {
                        parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,86L);
                        bitmap.Save(stream,codec,parameters);
                    }
                    bytes=stream.ToArray();return bytes.Length>0;
                }
            } catch {return false;}
        }
    }
}
