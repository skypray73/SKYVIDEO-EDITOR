using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyVersion("1.9.1.0")]
[assembly: AssemblyFileVersion("1.9.1.0")]
namespace SkyVideoEditor {
    class Clip {
        public string Path; public string Name; public double Duration, Start, End;
        public double Length { get { return Math.Max(0, End - Start); } }
        public override string ToString() { return Name + "    " + Fmt(Start) + " → " + Fmt(End) + "    (" + Fmt(Length) + ")"; }
        public static string Fmt(double v) { v=Math.Max(0,v); int h=(int)(v/3600), m=(int)(v%3600/60); return string.Format(CultureInfo.InvariantCulture,"{0:00}:{1:00}:{2:00.000}",h,m,v%60); }
        public static double Parse(string text) {
            string[] p=text.Trim().Replace(',', '.').Split(':'); double x;
            if(p.Length==1) x=double.Parse(p[0],CultureInfo.InvariantCulture);
            else if(p.Length==2) x=double.Parse(p[0],CultureInfo.InvariantCulture)*60+double.Parse(p[1],CultureInfo.InvariantCulture);
            else if(p.Length==3) x=double.Parse(p[0],CultureInfo.InvariantCulture)*3600+double.Parse(p[1],CultureInfo.InvariantCulture)*60+double.Parse(p[2],CultureInfo.InvariantCulture);
            else throw new Exception("時間格式不正確");
            if(x<0) throw new Exception("時間不可小於 0"); return x;
        }
    }

    class ClipListBox : ListBox {
        bool preservingSelection;
        protected override void WndProc(ref Message m){
            if(m.Msg==0x0201){ // WM_LBUTTONDOWN: keep a selected group intact when starting a drag.
                int point=m.LParam.ToInt32();var at=new Point((short)(point&0xffff),(short)((point>>16)&0xffff));int index=IndexFromPoint(at);
                preservingSelection=SelectedIndices.Count>1&&index>=0&&GetSelected(index)&&(ModifierKeys&(Keys.Control|Keys.Shift))==Keys.None;
                if(preservingSelection){Focus();Capture=true;OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,at.X,at.Y,0));return;}
            }
            if(m.Msg==0x0202&&preservingSelection){int point=m.LParam.ToInt32();preservingSelection=false;Capture=false;OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,(short)(point&0xffff),(short)((point>>16)&0xffff),0));return;}
            base.WndProc(ref m);
        }
    }

    // Use native channel/thumb geometry so click-to-seek follows the visible slider at any DPI.
    class SeekTrackBar : TrackBar {
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr wParam,ref NativeRect rect);
        bool dragging;
        void SeekAt(int x){
            var channel=new NativeRect();var thumb=new NativeRect();
            SendMessage(Handle,0x041A,IntPtr.Zero,ref channel); // TBM_GETCHANNELRECT
            SendMessage(Handle,0x0419,IntPtr.Zero,ref thumb);   // TBM_GETTHUMBRECT
            double left=channel.Left+(thumb.Right-thumb.Left-1)/2.0;
            double right=channel.Right-(thumb.Right-thumb.Left+1)/2.0;
            if(right<=left)return;
            double fraction=Math.Max(0,Math.Min(1,(x-left)/(right-left)));
            int target=Minimum+(int)Math.Round(fraction*((double)Maximum-Minimum));
            if(Value!=target){Value=target;OnScroll(EventArgs.Empty);}
        }
        protected override void WndProc(ref Message m){
            // Intercept before the native trackbar starts page-step/repeat behaviour.
            if(Enabled&&(m.Msg==0x0201||m.Msg==0x0203)){
                Focus();dragging=true;Capture=true;SeekAt((short)(m.LParam.ToInt64()&0xffff));return;
            }
            if(dragging&&m.Msg==0x0200){SeekAt((short)(m.LParam.ToInt64()&0xffff));return;}
            if(dragging&&m.Msg==0x0202){SeekAt((short)(m.LParam.ToInt64()&0xffff));dragging=false;Capture=false;return;}
            base.WndProc(ref m);
        }
        protected override void OnMouseCaptureChanged(EventArgs e){if(!Capture)dragging=false;base.OnMouseCaptureChanged(e);}
        protected override void OnEnabledChanged(EventArgs e){if(!Enabled){dragging=false;Capture=false;}base.OnEnabledChanged(e);}
    }

    class MainForm : Form {
        enum OutputContainer { MatchSource, Mp4, Mkv }
        enum OutputCodec { H264, H265 }
        enum OutputMode { Reencode, StreamCopy }

        readonly ComboBox exportMode=new ComboBox(), exportCodec=new ComboBox(), exportContainer=new ComboBox();
        readonly Label exportHelp=new Label();
        readonly Button chooseFfmpeg=new Button{Text="FFmpeg 設定",AutoSize=true};
        List<Clip> clips=new List<Clip>(); ListBox list=new ClipListBox(); TrackBar timeline=new SeekTrackBar(); PictureBox preview=new PictureBox(); Timer previewTimer=new Timer(); ToolTip tips=new ToolTip();
        TextBox startBox=new TextBox(), endBox=new TextBox(); Label now=new Label(), status=new Label(), ffLabel=new Label(); ProgressBar progress=new ProgressBar();
        Button add=new Button(), cut=new Button(), merge=new Button(), cancel=new Button(), moveTop=new Button(), moveBottom=new Button(); string ffmpeg,ffprobe; Task<string[]> ffmpegInit; readonly Queue<string[]> pendingDrops=new Queue<string[]>(); int selected=-1, dragIndex=-1; bool busy, updatingList; Point dragOrigin; int previewVersion; const string ClipDragFormat="SkyVideoEditor.Clips";
        public MainForm() {
            Text="SKYVIDEO EDITOR — 1.9.1"; AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi; Font=new Font("Segoe UI",9); ClientSize=new Size(1100,680); MinimumSize=new Size(900,620); StartPosition=FormStartPosition.CenterScreen; BackColor=Color.White;
            ForeColor=Color.FromArgb(31,41,55);BuildUi();ffLabel.Text="● 正在檢查 FFmpeg…";ffLabel.ForeColor=Color.DarkOrange;Shown+=async(s,e)=>await EnsureFfmpegReady();
        }
        void BuildUi() {
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty,Padding=Padding.Empty};
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,64));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,38));Controls.Add(root);
            var top=new FlowLayoutPanel{Dock=DockStyle.Top,Height=64,Padding=new Padding(18,11,18,9),BackColor=Color.FromArgb(245,247,250)};
            var title=new Label{Text="SKYVIDEO EDITOR",AutoSize=true,Font=new Font("Segoe UI",19,FontStyle.Bold),ForeColor=Color.FromArgb(17,32,58),Margin=new Padding(0,5,24,0)}; top.Controls.Add(title);
            add.Text="＋ 加入影片"; add.AutoSize=true; add.Click+=(s,e)=>AddFiles(); top.Controls.Add(add);
            var open=new Button{Text="開啟播放器",AutoSize=true}; open.Click+=(s,e)=>OpenPlayer(); top.Controls.Add(open);
            chooseFfmpeg.Click+=(s,e)=>ChooseFfmpeg(); top.Controls.Add(chooseFfmpeg);
            ffLabel.AutoSize=true; ffLabel.Margin=new Padding(20,8,0,0); top.Controls.Add(ffLabel);top.Dock=DockStyle.Fill;top.Margin=Padding.Empty;top.WrapContents=false;root.Controls.Add(top,0,0);
            var split=new SplitContainer{Dock=DockStyle.Fill,SplitterDistance=520,BackColor=Color.White,Margin=new Padding(12,10,12,8)};root.Controls.Add(split,0,1);
            Load+=(s,e)=>{int panelMin=(int)Math.Ceiling(360*CurrentAutoScaleDimensions.Width/96f);split.Panel1MinSize=panelMin;split.Panel2MinSize=panelMin;if(split.Width>panelMin*2+split.SplitterWidth)split.SplitterDistance=Math.Max(panelMin,Math.Min(split.Width-panelMin-split.SplitterWidth,(int)(split.Width*.48)));};
            var left=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));left.RowStyles.Add(new RowStyle(SizeType.AutoSize));left.RowStyles.Add(new RowStyle(SizeType.Percent,100)); var bar=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,Margin=Padding.Empty,WrapContents=true};
            var up=new Button{Text="↑ 上移",AutoSize=true}; up.Click+=(s,e)=>Reorder(-1); var down=new Button{Text="↓ 下移",AutoSize=true}; down.Click+=(s,e)=>Reorder(1); var rem=new Button{Text="× 移除",AutoSize=true}; rem.Click+=(s,e)=>RemoveSelected(); var all=new Button{Text="全選",AutoSize=true};all.Click+=(s,e)=>SelectAllClips();
            moveTop.Text="移到最頂";moveTop.Click+=(s,e)=>MoveToEdge(false);moveBottom.Text="移到最底";moveBottom.Click+=(s,e)=>MoveToEdge(true);
            bar.Controls.Add(up);bar.Controls.Add(down);bar.Controls.Add(moveTop);bar.Controls.Add(moveBottom);bar.Controls.Add(rem);bar.Controls.Add(all);left.Controls.Add(bar);
            list.Dock=DockStyle.Fill; list.IntegralHeight=false;list.BackColor=Color.White; list.ForeColor=Color.FromArgb(31,41,55); list.BorderStyle=BorderStyle.FixedSingle; list.Font=new Font("Segoe UI",10); list.ItemHeight=30; list.AllowDrop=true; list.SelectionMode=SelectionMode.MultiExtended;list.SelectedIndexChanged+=(s,e)=>SelectionChanged();list.KeyDown+=ListKeyDown; list.MouseDown+=ListMouseDown; list.MouseMove+=ListMouseMove; list.MouseUp+=(s,e)=>dragIndex=-1; list.DragEnter+=ListDragOver; list.DragOver+=ListDragOver; list.DragDrop+=ListDragDrop; left.Controls.Add(list,0,1);split.Panel1.Controls.Add(left);
            var right=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=8,Padding=new Padding(12,8,12,6)}; right.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            right.RowStyles.Clear();right.RowStyles.Add(new RowStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.AutoSize));right.RowStyles.Add(new RowStyle(SizeType.Absolute,38));for(int row=3;row<8;row++)right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            preview.Dock=DockStyle.Fill;preview.BackColor=Color.FromArgb(238,242,247);preview.SizeMode=PictureBoxSizeMode.Zoom;preview.BorderStyle=BorderStyle.FixedSingle;right.Controls.Add(preview,0,0);right.SetColumnSpan(preview,2);previewTimer.Interval=260;previewTimer.Tick+=(s,e)=>{previewTimer.Stop();RenderPreview();};
            now.Text="目前 00:00:00.000"; now.AutoSize=true; now.ForeColor=Color.FromArgb(5,150,105); right.Controls.Add(now,0,1);right.SetColumnSpan(now,2);
            timeline.Minimum=0; timeline.Maximum=100000; timeline.TickFrequency=10000; timeline.Dock=DockStyle.Fill; timeline.Scroll+=(s,e)=>TimelineChanged(); right.Controls.Add(timeline,0,2);right.SetColumnSpan(timeline,2);
            timeline.AccessibleName="影片預覽時間軸";tips.SetToolTip(timeline,"點選時間軸任意位置，或按住滑鼠左右拖曳以預覽；不會更動剪輯起點／終點。");
            right.Controls.Add(new Label{Text="起點 Start",AutoSize=true},0,3);var sb=new Button{Text="設為目前"};sb.Click+=(s,e)=>{if(busy||selected<0)return;startBox.Text=Clip.Fmt(CurrentTime());ApplyTimes();};right.Controls.Add(TimeRow(startBox,sb),1,3);
            right.Controls.Add(new Label{Text="終點 End",AutoSize=true},0,4);var eb=new Button{Text="設為目前"};eb.Click+=(s,e)=>{if(busy||selected<0)return;endBox.Text=Clip.Fmt(CurrentTime());ApplyTimes();};right.Controls.Add(TimeRow(endBox,eb),1,4);
            var apply=new Button{Text="套用時間",AutoSize=true};apply.Click+=(s,e)=>ApplyTimes();right.Controls.Add(apply,1,5);
            var settings=BuildExportSettings();
            right.Controls.Add(settings,0,6);right.SetColumnSpan(settings,2);
            var ex=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};cut.Text="分別輸出";cut.AutoSize=true;cut.Click+=(s,e)=>Export(false);merge.Text="合併輸出";merge.AutoSize=true;merge.Click+=(s,e)=>Export(true);cancel.Text="取消";cancel.AutoSize=true;cancel.Enabled=false;cancel.Click+=(s,e)=>Cancel();ex.Controls.Add(cut);ex.Controls.Add(merge);ex.Controls.Add(cancel);tips.AutoPopDelay=8000;tips.InitialDelay=300;tips.ReshowDelay=100;tips.SetToolTip(cut,"將清單中的每支影片依起點／終點剪輯，分別儲存成檔案。");tips.SetToolTip(merge,"將清單中的所有剪輯片段依目前順序合併成一支影片。");right.Controls.Add(ex,0,7);right.SetColumnSpan(ex,2);split.Panel2.Controls.Add(right);
            var bottom=new TableLayoutPanel{Dock=DockStyle.Fill,Margin=Padding.Empty,ColumnCount=2,RowCount=1,Padding=new Padding(18,7,18,7),BackColor=Color.FromArgb(245,247,250)};bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,280));status.Text="就緒";status.Dock=DockStyle.Fill;status.ForeColor=Color.FromArgb(75,85,99);progress.Dock=DockStyle.Fill;progress.Minimum=0;progress.Maximum=100;progress.Value=0;progress.Style=ProgressBarStyle.Continuous;bottom.Controls.Add(status,0,0);bottom.Controls.Add(progress,1,0);root.Controls.Add(bottom,0,2);
            ex.Margin=Padding.Empty;ex.WrapContents=false;
            ApplyTheme(this);
            EnableFileDrop(this);
            UpdateEdgeButtons();
        }
        TableLayoutPanel BuildExportSettings(){
            var settings=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=3,RowCount=3,Margin=Padding.Empty};
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,35));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
            for(int row=0;row<3;row++)settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var labels=new[]{"輸出模式","影片編碼","檔案格式"};
            var choices=new[]{exportMode,exportCodec,exportContainer};
            exportMode.Items.AddRange(new[]{"重新編碼","快速無損"});
            exportCodec.Items.AddRange(new[]{"H.264","H.265 (HEVC)"});
            exportContainer.Items.AddRange(new[]{"依來源（保留 MKV）","MP4","MKV"});
            for(int column=0;column<choices.Length;column++){
                settings.Controls.Add(new Label{Text=labels[column],AutoSize=true},column,0);
                var choice=choices[column];
                choice.AccessibleName=labels[column];
                choice.DropDownStyle=ComboBoxStyle.DropDownList;
                choice.Dock=DockStyle.Fill;
                choice.SelectedIndex=0;
                settings.Controls.Add(choice,column,1);
                choice.SelectedIndexChanged+=(s,e)=>UpdateExportHelp();
            }
            exportHelp.AutoSize=true;
            exportHelp.Dock=DockStyle.Fill;
            settings.Controls.Add(exportHelp,0,2);
            settings.SetColumnSpan(exportHelp,3);
            settings.SizeChanged+=(s,e)=>exportHelp.MaximumSize=new Size(Math.Max(1,settings.ClientSize.Width-exportHelp.Margin.Horizontal),0);
            UpdateExportHelp();
            return settings;
        }

        void UpdateExportHelp(){
            bool fast=exportMode.SelectedIndex==(int)OutputMode.StreamCopy;
            exportCodec.Enabled=!busy&&!fast;
            string modeHelp=fast
                ?"快速無損會直接複製影音串流；切點可能落在附近的關鍵影格。合併時各段串流需相容。"
                :exportCodec.SelectedIndex==(int)OutputCodec.H265
                    ?"重新編碼可精準剪輯。H.265 通常較省空間，編碼較慢，播放器需支援 HEVC。"
                    :"重新編碼可精準剪輯。H.264 的播放相容性較廣。";
            string containerHelp=exportContainer.SelectedIndex==(int)OutputContainer.MatchSource
                ?"MKV 來源輸出為 MKV，其餘為 MP4。合併含 MKV 時輸出 MKV。"
                :"輸出為 "+exportContainer.SelectedItem+"。";
            exportHelp.Text=modeHelp+Environment.NewLine+containerHelp;
        }

        static TableLayoutPanel TimeRow(TextBox box,Button button){var row=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,RowCount=1,Margin=Padding.Empty};row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));row.RowStyles.Add(new RowStyle(SizeType.AutoSize));box.Anchor=AnchorStyles.Left|AnchorStyles.Right;button.AutoSize=true;button.Dock=DockStyle.Fill;row.Controls.Add(box,0,0);row.Controls.Add(button,1,0);return row;}
        async Task EnsureFfmpegReady(){if(ffmpeg!=null)return;if(ffmpegInit==null)ffmpegInit=Task.Run(()=>{string adjacent=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ffmpeg.exe");if(File.Exists(adjacent))return new[]{adjacent,FindSibling(adjacent,"ffprobe.exe")};string found=ExtractBundledFfmpeg();if(found==null)found=Find("ffmpeg.exe");return new[]{found,found==null?null:FindSibling(found,"ffprobe.exe")};});string[] result=await ffmpegInit;if(ffmpeg==null){ffmpeg=result[0];ffprobe=result[1];}UpdateFfmpegLabel();}
        void EnableFileDrop(Control c){c.AllowDrop=true;if(c!=list){c.DragEnter+=FileDragOver;c.DragOver+=FileDragOver;c.DragDrop+=(s,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop))AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));};}foreach(Control child in c.Controls)EnableFileDrop(child);}
        string ExtractBundledFfmpeg(){try{string dir=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SKYVIDEO_EDITOR");string target=System.IO.Path.Combine(dir,"ffmpeg.exe");if(File.Exists(target)&&new FileInfo(target).Length>1000000)return target;using(Stream source=Assembly.GetExecutingAssembly().GetManifestResourceStream("ffmpeg.exe")){if(source==null)return null;Directory.CreateDirectory(dir);using(FileStream output=File.Create(target)){source.CopyTo(output);}return target;}}catch{return null;}}
        void ApplyTheme(Control parent){foreach(Control c in parent.Controls){if(c is Button){var b=(Button)c;b.BackColor=Color.White;b.ForeColor=Color.FromArgb(31,41,55);b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Color.FromArgb(148,163,184);b.FlatAppearance.MouseOverBackColor=Color.FromArgb(239,246,255);b.Font=new Font("Segoe UI",9,FontStyle.Regular);b.AutoSize=true;b.AutoSizeMode=AutoSizeMode.GrowAndShrink;b.MinimumSize=new Size(0,34);b.Padding=new Padding(10,3,10,3);}else if(c is TextBox){c.BackColor=Color.White;c.ForeColor=Color.FromArgb(31,41,55);c.Font=new Font("Segoe UI",10);}else if(c is CheckBox){c.ForeColor=Color.FromArgb(31,41,55);}else if(c is Label && c!=now && c!=ffLabel){c.ForeColor=Color.FromArgb(31,41,55);}ApplyTheme(c);}}
        static string FindSibling(string path,string name){string p=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path),name);return File.Exists(p)?p:null;}
        string Find(string name){var c=new List<string>{System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"ComfyUI",name),System.IO.Path.Combine("C:\\ComfyUI",name),System.IO.Path.Combine("D:\\ComfyUI",name)};string p=Environment.GetEnvironmentVariable("PATH")??"";c.AddRange(p.Split(';').Select(x=>System.IO.Path.Combine(x,name)));foreach(var x in c)if(File.Exists(x))return x;return null;}
        void UpdateFfmpegLabel(){ffLabel.Text=ffmpeg==null?"● 尚未設定 FFmpeg":"● FFmpeg 已就緒";ffLabel.ForeColor=ffmpeg==null?Color.Salmon:Color.MediumAquamarine;}
        void ChooseFfmpeg(){using(var d=new OpenFileDialog{Filter="ffmpeg.exe|ffmpeg.exe|執行檔|*.exe",Title="選擇 ffmpeg.exe"})if(d.ShowDialog()==DialogResult.OK){ffmpeg=d.FileName;ffprobe=FindSibling(ffmpeg,"ffprobe.exe");UpdateFfmpegLabel();}}
        void AddFiles(){using(var d=new OpenFileDialog{Filter="影片|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.wmv;*.flv;*.mpeg;*.mpg;*.ts;*.mts;*.m2ts|所有檔案|*.*",Multiselect=true,Title="加入影片"})if(d.ShowDialog()==DialogResult.OK)AddPaths(d.FileNames);}
        async void AddPaths(string[] paths){var valid=paths.Where(p=>File.Exists(p)&&Regex.IsMatch(System.IO.Path.GetExtension(p),"^\\.(mp4|mov|mkv|webm|avi|m4v|wmv|flv|mpeg|mpg|ts|mts|m2ts)$",RegexOptions.IgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(valid.Length==0){MessageBox.Show(this,"請拖入支援的影片檔案，例如 MP4、MOV、MKV、WebM、AVI、MPEG 或 TS。","不支援的檔案");return;}if(busy){pendingDrops.Enqueue(valid);status.Text="已保留待處理的 "+valid.Length+" 支影片…";return;}SetBusy(true,"已接收 "+valid.Length+" 支影片，正在準備 FFmpeg…");await EnsureFfmpegReady();if(ffmpeg==null){SetBusy(false,"找不到 FFmpeg");MessageBox.Show(this,"找不到 ffmpeg.exe，請確認它和程式放在同一個資料夾。","FFmpeg 尚未就緒",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}int added=0;for(int i=0;i<valid.Length;i++){string p=valid[i];SetProgress((int)(i*100.0/valid.Length),"正在讀取第 "+(i+1)+"／"+valid.Length+" 支影片…");try{var c=await Task.Run(()=>Probe(p));clips.Add(c);list.Items.Add(c);added++;}catch(Exception ex){MessageBox.Show(this,p+"\n"+ex.Message,"無法加入影片",MessageBoxButtons.OK,MessageBoxIcon.Warning);}SetProgress((int)((i+1)*100.0/valid.Length),"已處理第 "+(i+1)+"／"+valid.Length+" 支影片");}SetBusy(false,"已加入 "+added+" 支影片");if(selected<0&&clips.Count>0)list.SelectedIndex=0;if(pendingDrops.Count>0){var next=pendingDrops.Dequeue();AddPaths(next);}}
        Clip Probe(string p){if(ffprobe!=null){var r=Run(ffprobe,"-v error -show_entries format=duration:stream=codec_type,width,height,r_frame_rate -of json \""+p+"\"");var m=Regex.Match(r,"\\\"duration\\\"\\s*:\\s*\\\"?([0-9.]+)");if(!m.Success)throw new Exception("無法讀取影片長度");double d=double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture);return new Clip{Path=p,Name=System.IO.Path.GetFileName(p),Duration=d,Start=0,End=d};}if(ffmpeg==null)throw new Exception("請先在 FFmpeg 設定中選擇 ffmpeg.exe");var text=Run(ffmpeg,"-hide_banner -i \""+p+"\"");var dm=Regex.Match(text,"Duration:\\s*(\\d+):(\\d+):(\\d+(?:\\.\\d+)?)");if(!dm.Success)throw new Exception("無法讀取影片長度");double dur=int.Parse(dm.Groups[1].Value)*3600+int.Parse(dm.Groups[2].Value)*60+double.Parse(dm.Groups[3].Value,CultureInfo.InvariantCulture);return new Clip{Path=p,Name=System.IO.Path.GetFileName(p),Duration=dur,Start=0,End=dur};}
        string Run(string exe,string args){
            var info=new ProcessStartInfo(exe,args){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true,StandardErrorEncoding=Encoding.UTF8,StandardOutputEncoding=Encoding.UTF8};
            using(var process=Process.Start(info)){
                var output=process.StandardOutput.ReadToEndAsync();
                var errors=process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                return output.GetAwaiter().GetResult()+"\n"+errors.GetAwaiter().GetResult();
            }
        }
        Clip ActiveClip(){return selected>=0&&selected<clips.Count?clips[selected]:null;}
        Clip[] SelectedClips(){return list.SelectedIndices.Cast<int>().Where(i=>i>=0&&i<clips.Count).Select(i=>clips[i]).ToArray();}
        void SelectionChanged(){if(updatingList)return;if(list.SelectedIndices.Count==0)SelectClip(-1);else if(selected<0||!list.GetSelected(selected))SelectClip(list.SelectedIndex);UpdateEdgeButtons();}
        void SelectClip(int i){
            previewVersion++;previewTimer.Stop();var previous=preview.Image;preview.Image=null;if(previous!=null)previous.Dispose();
            if(i<0||i>=clips.Count){selected=-1;startBox.Clear();endBox.Clear();timeline.Value=0;now.Text="目前 00:00:00.000";return;}
            selected=i;var c=clips[i];startBox.Text=Clip.Fmt(c.Start);endBox.Text=Clip.Fmt(c.End);timeline.Value=(int)(c.Start/Math.Max(c.Duration,.001)*100000);now.Text="目前 "+Clip.Fmt(c.Start);previewTimer.Start();
        }
        // Rebuilding labels/order must preserve the active clip, selection and playhead.
        void RefreshClipList(Clip[] chosen,Clip active){
            int top=list.TopIndex;updatingList=true;list.BeginUpdate();
            try{list.Items.Clear();foreach(var clip in clips)list.Items.Add(clip);for(int i=0;i<clips.Count;i++)if(chosen.Contains(clips[i]))list.SetSelected(i,true);if(clips.Count>0)list.TopIndex=Math.Min(top,clips.Count-1);}
            finally{list.EndUpdate();updatingList=false;}
            if(active!=null&&clips.Contains(active)&&chosen.Contains(active))selected=clips.IndexOf(active);
            else SelectClip(list.SelectedIndex);
            UpdateEdgeButtons();
        }
        void SelectAllClips(){if(busy)return;updatingList=true;list.BeginUpdate();try{for(int i=0;i<list.Items.Count;i++)list.SetSelected(i,true);}finally{list.EndUpdate();updatingList=false;}SelectionChanged();list.Focus();}
        void ListKeyDown(object sender,KeyEventArgs e){
            if(e.KeyCode==Keys.Delete&&e.Modifiers==Keys.None){e.Handled=true;e.SuppressKeyPress=true;RemoveSelected();}
            else if(e.KeyCode==Keys.A&&e.Modifiers==Keys.Control){e.Handled=true;e.SuppressKeyPress=true;SelectAllClips();}
        }
        double CurrentTime(){if(selected<0)return 0;return timeline.Value/100000.0*clips[selected].Duration;}
        void TimelineChanged(){if(selected<0)return;previewVersion++;now.Text="目前 "+Clip.Fmt(CurrentTime());previewTimer.Stop();previewTimer.Start();}
        async void RenderPreview(){
            if(selected<0||selected>=clips.Count||ffmpeg==null)return;
            int request=++previewVersion;var clip=clips[selected];string path=clip.Path;
            double at=Math.Min(CurrentTime(),Math.Max(0,clip.Duration-0.1));
            string image=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"skyvideo-preview-"+Guid.NewGuid().ToString("N")+".jpg");
            try{
                await Task.Run(()=>RunChecked(ffmpeg,"-hide_banner -loglevel error -y -ss "+at.ToString("0.###",CultureInfo.InvariantCulture)+" -i \""+path+"\" -frames:v 1 -vf \"scale=720:-2\" \""+image+"\""));
                if(IsDisposed||request!=previewVersion||selected<0||selected>=clips.Count||clips[selected]!=clip)return;
                if(!File.Exists(image))throw new Exception("此時間點沒有可顯示的影格");
                using(var fs=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                using(var fresh=new Bitmap(fs)){var copy=new Bitmap(fresh);var previous=preview.Image;preview.Image=copy;if(previous!=null)previous.Dispose();}
            }catch(Exception ex){if(!IsDisposed&&request==previewVersion)status.Text="預覽失敗："+ex.Message.Split('\n')[0];}
            finally{try{File.Delete(image);}catch{}}
        }
        bool CommitTimes(){
            var c=ActiveClip();if(c==null)return true;
            double a=Clip.Parse(startBox.Text),b=Clip.Parse(endBox.Text);
            if(double.IsNaN(a)||double.IsInfinity(a)||double.IsNaN(b)||double.IsInfinity(b)||a>=b||b>c.Duration+.05)throw new Exception("起點／終點範圍不正確");
            c.Start=a;c.End=Math.Min(b,c.Duration);startBox.Text=Clip.Fmt(c.Start);endBox.Text=Clip.Fmt(c.End);
            RefreshClipList(SelectedClips(),c);return true;
        }
        void ApplyTimes(){if(busy||selected<0)return;try{CommitTimes();}catch(Exception ex){MessageBox.Show(this,ex.Message,"時間格式錯誤",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        void Reorder(int d){
            if(busy)return;var chosen=SelectedClips();if(chosen.Length==0)return;var active=ActiveClip();bool changed=false;
            // Move each selected group one place without changing its internal order.
            if(d<0){for(int i=1;i<clips.Count;i++)if(chosen.Contains(clips[i])&&!chosen.Contains(clips[i-1])){var c=clips[i];clips[i]=clips[i-1];clips[i-1]=c;changed=true;}}
            else{for(int i=clips.Count-2;i>=0;i--)if(chosen.Contains(clips[i])&&!chosen.Contains(clips[i+1])){var c=clips[i];clips[i]=clips[i+1];clips[i+1]=c;changed=true;}}
            if(changed)RefreshClipList(chosen,active);
        }
        void UpdateEdgeButtons(){
            var chosen=SelectedClips();bool enabled=!busy&&chosen.Length>0;
            moveTop.Enabled=enabled&&!clips.Take(chosen.Length).SequenceEqual(chosen);
            moveBottom.Enabled=enabled&&!clips.Skip(clips.Count-chosen.Length).SequenceEqual(chosen);
        }
        void MoveToEdge(bool bottom){
            if(busy)return;var chosen=SelectedClips();if(chosen.Length==0)return;var active=ActiveClip();
            clips.RemoveAll(c=>chosen.Contains(c));clips.InsertRange(bottom?clips.Count:0,chosen);
            RefreshClipList(chosen,active);
            if(bottom)list.TopIndex=Math.Max(0,clips.Count-Math.Max(1,list.ClientSize.Height/Math.Max(1,list.ItemHeight)));
            else list.TopIndex=0;
            list.Focus();status.Text="已將 "+chosen.Length+" 支影片移到"+(bottom?"最底":"最頂");
        }
        void RemoveSelected(){
            if(busy)return;var chosen=SelectedClips();if(chosen.Length==0)return;int first=list.SelectedIndex;var active=ActiveClip();
            clips.RemoveAll(c=>chosen.Contains(c));
            if(clips.Count==0)RefreshClipList(new Clip[0],null);
            else{var next=active!=null&&clips.Contains(active)?active:clips[Math.Min(first,clips.Count-1)];RefreshClipList(new[]{next},next==active?active:null);}
            status.Text="已從清單移除 "+chosen.Length+" 支影片";
        }
        void FileDragOver(object s,DragEventArgs e){e.Effect=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;}
        void ListMouseDown(object s,MouseEventArgs e){dragIndex=-1;if(e.Button!=MouseButtons.Left||busy)return;dragIndex=list.IndexFromPoint(e.Location);dragOrigin=e.Location;if(dragIndex>=0&&list.GetSelected(dragIndex)&&selected!=dragIndex)SelectClip(dragIndex);if((ModifierKeys&(Keys.Control|Keys.Shift))!=Keys.None)dragIndex=-1;}
        void ListMouseMove(object s,MouseEventArgs e){
            if(e.Button!=MouseButtons.Left||dragIndex<0||busy)return;
            Size size=SystemInformation.DragSize;var bounds=new Rectangle(dragOrigin.X-size.Width/2,dragOrigin.Y-size.Height/2,size.Width,size.Height);
            if(bounds.Contains(e.Location))return;
            var chosen=SelectedClips();if(chosen.Length==0)return;var data=new DataObject();data.SetData(ClipDragFormat,chosen);
            try{list.DoDragDrop(data,DragDropEffects.Move);}finally{dragIndex=-1;}
        }
        void ListDragOver(object s,DragEventArgs e){if(busy){e.Effect=DragDropEffects.None;return;}if(e.Data.GetDataPresent(DataFormats.FileDrop)){e.Effect=DragDropEffects.Copy;return;}e.Effect=e.Data.GetDataPresent(ClipDragFormat)?DragDropEffects.Move:DragDropEffects.None;}
        void ListDragDrop(object s,DragEventArgs e){
            if(busy)return;
            if(e.Data.GetDataPresent(DataFormats.FileDrop)){AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));return;}
            if(!e.Data.GetDataPresent(ClipDragFormat))return;
            var chosen=e.Data.GetData(ClipDragFormat) as Clip[];if(chosen==null||chosen.Length==0||chosen.Any(c=>!clips.Contains(c)))return;
            var active=ActiveClip();int to=list.IndexFromPoint(list.PointToClient(new Point(e.X,e.Y)));
            // Drop before the target row, or at the end when below the list.
            if(to>=0&&chosen.Contains(clips[to]))return;
            int insert=to<0?clips.Count-chosen.Length:clips.Take(to).Count(c=>!chosen.Contains(c));
            var ordered=clips.Where(c=>chosen.Contains(c)).ToArray();clips.RemoveAll(c=>chosen.Contains(c));clips.InsertRange(insert,ordered);
            RefreshClipList(ordered,active);dragIndex=-1;
        }
        async void Export(bool mergeIt){
            if(busy||clips.Count==0){MessageBox.Show(this,"請先加入影片。","提示");return;}
            if(ffmpeg==null){ChooseFfmpeg();if(ffmpeg==null)return;}
            if(!ApplyAndValidate())return;
            var mode=(OutputMode)exportMode.SelectedIndex;
            var codec=(OutputCodec)exportCodec.SelectedIndex;
            var container=(OutputContainer)exportContainer.SelectedIndex;
            string dest;
            if(mergeIt){
                var resolved=ResolveContainer(container,clips);
                string extension=resolved==OutputContainer.Mkv?"mkv":"mp4";
                using(var dialog=new SaveFileDialog{Filter=extension.ToUpperInvariant()+" 影片|*."+extension,DefaultExt=extension,FileName="merged."+extension}){
                    dialog.FileOk+=(s,e)=>{
                        if(string.Equals(Path.GetExtension(dialog.FileName),"."+extension,StringComparison.OrdinalIgnoreCase))return;
                        e.Cancel=true;
                        MessageBox.Show(this,"請使用 ."+extension+" 副檔名。","輸出格式");
                    };
                    if(dialog.ShowDialog()!=DialogResult.OK)return;
                    dest=dialog.FileName;
                }
            }else{
                using(var dialog=new FolderBrowserDialog{Description="選擇片段輸出資料夾"}){
                    if(dialog.ShowDialog()!=DialogResult.OK)return;
                    dest=dialog.SelectedPath;
                }
            }
            SetBusy(true,mergeIt?"準備合成…":"準備輸出…");
            try{
                var snap=clips.Select(c=>new Clip{Path=c.Path,Name=c.Name,Start=c.Start,End=c.End}).ToList();
                var output=await Task.Run(()=>DoExport(snap,mergeIt,mode,codec,container,dest));
                SetBusy(false,"輸出完成");
                MessageBox.Show(this,"已建立：\n"+output,"完成",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }catch(Exception ex){
                SetBusy(false,"輸出失敗");
                MessageBox.Show(this,ex.Message,"輸出失敗",MessageBoxButtons.OK,MessageBoxIcon.Error);
            }
        }

        static OutputContainer ResolveContainer(OutputContainer container,IEnumerable<Clip> sources){
            if(container!=OutputContainer.MatchSource)return container;
            return sources.Any(c=>string.Equals(Path.GetExtension(c.Path),".mkv",StringComparison.OrdinalIgnoreCase))
                ?OutputContainer.Mkv:OutputContainer.Mp4;
        }
        bool ApplyAndValidate(){for(int i=0;i<clips.Count;i++){if(i==selected&&!ApplySilent())return false;var c=clips[i];if(c.End<=c.Start)return false;}return true;}
        bool ApplySilent(){try{return CommitTimes();}catch{return false;}}
        string DoExport(List<Clip> clipsToExport,bool mergeIt,OutputMode mode,OutputCodec codec,OutputContainer container,string dest){
            bool fast=mode==OutputMode.StreamCopy;
            var mergedContainer=mergeIt?ResolveContainer(container,clipsToExport):container;
            string temp=mergeIt?Path.Combine(Path.GetTempPath(),"skyvideo-"+Guid.NewGuid().ToString("N")):null;
            if(mergeIt)Directory.CreateDirectory(temp);
            try{
                var parts=new List<string>();
                double clipSeconds=Math.Max(.001,clipsToExport.Sum(c=>c.Length));
                double totalWork=clipSeconds*(mergeIt?2:1),completed=0;
                var mediaInfo=mergeIt&&!fast&&clipsToExport.Count>1
                    ?clipsToExport.Select(c=>ReadStreamInfo(c.Path)).ToArray()
                    :null;
                string videoFilter="scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p";
                bool mergeHasAudio=false;
                if(mediaInfo!=null){
                    var dimensions=Regex.Match(mediaInfo[0],@"Video:[^\r\n]*?\b(\d{2,})x(\d{2,})\b");
                    if(!dimensions.Success)throw new Exception("無法讀取第一段影片的解析度。");
                    int width=int.Parse(dimensions.Groups[1].Value,CultureInfo.InvariantCulture)/2*2;
                    int height=int.Parse(dimensions.Groups[2].Value,CultureInfo.InvariantCulture)/2*2;
                    string size=width+":"+height;
                    string frameRate=ReadMergeFrameRate(clipsToExport[0].Path,mediaInfo[0]);
                    videoFilter="fps="+frameRate+",scale="+size
                        +":force_original_aspect_ratio=decrease:force_divisible_by=2,pad="+size+":(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p";
                    mergeHasAudio=mediaInfo.Any(info=>Regex.IsMatch(info,@"(?m)^\s*Stream #.*: Audio:"));
                }

                string streamSignature=null;
                for(int i=0;i<clipsToExport.Count;i++){
                    if(!busy)throw new Exception("已取消");
                    var clip=clipsToExport[i];
                    int item=i+1;
                    double itemStart=completed,itemLength=clip.Length;
                    SetProgress((int)(itemStart*100/totalWork),"正在處理第 "+item+"／"+clipsToExport.Count+" 段…");
                    var partContainer=mergeIt?mergedContainer:ResolveContainer(container,new[]{clip});
                    string extension=partContainer==OutputContainer.Mkv?".mkv":".mp4";
                    string outp=mergeIt
                        ?Path.Combine(temp,"part-"+i.ToString("000")+extension)
                        :Path.Combine(dest,item.ToString("00")+"_"+Path.GetFileNameWithoutExtension(clip.Name)+"_cut"+extension);
                    bool addSilence=mergeHasAudio&&!Regex.IsMatch(mediaInfo[i],@"(?m)^\s*Stream #.*: Audio:");
                    string args="-hide_banner -loglevel error -y -progress pipe:1 -nostats -ss "
                        +clip.Start.ToString("0.###",CultureInfo.InvariantCulture)+" -i \""+clip.Path+"\" ";
                    if(addSilence)args+="-f lavfi -i anullsrc=r=48000:cl=stereo ";
                    args+="-t "+itemLength.ToString("0.###",CultureInfo.InvariantCulture)+" -map 0:v:0 ";
                    args+=addSilence?"-map 1:a:0 -shortest ":"-map 0:a:0? ";
                    if(fast)args+="-c copy -avoid_negative_ts make_zero ";
                    else{
                        args+="-vf \""+videoFilter+"\" ";
                        args+=codec==OutputCodec.H265
                            ?"-c:v libx265 -preset veryfast -crf 23 "
                            :"-c:v libx264 -preset veryfast -crf 18 ";
                        if(codec==OutputCodec.H265&&partContainer==OutputContainer.Mp4)args+="-tag:v hvc1 ";
                        args+="-c:a aac -b:a 192k -ar 48000 ";
                        if(mergeHasAudio)args+="-ac 2 ";
                    }
                    if(partContainer==OutputContainer.Mp4){
                        args+="-movflags +faststart ";
                        if(mergeIt)args+="-video_track_timescale 90000 ";
                    }
                    try{
                        RunCheckedProgress(ffmpeg,args+"\""+outp+"\"",itemLength,t=>{
                            int percent=(int)((itemStart+Math.Min(t,itemLength))*100/totalWork);
                            SetProgress(percent,"正在處理第 "+item+"／"+clipsToExport.Count+" 段… "+percent+"%");
                        });
                    }catch(Exception ex){
                        if(!busy||!fast)throw;
                        throw new Exception("快速無損需要原始串流與輸出格式相容。可改用重新編碼。\n\n"+ex.Message,ex);
                    }

                    if(fast&&mergeIt&&clipsToExport.Count>1){
                        string signature=ReadStreamSignature(outp);
                        if(string.IsNullOrWhiteSpace(signature)||(streamSignature!=null&&streamSignature!=signature))
                            throw new Exception("快速無損無法合併這些片段，因為影音串流設定不同或無法確認。請改用「重新編碼」。");
                        streamSignature=signature;
                    }
                    parts.Add(outp);
                    completed+=itemLength;
                }
                if(mergeIt){
                    if(!busy)throw new Exception("已取消");
                    SetProgress(50,"正在合成影片… 50%");
                    string txt=Path.Combine(temp,"concat.txt");
                    File.WriteAllLines(txt,parts.Select(p=>"file '"+p.Replace("'","'\\''")+"'"),new UTF8Encoding(false));
                    string containerArgs=mergedContainer==OutputContainer.Mp4
                        ?" -movflags +faststart -f mp4"
                        :" -f matroska";
                    RunCheckedProgress(ffmpeg,"-hide_banner -loglevel error -y -progress pipe:1 -nostats -f concat -safe 0 -i \""
                        +txt+"\" -c copy"+containerArgs+" \""+dest+"\"",clipSeconds,t=>{
                        int percent=(int)((completed+Math.Min(t,clipSeconds))*100/totalWork);
                        SetProgress(percent,"正在合成影片… "+percent+"%");
                    });
                }
                SetProgress(100,"處理完成 100%");
                return mergeIt?dest:string.Join("\n",parts);
            }finally{
                if(temp!=null)try{Directory.Delete(temp,true);}catch{}
            }
        }

        string ReadStreamInfo(string path){
            return Run(ffmpeg,"-hide_banner -i \""+path+"\"");
        }

        string ReadMergeFrameRate(string path,string streamInfo){
            if(ffprobe!=null){
                string rate=Run(ffprobe,"-v error -select_streams v:0 -show_entries stream=r_frame_rate "
                    +"-of default=nw=1:nk=1 \""+path+"\"").Trim();
                if(Regex.IsMatch(rate,@"^[1-9]\d*/[1-9]\d*$"))return rate;
            }
            var reported=Regex.Match(streamInfo,@"Video:[^\r\n]*?, ([1-9]\d*(?:\.\d+)?) fps");
            return reported.Success?reported.Groups[1].Value:"30";
        }

        string ReadStreamSignature(string path){
            if(ffprobe!=null){
                string args="-v error -show_data_hash sha256 "
                    +"-show_entries stream=codec_name,codec_type,profile,width,height,pix_fmt,sample_aspect_ratio,sample_rate,channels,channel_layout,time_base,extradata_hash "
                    +"-of compact=p=0 \""+path+"\"";
                return Run(ffprobe,args).Trim();
            }

            var streams=Regex.Matches(ReadStreamInfo(path),@"(?m)^\s*Stream #.*?: ((?:Video|Audio):[^\r\n]+)");
            return string.Join("\n",streams.Cast<Match>().Select(stream=>
                Regex.Replace(stream.Groups[1].Value,@", \d+ kb/s|\s+\([^)]*default[^)]*\)","")));
        }
        void RunChecked(string exe,string args){var si=new ProcessStartInfo(exe,args){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true,StandardErrorEncoding=Encoding.UTF8,StandardOutputEncoding=Encoding.UTF8};using(var p=Process.Start(si)){string o=p.StandardOutput.ReadToEnd(),e=p.StandardError.ReadToEnd();p.WaitForExit();string text=e+"\n"+o;if(p.ExitCode!=0)throw new Exception(text.Length>2500?text.Substring(text.Length-2500):text);}}
        void RunCheckedProgress(string exe,string args,double duration,Action<double> report){var errors=new StringBuilder();var si=new ProcessStartInfo(exe,args){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true,StandardErrorEncoding=Encoding.UTF8,StandardOutputEncoding=Encoding.UTF8};using(var p=new Process{StartInfo=si}){p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(errors)errors.AppendLine(e.Data);};p.Start();p.BeginErrorReadLine();string line;while((line=p.StandardOutput.ReadLine())!=null){if(!busy){try{p.Kill();}catch{}throw new Exception("已取消");}int eq=line.IndexOf('=');if(eq<1)continue;string key=line.Substring(0,eq),value=line.Substring(eq+1);double seconds;TimeSpan time;if((key=="out_time_us"||key=="out_time_ms")&&double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out seconds))report(seconds/1000000.0);else if(key=="out_time"&&TimeSpan.TryParse(value,CultureInfo.InvariantCulture,out time))report(time.TotalSeconds);else if(key=="progress"&&value=="end")report(duration);}p.WaitForExit();if(p.ExitCode!=0){string text;lock(errors)text=errors.ToString();throw new Exception(text.Length>2500?text.Substring(text.Length-2500):text);}}}
        void OpenPlayer(){if(selected<0)return;try{Process.Start(clips[selected].Path);}catch(Exception ex){MessageBox.Show(ex.Message);}}
        void Cancel(){busy=false;cancel.Enabled=false;status.Text="正在取消…";}
        void SetProgress(int value,string text){Action update=()=>{progress.Value=Math.Max(0,Math.Min(100,value));status.Text=text;};if(IsHandleCreated&&InvokeRequired)BeginInvoke(update);else update();}
        void SetBusy(bool b,string text){busy=b;add.Enabled=!b;chooseFfmpeg.Enabled=!b;cut.Enabled=!b;merge.Enabled=!b;cancel.Enabled=b;list.Enabled=!b;startBox.Enabled=!b;endBox.Enabled=!b;timeline.Enabled=!b;exportMode.Enabled=!b;exportContainer.Enabled=!b;UpdateExportHelp();UpdateEdgeButtons();if(b)progress.Value=0;else if(!text.Contains("完成")&&!text.Contains("已加入"))progress.Value=0;status.Text=text;}
    }
    static class Program {
        [STAThread]
        static void Main(){
            try{
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using(var icon=Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location))
                using(var form=new MainForm()){
                    if(icon!=null)form.Icon=icon;
                    Application.Run(form);
                }
            }catch(Exception ex){
                try{File.WriteAllText(Path.Combine(Path.GetTempPath(),"SKYVIDEO_EDITOR_error.txt"),ex.ToString());}catch{}
                MessageBox.Show(ex.ToString(),"SKYVIDEO EDITOR 啟動失敗",MessageBoxButtons.OK,MessageBoxIcon.Error);
            }
        }
    }
}
