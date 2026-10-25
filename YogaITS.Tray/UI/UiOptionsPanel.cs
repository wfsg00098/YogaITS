using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace YogaITS.Tray.UI;

internal sealed class UiOptionsPanel : IDisposable
{
    private readonly TrayMenuWindow _window;
    private readonly FeatureCard _card=new("UI选项");
    private readonly GlassOptionBar _style, _source;
    private readonly Button[] _nativeStyleButtons;
    private readonly OptionGroupPresentation _stylePresentation;
    private readonly AnimatedOptionHost _sourceHost, _imageHost;
    private readonly Button _imageButton;
    private bool _connected, _disposed, _picking;
    private string? _error;
    public Border View=>_card.View;
    public UiOptionsPanel(TrayMenuWindow window)
    {
        _window=window;
        _style=new(window,acceleration:true,options:["WinUI3","液态玻璃"],groupName:"UI选项");
        _nativeStyleButtons=new[]{"WinUI3","液态玻璃"}.Select((label,index)=>
        {
            var button=TrayMenuWindow.CreateOptionTile(label,index==0,false,TrayMenuWindow.OptionTileHeightDip,TrayMenuWindow.ControlCenterButtonFontSize);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,UiAccessibility.GroupedName("UI选项",label));
            button.Click+=(_,_)=>_style.RequestSelection(index);
            return button;
        }).ToArray();
        var nativeStyle=TrayMenuWindow.CreateTileGrid(_nativeStyleButtons,2,TrayMenuWindow.OptionTileHeightDip);
        _stylePresentation=new(_style,nativeStyle,()=>LiquidGlassWindow.EffectsEnabled(window),_style.CancelPreview);
        _source=new(window,acceleration:true,options:GlassBackgroundOptions.Choices.Select(choice=>choice.Label).ToArray(),groupName:"UI选项中的背景来源");
        _sourceHost=new(()=>LiquidGlassWindow.EffectsEnabled(window),p=>_source.SetReveal(p));
        _sourceHost.SetChild(_source);
        _imageButton=TrayMenuWindow.CreateOptionTile("选择图像",false,true,TrayMenuWindow.OptionTileHeightDip,TrayMenuWindow.ControlCenterButtonFontSize);
        _imageButton.Click+=async(_,_)=>await PickImageAsync();
        var imageGrid=TrayMenuWindow.CreateTileGrid([_imageButton],1,TrayMenuWindow.OptionTileHeightDip);
        _imageHost=new(()=>LiquidGlassWindow.EffectsEnabled(window),p=>WindowMaterial.SetReveal(imageGrid,p));
        _imageHost.SetChild(imageGrid);
        _style.SelectionRequested+=async index=>
        {
            _error=null;
            RefreshNative(index);
            try { await GlassSettingsController.SelectStyleAsync(index==0); }
            catch(Exception ex) { _error="UI切换失败："+ex.Message; }
            finally { if(!_disposed) Refresh(); }
        };
        _source.SelectionRequested+=async index=>
        {
            _error=null;
            try { await GlassSettingsController.SelectSourceAsync(GlassBackgroundOptions.Choices[index].Source); }
            catch(Exception ex) { _error="背景切换失败："+ex.Message; }
            finally { if(!_disposed) Refresh(); }
        };
        View.Loaded+=Loaded; View.Unloaded+=Unloaded;
        Refresh();
    }
    private void Loaded(object sender,RoutedEventArgs args)
    {
        if(_connected || _disposed) return; _connected=true;
        LiquidGlassWindow.PreferenceChanged+=Refresh;
        LiquidGlassWindow.AvailabilityChanged+=Refresh;
        LiquidGlassWindow.BackgroundStatusChanged+=Refresh;
        Refresh();
    }
    private void Unloaded(object sender,RoutedEventArgs args)=>Disconnect();
    private void Disconnect()
    {
        if(!_connected) return; _connected=false;
        LiquidGlassWindow.PreferenceChanged-=Refresh;
        LiquidGlassWindow.AvailabilityChanged-=Refresh;
        LiquidGlassWindow.BackgroundStatusChanged-=Refresh;
    }
    public void Refresh()
    {
        if(_disposed) return;
        bool glass=!LiquidGlassWindow.NativeRequested;
        _style.SetOptionEnabled(1,LiquidGlassWindow.Available(_window));
        RefreshNative();
        _style.SelectIndex(glass?1:0,animate:true);
        _source.SetOptionEnabled(GlassBackgroundOptions.ChoiceIndex(GlassBackgroundSource.TransparentDesktop),GlassCapability.CaptureSupported);
        _source.SelectIndex(GlassBackgroundOptions.ChoiceIndex(GlassBackgroundOptions.Source),animate:true);
        _sourceHost.SetExpanded(glass);
        bool image=glass && GlassBackgroundOptions.Source==GlassBackgroundSource.StaticImage;
        _imageHost.SetExpanded(image);
        _card.Update(true,body=>
        {
            _card.Content(body,_stylePresentation);
            _card.Note(body,"背景来源",visible:glass);
            _card.Content(body,_sourceHost);
            _card.Content(body,_imageHost);
        });
        string status=image ? GlassBackgroundOptions.ImagePath is { } path ? Path.GetFileName(path) : "请选择一张图像。" : "";
        if(glass && LiquidGlassWindow.ImageError(_window) is { } error) status=error;
        _card.SetStatus(_error??status);
    }
    private void RefreshNative(int? pending=null)
    {
        int selected=LiquidGlassWindow.NativeRequested?0:1;
        for(int i=0;i<_nativeStyleButtons.Length;i++)
        {
            _nativeStyleButtons[i].IsEnabled=pending is null && (i==0 || LiquidGlassWindow.Available(_window));
            TrayMenuWindow.RefreshOptionTile(_nativeStyleButtons[i],i==selected,pending==i);
        }
    }
    private async Task PickImageAsync()
    {
        if(_disposed || _picking) return;
        _picking=true; _error=null; _imageButton.IsEnabled=false;
        try
        {
            var picker=new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(_window));
            foreach(string extension in new[]{".jpg",".jpeg",".png",".bmp",".tif",".tiff",".webp"}) picker.FileTypeFilter.Add(extension);
            var file=await picker.PickSingleFileAsync();
            if(file is not null && !_disposed && GlassBackgroundOptions.Source==GlassBackgroundSource.StaticImage)
                await GlassSettingsController.SelectImageAsync(file.Path);
        }
        catch(Exception ex) { if(!_disposed) _error="无法选择图像："+ex.Message; }
        finally { _picking=false; if(!_disposed) { _imageButton.IsEnabled=true; Refresh(); } }
    }
    internal void RequestStyle(bool native)=>_style.RequestSelection(native?0:1);
    internal void Verify()
    {
        if(_style.SelectedIndex!=(LiquidGlassWindow.NativeRequested?0:1))
            throw new InvalidOperationException("UI style bar does not reflect the shared preference.");
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true; Disconnect();
        View.Loaded-=Loaded; View.Unloaded-=Unloaded;
        _sourceHost.Dispose(); _imageHost.Dispose(); _stylePresentation.Dispose(); _source.Dispose(); _card.Dispose();
    }
}
