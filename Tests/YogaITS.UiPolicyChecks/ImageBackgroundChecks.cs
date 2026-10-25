using YogaITS.Tray.UI.Glass.Backgrounds;

internal static class ImageBackgroundChecks
{
    public static int Run()
    {
        int count=0;
        void Check(bool value,string message) { count++; if(!value) throw new InvalidOperationException("Background: "+message); }
        foreach(double iw in new[]{80.0,400,1920,6000})
        foreach(double ih in new[]{60.0,200,1080,4000})
        foreach(double w in new[]{200.0,492,1000})
        foreach(double h in new[]{150.0,820,1200})
        {
            var rect=ImageBackgroundLayout.Cover(iw,ih,new(0,0,w,h));
            Check(rect.Width+0.001>=w && rect.Height+0.001>=h,"Uniform fill covers both axes");
            Check(Math.Abs(rect.Width/iw-rect.Height/ih)<0.000001,"Image aspect ratio is preserved");
            Check(Math.Abs(rect.X+(rect.Width-w)/2)<0.000001 && Math.Abs(rect.Y+(rect.Height-h)/2)<0.000001,"Overflow crops evenly about center");
            var fit=ImageBackgroundLayout.Place(iw,ih,new(-1920,100,w,h),WallpaperPosition.Fit);
            Check(fit.Width<=w+0.001 && fit.Height<=h+0.001,"Native wallpaper fit stays within the monitor");
        }
        Check(ImageBackgroundLayout.Cover(4000,2000,new(0,0,400,200)).Width==400,"Large images also downscale to fill");
        Check(GlassBackgroundOptions.Source==GlassBackgroundSource.DesktopWallpaper,"Startup defaults to wallpaper without capture");
        var expected=new[]{(GlassBackgroundSource.StaticImage,"静态图像"),
            (GlassBackgroundSource.DesktopWallpaper,"桌面壁纸"),(GlassBackgroundSource.TransparentDesktop,"透明桌面")};
        for(int i=0;i<expected.Length;i++)
            Check(GlassBackgroundOptions.Choices[i].Source==expected[i].Item1 &&
                GlassBackgroundOptions.Choices[i].Label==expected[i].Item2 &&
                GlassBackgroundOptions.ChoiceIndex(expected[i].Item1)==i,"Source labels and selection round-trip without enum casts");
        foreach(bool native in new[]{false,true})
        foreach(var source in Enum.GetValues<GlassBackgroundSource>())
            Check(GlassBackgroundOptions.NeedsCaptureConsent(native,source)==(!native && source==GlassBackgroundSource.TransparentDesktop),
                "Only transparent desktop in glass mode requests capture consent");
        var position=ImageBackgroundLayout.Place(100,200,new(-1920,0,1920,1080),WallpaperPosition.Stretch);
        Check(position==new ImageRect(-1920,0,1920,1080),"Wallpaper stretch follows its actual monitor bounds");
        return count;
    }
}
