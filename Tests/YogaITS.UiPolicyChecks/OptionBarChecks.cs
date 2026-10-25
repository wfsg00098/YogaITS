using YogaITS.Tray.UI.Controls;
using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Layout;

internal static class OptionBarChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool valid, string message)
        {
            checks++;
            if (!valid) throw new InvalidOperationException("Option bar: " + message);
        }
        foreach (int count in new[] { 2, 3, 4 })
        foreach (double width in new[] { 202.0, 208, 220, 244, 352, 600, 960 })
        {
            var layout = ChoiceTrackLayout.Create(width, 80, count);
            Check(layout.Items.Length == count && layout.Track.Width <= width,
                "Capability-dependent bars fit their real compact/full viewport");
            for (int index = 0; index < count; index++)
            {
                var item = layout.Capsule(index);
                Check(item.X >= 0 && item.X+item.Width <= width && item.Width > 0 && layout.Hit(item.Center) == index,
                    "Every visible option has a centered bounded hit target");
                var gesture = new FloatingTabGesture();
                Check(gesture.Begin(1,layout.Capsule(0).Center,0,layout), "Gesture begins with a compact/capability layout");
                gesture.Move(1,item.Center,out _);
                Check(gesture.ConfirmedIndex == 0, "Dragging never changes the confirmed value");
                Check(gesture.Release(1,item.Center,out int released) && released == index,
                    "Release selects every supported option without a hardcoded fourth slot");
            }
            var physics = new FourEdgePhysics();
            physics.Reset(layout.Capsule(0));
            physics.ConfigureTrack(layout.Track,layout.Capsule(0),layout.Capsule(count-1));
            physics.SetPressed(true);
            physics.DragTo(layout.Capsule(count-1) with { X = 10000 },1);
            for (int step=0;step<360;step++) physics.Advance(1.0/240);
            Check(float.IsFinite(physics.Frame.X+physics.Frame.Width) && physics.Frame.X+physics.Frame.Width <= width+6.01,
                "Compact bar physics retains the same outward endpoint limit");
        }
        foreach (int count in new[] { 3,4 })
        {
            var selection = new OptionSelectionState(count);
            selection.Confirm(1);
            Check(selection.Request(2) && selection.SelectedIndex == 1 && selection.DisplayIndex == 2 && selection.IsBusy,
                "A request moves the visual target while retaining confirmed state");
            Check(!selection.Request(0), "Pending readback rejects repeated input");
            selection.Confirm(1);
            Check(selection.SelectedIndex == 1 && !selection.IsBusy && selection.DisplayIndex == 1,
                "Failed/unconfirmed changes restore the actual prior selection");
            selection.SetEnabled(2,false);
            Check(!selection.Request(2) && selection.NextEnabled(1) != 2 && selection.NearestEnabled(2) != 2,
                "Disabled modes are skipped by pointer requests and keyboard navigation");
            selection.SetEnabled(2,true);
            Check(selection.Request(2), "Recovered capability accepts requests again");
            selection.Confirm(2);
            Check(selection.SelectedIndex == 2 && selection.PendingIndex is null,
                "Readback confirmation commits exactly one actual option");
            for (int i=0;i<count;i++) selection.SetEnabled(i,false);
            Check(!selection.Request(0), "Unavailable backends cannot accept input using stale state");
        }
        foreach (int count in new[] { 3, 4, 7 })
        {
            int columns = count == 7 ? 2 : count;
            var layout = ChoiceTrackLayout.Create(600, ChoiceTrackLayout.NaturalHeight(count, columns), count, columns);
            foreach (int disabled in Enumerable.Range(0, count))
            foreach (int current in Enumerable.Range(0, count).Where(index => index != disabled))
            {
                var selection = new OptionSelectionState(count);
                selection.Confirm(current);
                selection.SetEnabled(disabled, false);
                Check(!selection.Request(disabled) && selection.PendingIndex is null && selection.SelectedIndex == current,
                    "Clicking a disabled slot does not request another option or change the confirmed selection");
                var gesture = new FloatingTabGesture();
                var start = layout.Capsule(current);
                var destination = layout.Capsule(disabled);
                gesture.Begin(1, new System.Numerics.Vector2(start.Center, start.CenterY), current, layout);
                gesture.Move(1, new System.Numerics.Vector2(destination.Center, destination.CenterY), out _);
                Check(gesture.Release(1, new System.Numerics.Vector2(destination.Center, destination.CenterY), out int hit) && hit == disabled,
                    "Drag hit testing retains the actual disabled destination rather than choosing a neighbor");
                int released = selection.ReleaseTarget(hit);
                Check(released == current && !selection.Request(released) && selection.PendingIndex is null,
                    "Release over a disabled slot returns to the current option without activation");
                selection.SetEnabled(disabled, true);
                Check(selection.ReleaseTarget(disabled) == disabled && selection.Request(disabled),
                    "Re-enabling a slot permits normal selection again");
            }
        }
        {
            var selection = new OptionSelectionState(4);
            selection.Confirm(3);
            selection.SetEnabled(3, false);
            selection.Confirm(2); // Owner changes Creator to Performance.
            Check(selection.ReleaseTarget(3) == 2 && !selection.Request(3),
                "A stale pointer release follows the owner's actual fallback selection");
            selection.SetEnabled(3, true);
            Check(selection.SelectedIndex == 2 && selection.PendingIndex is null,
                "Re-enabling Creator does not change the current mode");
        }
        return checks;
    }
}
