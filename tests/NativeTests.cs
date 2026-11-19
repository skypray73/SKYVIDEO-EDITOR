using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SkyVideoEditor;

class TestSlider : SeekTrackBar {
    public void Pointer(int message,int x,int y){var m=Message.Create(Handle,message,IntPtr.Zero,new IntPtr((y<<16)|(x&65535)));base.WndProc(ref m);}
    public void Key(int key){var m=Message.Create(Handle,0x100,new IntPtr(key),IntPtr.Zero);base.WndProc(ref m);}
    [StructLayout(LayoutKind.Sequential)] public struct Rect {public int L,T,R,B;}
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr w,ref Rect r);
    public double ThumbCenter(){var r=new Rect();SendMessage(Handle,0x419,IntPtr.Zero,ref r);return (r.L+r.R-1)/2.0;}
}
class NativeTests {
    static int checks;
    static void Assert(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static T Get<T>(object o,string name){return (T)o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
    static void Set(object o,string name,object value){o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(o,value);}
    static void Call(object o,string name,params object[] args){o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(o,args);}
    static string Order(List<Clip> c){return string.Join("",c.Select(x=>x.Name));}
    [STAThread] static int Main(string[] args){try{
        Application.EnableVisualStyles();
        using(var form=new MainForm()){
            var handle=form.Handle;var clips=Get<List<Clip>>(form,"clips");var list=Get<ListBox>(form,"list");
            var top=Get<Button>(form,"moveTop");var bottom=Get<Button>(form,"moveBottom");var timeline=Get<TrackBar>(form,"timeline");
            var mode=Get<ComboBox>(form,"exportMode");var codec=Get<ComboBox>(form,"exportCodec");
            var container=Get<ComboBox>(form,"exportContainer");var help=Get<Label>(form,"exportHelp");
            Assert(mode.SelectedIndex==0&&codec.SelectedIndex==0&&container.SelectedIndex==0,"Default output settings");
            codec.SelectedIndex=1;Assert(help.Text.Contains("H.265")&&help.Text.Contains("MKV"),"HEVC and source-format help");
            mode.SelectedIndex=1;Assert(!codec.Enabled&&help.Text.Contains("關鍵影格"),"Stream copy disables codec choice");
            container.SelectedIndex=1;Assert(help.Text.Contains("輸出為 MP4"),"Explicit container help");
            mode.SelectedIndex=0;Assert(codec.Enabled&&codec.SelectedIndex==1,"Re-encode restores the prior codec choice");
            Assert(!top.Enabled&&!bottom.Enabled,"Empty list edge buttons");
            foreach(char c in "ABCDE")clips.Add(new Clip{Name=c.ToString(),Duration=100,End=100});
            Call(form,"RefreshClipList",new Clip[]{clips[1],clips[3]},clips[1]);timeline.Value=33000;
            var active=clips[1];Call(form,"MoveToEdge",false);
            Assert(Order(clips)=="BDACE","Noncontiguous top order");
            Assert(list.SelectedIndices.Cast<int>().SequenceEqual(new[]{0,1}),"Top selection retained");
            Assert(Get<int>(form,"selected")==0&&timeline.Value==33000,"Active clip/playhead retained");
            Assert(!top.Enabled&&bottom.Enabled,"Top edge button states");
            Call(form,"MoveToEdge",true);Assert(Order(clips)=="ACEBD","Multi bottom order");
            Assert(list.SelectedIndices.Cast<int>().SequenceEqual(new[]{3,4}),"Bottom selection retained");
            Assert(Get<int>(form,"selected")==3&&timeline.Value==33000,"Bottom playhead retained");
            Assert(top.Enabled&&!bottom.Enabled,"Bottom edge button states");
            Call(form,"SetBusy",true,"test");Call(form,"MoveToEdge",false);
            Assert(Order(clips)=="ACEBD"&&!top.Enabled&&!bottom.Enabled&&!timeline.Enabled,"Busy protection");
            Assert(!mode.Enabled&&!codec.Enabled&&!container.Enabled,"Output settings are locked during processing");
            Call(form,"SetBusy",false,"test");
            Call(form,"RefreshClipList",new Clip[]{active},active);Call(form,"MoveToEdge",false);
            Assert(Order(clips)=="BACED","Single top");Call(form,"MoveToEdge",true);Assert(Order(clips)=="ACEDB","Single bottom");
            Call(form,"SelectAllClips");Assert(!top.Enabled&&!bottom.Enabled,"All selected no-op");
            Call(form,"MoveToEdge",false);Assert(Order(clips)=="ACEDB","All selected order");
            Call(form,"RefreshClipList",new Clip[0],null);Assert(!top.Enabled&&!bottom.Enabled,"Deselected states");
        }
        foreach(int width in new[]{360,450,540,720})using(var t=new TestSlider{Width=width,Minimum=0,Maximum=100000}){
            int events=0;t.Scroll+=(s,e)=>events++;var h=t.Handle;
            t.Pointer(0x201,width/4,t.Height-1);
            Assert(Math.Abs(t.ThumbCenter()-width/4)<=1.1,"Native thumb matches click geometry at width "+width);
            Assert(events==1,"One seek per click");
            t.Pointer(0x200,width*3/4,0);Assert(Math.Abs(t.ThumbCenter()-width*3/4)<=1.1,"Whole area drag");
            t.Pointer(0x200,-50,0);Assert(t.Value==0,"Left clamp outside control");
            t.Pointer(0x202,width+50,0);Assert(t.Value==100000&&!t.Capture,"Right clamp and release");
            t.Pointer(0x200,width/2,0);Assert(t.Value==100000,"No dragging after release");
            t.Key((int)Keys.Left);Assert(t.Value<100000,"Native keyboard navigation retained");
            t.Pointer(0x201,width/2,0);t.Capture=false;int saved=t.Value;
            t.Pointer(0x200,20,0);Assert(t.Value==saved,"Capture loss ends drag");
            t.Enabled=false;t.Pointer(0x201,0,0);Assert(t.Value==saved,"Disabled slider ignores input");
        }
        if(args.Length==2)using(var form=new MainForm()){
            var h=form.Handle;Set(form,"ffmpeg",args[0]);Call(form,"UpdateFfmpegLabel");
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();Application.DoEvents();
            var c=(Clip)typeof(MainForm).GetMethod("Probe",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{args[1]});
            c.Start=c.Duration*.1;c.End=c.Duration*.9;double start=c.Start,end=c.End;
            Get<List<Clip>>(form,"clips").Add(c);Call(form,"RefreshClipList",new[]{c},null);
            var slider=Get<TrackBar>(form,"timeline");slider.Value=60000;Call(form,"TimelineChanged");
            Get<Timer>(form,"previewTimer").Stop();Call(form,"RenderPreview");
            var clock=System.Diagnostics.Stopwatch.StartNew();var preview=Get<PictureBox>(form,"preview");
            while(preview.Image==null&&clock.ElapsedMilliseconds<15000){Application.DoEvents();System.Threading.Thread.Sleep(10);}
            Assert(preview.Image!=null,"Real FFmpeg frame rendering");
            Assert(c.Start==start&&c.End==end&&slider.Value==60000,"Seeking preserves trim and playhead");
            var top=Get<Button>(form,"moveTop");var bottom=Get<Button>(form,"moveBottom");
            Assert(top.Bottom<=top.Parent.ClientSize.Height&&bottom.Bottom<=bottom.Parent.ClientSize.Height,"Edge buttons fit wrapped toolbar");
            Assert(top.Right<=top.Parent.ClientSize.Width&&bottom.Right<=bottom.Parent.ClientSize.Width,"Edge buttons fit toolbar width");
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,form.Width,form.Height));image.Save("work/preview-integration.png");}
            Console.WriteLine("Real-video preview PASS; screenshot: work/preview-integration.png");
        }
        Console.WriteLine("PASS: "+checks+" native control/regression checks");return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
