$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class TerminalNotesIconGenerator {
    static GraphicsPath Rounded(float x,float y,float width,float height,float radius) {
        var p=new GraphicsPath(); float d=radius*2;
        p.AddArc(x,y,d,d,180,90); p.AddArc(x+width-d,y,d,d,270,90);
        p.AddArc(x+width-d,y+height-d,d,d,0,90); p.AddArc(x,y+height-d,d,d,90,90);p.CloseFigure();return p;
    }
    static Color ColorOf(string hex) {return ColorTranslator.FromHtml(hex);}
    static LinearGradientBrush Gradient(RectangleF bounds,string[] colors,float[] positions) {
        var brush=new LinearGradientBrush(bounds,ColorOf(colors[0]),ColorOf(colors[colors.Length-1]),LinearGradientMode.Vertical);
        var blend=new ColorBlend(colors.Length); blend.Positions=positions;
        for(int i=0;i<colors.Length;i++) blend.Colors[i]=ColorOf(colors[i]);
        brush.InterpolationColors=blend;return brush;
    }
    static void DrawIcon(Graphics g) {
        // Keep geometry and gradient stops in sync with Assets/AppIcon.svg.
        using(var p=Rounded(8,8,240,240,52))
        using(var b=Gradient(new RectangleF(8,8,240,240),new[]{"#20201E","#10100F","#070807"},new[]{0f,.45f,1f})) g.FillPath(b,p);
        using(var p=Rounded(12,12,232,232,48))
        using(var b=Gradient(new RectangleF(12,12,232,232),new[]{"#B9965C","#F5E6BF","#D8BB82","#B38E52","#EAD29D","#B9965C"},new[]{0f,.22f,.46f,.53f,.60f,1f}))
        using(var pen=new Pen(b,4)) g.DrawPath(pen,p);
        using(var b=Gradient(new RectangleF(48,64,160,128),new[]{"#FFF0CE","#E5CCA0","#C5A16A","#F3DDB1","#D8B77E"},new[]{0f,.44f,.52f,.61f,1f}))
        using(var pen=new Pen(b,20)) {
            pen.StartCap=pen.EndCap=LineCap.Square;pen.LineJoin=LineJoin.Miter;
            g.DrawLines(pen,new[]{new PointF(65,83),new PointF(109,128),new PointF(65,173)});
            g.DrawLine(pen,142,173,191,173);
        }
    }
    public static void Generate(string folder) {
        int[] sizes={16,24,32,48,64,128,256}; var pngs=new byte[sizes.Length][];
        for(int i=0;i<sizes.Length;i++) {
            // Supersampling keeps the narrow rim and diagonal prompt clean at tray sizes.
            int renderSize=sizes[i]*4;
            using(var source=new Bitmap(renderSize,renderSize,PixelFormat.Format32bppArgb))
            using(var canvas=Graphics.FromImage(source))
            using(var bmp=new Bitmap(sizes[i],sizes[i],PixelFormat.Format32bppArgb))
            using(var g=Graphics.FromImage(bmp)) {
                canvas.Clear(Color.Transparent);canvas.SmoothingMode=SmoothingMode.AntiAlias;canvas.PixelOffsetMode=PixelOffsetMode.HighQuality;
                canvas.ScaleTransform(renderSize/256f,renderSize/256f);DrawIcon(canvas);
                g.Clear(Color.Transparent);g.CompositingMode=CompositingMode.SourceCopy;
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                using(var attributes=new ImageAttributes()) {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(source,new Rectangle(0,0,sizes[i],sizes[i]),0,0,renderSize,renderSize,GraphicsUnit.Pixel,attributes);
                }
                using(var buffer=new MemoryStream()) {bmp.Save(buffer,ImageFormat.Png);pngs[i]=buffer.ToArray();}
                if(sizes[i]==256) bmp.Save(Path.Combine(folder,"AppIcon.png"),ImageFormat.Png);
            }
        }
        using(var stream=File.Create(Path.Combine(folder,"AppIcon.ico"))) using(var writer=new BinaryWriter(stream)) {
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);
            int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++) {writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(pngs[i].Length);writer.Write(offset);offset+=pngs[i].Length;}
            foreach(var png in pngs) writer.Write(png);
        }
    }
}
'@
[TerminalNotesIconGenerator]::Generate((Join-Path $PSScriptRoot '../Assets'))
