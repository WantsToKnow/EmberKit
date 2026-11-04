param(
    [Parameter(Mandatory=$true)][string]$Ffmpeg,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixtures = Join-Path $repo 'native\EmberFeedback\bin\Fixtures'
$assets = Join-Path $repo 'native\EmberFeedback\assets'
$frames = Join-Path $OutputDirectory 'frames'
New-Item -ItemType Directory -Path $frames -Force | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Text.Json
$code = @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
public static class FeedbackAnimationPreview {
    static Bitmap Crop(string path) {
        using (var original = new Bitmap(path)) {
            int l=original.Width,t=original.Height,r=-1,b=-1;
            var data=original.LockBits(new Rectangle(0,0,original.Width,original.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            var bytes=new byte[data.Stride*original.Height]; Marshal.Copy(data.Scan0,bytes,0,bytes.Length); original.UnlockBits(data);
            for(int y=0;y<original.Height;y++) for(int x=0;x<original.Width;x++) if(bytes[y*data.Stride+x*4+3]!=0) {
                l=Math.Min(l,x);t=Math.Min(t,y);r=Math.Max(r,x);b=Math.Max(b,y);
            }
            return original.Clone(new Rectangle(l,t,r-l+1,b-t+1),PixelFormat.Format32bppArgb);
        }
    }
    static void Blit(Graphics g, Bitmap image, float x,float y,float w,float h,float alpha,bool amber) {
        if(alpha<=0 || w<=0 || h<=0)return;
        using(var attributes=new ImageAttributes()) {
            var color=new ColorMatrix();color.Matrix33=alpha;
            if(amber){color.Matrix11=196f/255;color.Matrix22=72f/255;}
            attributes.SetColorMatrix(color);
            g.DrawImage(image,new Rectangle((int)(x-w/2),(int)(y-h/2),(int)w,(int)h),0,0,image.Width,image.Height,GraphicsUnit.Pixel,attributes);
        }
    }
    public static void Render(string assets,string fixtures,string output) {
        string jsonText=File.ReadAllText(Path.Combine(fixtures,"animation-preview.json"));
        using(var normal=Crop(Path.Combine(assets,"skull.png")))
        using(var head=Crop(Path.Combine(assets,"headshot.png")))
        using(var ring=new Bitmap(128,128,PixelFormat.Format32bppArgb))
        using(var json=JsonDocument.Parse(jsonText)) {
            var pixels=File.ReadAllBytes(Path.Combine(fixtures,"animation-ring.rgba"));
            var bits=ring.LockBits(new Rectangle(0,0,128,128),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            Marshal.Copy(pixels,0,bits.Scan0,pixels.Length);ring.UnlockBits(bits);
            int index=0;
            foreach(var frame in json.RootElement.GetProperty("frames").EnumerateArray()) {
                using(var canvas=new Bitmap(640,320,PixelFormat.Format32bppArgb))
                using(var g=Graphics.FromImage(canvas))
                using(var title=new Font("Microsoft YaHei",16,FontStyle.Regular,GraphicsUnit.Pixel))
                using(var label=new Font("Microsoft YaHei",14,FontStyle.Regular,GraphicsUnit.Pixel)) {
                    g.Clear(Color.FromArgb(22,27,36));g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.SmoothingMode=SmoothingMode.AntiAlias;
                    g.DrawString("击杀反馈动画 · 局部放大预览",title,Brushes.White,20,16);
                    g.DrawString("大到小落下 → 扩散圆环 → 左侧入列",label,Brushes.Silver,20,42);
                    float opacity=frame.GetProperty("alpha").GetSingle();
                    var skulls=frame.GetProperty("skulls");
                    for(int i=skulls.GetArrayLength()-1;i>=0;i--) {
                        var p=skulls[i];bool special=p.GetProperty("headshot").GetBoolean();
                        float ringSize=p.GetProperty("ringSize").GetSingle()*2;
                        Blit(g,ring,320+p.GetProperty("ringX").GetSingle()*2,200,ringSize,ringSize,opacity*p.GetProperty("ringAlpha").GetSingle(),special);
                        var image=special?head:normal;float h=54*p.GetProperty("scale").GetSingle();
                        Blit(g,image,320+p.GetProperty("x").GetSingle()*2,200+p.GetProperty("y").GetSingle()*2,h*image.Width/image.Height,h,opacity*p.GetProperty("alpha").GetSingle(),false);
                    }
                    g.DrawString("图标最终大小为原版一半；不显示连杀数字",label,Brushes.Silver,20,286);
                    canvas.Save(Path.Combine(output,"frame-"+index.ToString("D3")+".png"),ImageFormat.Png);index++;
                }
            }
        }
    }
}
'@
$references = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Point].Assembly.Location, [System.Text.Json.JsonDocument].Assembly.Location,
    'System.Runtime.dll','System.Collections.dll','System.Runtime.InteropServices.dll','System.ComponentModel.Primitives.dll','System.Memory.dll')
$references += @(Get-ChildItem -LiteralPath (Split-Path ([System.Drawing.Bitmap].Assembly.Location) -Parent) -Filter 'System.Private.Windows*.dll' | ForEach-Object FullName)
Add-Type -TypeDefinition $code -ReferencedAssemblies $references
[FeedbackAnimationPreview]::Render($assets, $fixtures, $frames)
$video = Join-Path $OutputDirectory 'kill-feedback-animation.mp4'
& $Ffmpeg -hide_banner -loglevel error -y -framerate 60 -i (Join-Path $frames 'frame-%03d.png') -c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -movflags +faststart $video
if ($LASTEXITCODE -ne 0) { throw 'Animation preview encoding failed.' }
Write-Output $video
