using System.Numerics;
using YogaITS.Tray.Demos.Acceleration;
using YogaITS.Tray.Demos.SwiftTabs;

internal static class ElasticLayoutChecks
{
    public static int Run()
    {
        int checks=0;
        void Check(bool valid,string message)
        {
            checks++;
            if(!valid) throw new InvalidOperationException("Elastic layout: "+message);
        }
        foreach(bool neutral in new[]{true,false})
        foreach(var delta in new[]{new Vector2(160,0),new Vector2(-160,0),new Vector2(0,160),new Vector2(0,-160)})
        {
            var model=new FourEdgePhysics(neutral,elasticLayout:true);
            var start=new TabFrame(0,0,140,48);
            model.Reset(start);
            var target=start with {X=delta.X,Y=delta.Y};
            model.SetTarget(target,layout:true);
            Check(model.Frame==start && model.Velocity==Vector4.Zero,"Layout changes preserve the integrated state");
            var direction=Vector2.Normalize(delta);
            float peakExtension=0,peakOvershoot=0;
            for(int i=0;i<1200;i++)
            {
                model.Advance(FourEdgePhysics.FixedStep);
                var frame=model.Frame;
                Check(float.IsFinite(frame.X+frame.Y+frame.Width+frame.Height) && frame.Width>=4 && frame.Height>=4,
                    "Repositioning retains ordered finite physical edges");
                peakExtension=Math.Max(peakExtension,delta.X!=0?frame.Width-start.Width:frame.Height-start.Height);
                peakOvershoot=Math.Max(peakOvershoot,Vector2.Dot(new(frame.Center-target.Center,frame.CenterY-target.CenterY),direction));
            }
            Check(peakExtension>4 && peakOvershoot>2,"Unpressed layout travel stretches, then rebounds rather than moving rigidly");
            Check(model.IsSettled && Math.Abs(model.Frame.Center-target.Center)<0.05 && Math.Abs(model.Frame.CenterY-target.CenterY)<0.05,
                "All travel directions settle at the exact destination");
        }
        foreach(float width in new[]{80f,180,350})
        {
            var model=new FourEdgePhysics(layoutOnly:true,elasticLayout:true);
            var start=new TabFrame(0,0,width,48);
            model.Reset(start); model.SetTarget(start with {Width=width*2});
            float maximum=width;
            for(int i=0;i<1200;i++)
            {
                model.Advance(FourEdgePhysics.FixedStep);
                Check(Math.Abs(model.Frame.X)<0.001 && Math.Abs(model.Frame.Y)<0.001 && Math.Abs(model.Frame.Height-48)<0.001,
                    "One-sided expansion never shifts the fixed or perpendicular edges");
                maximum=Math.Max(maximum,model.Frame.Width);
            }
            Check(maximum>width*2+2 && model.IsSettled,"Size changes have a visible rebound and then settle");
            model.SetPressed(true);
            for(int i=0;i<200;i++)
            {
                model.Advance(FourEdgePhysics.FixedStep);
                Check(Math.Abs(model.Frame.Center-width)<0.001 && Math.Abs(model.Frame.CenterY-24)<0.001,
                    "A later stationary press remains center anchored");
            }
        }
        {
            var model=new FourEdgePhysics(elasticLayout:true);
            var three=SwiftTabLayout.Create(600,80,3); var two=SwiftTabLayout.Create(600,80,2);
            model.Reset(three.Capsule(1));
            model.ConfigureTrack(three.Track,three.Capsule(0),three.Capsule(2));
            foreach(var layout in new[]{two,three,two,three,two})
            {
                var position=model.Position; var velocity=model.Velocity;
                model.ConfigureTrack(layout.Track,layout.Capsule(0),layout.Capsule(layout.Items.Length-1),preserveFrame:true);
                model.SetTarget(layout.Capsule(1),layout:true);
                Check(model.Position==position && model.Velocity==velocity,"Repeated count changes preserve current momentum");
                for(int i=0;i<20;i++) model.Advance(FourEdgePhysics.FixedStep);
            }
            for(int i=0;i<1600;i++) model.Advance(FourEdgePhysics.FixedStep);
            Check(model.IsSettled && Math.Abs(model.Frame.Width-two.Capsule(1).Width)<0.05,"Repeated add/remove settles at the surviving option");
        }
        {
            var control=new FourEdgePhysics(elasticLayout:true); var repeated=new FourEdgePhysics(elasticLayout:true);
            var start=new TabFrame(0,0,140,48); var target=start with {X=180};
            control.Reset(start); repeated.Reset(start); control.SetTarget(target); repeated.SetTarget(target);
            for(int i=0;i<300;i++)
            {
                if(i==6) repeated.SetTarget(target,layout:true);
                control.Advance(FourEdgePhysics.FixedStep); repeated.Advance(FourEdgePhysics.FixedStep);
                Check(control.Position==repeated.Position && control.Velocity==repeated.Velocity,
                    "A same-geometry layout notification does not replace an in-flight selection spring");
            }
        }
        return checks;
    }
}
