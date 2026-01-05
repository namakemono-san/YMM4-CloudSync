using System.IO;
using System.Windows;
using YMM4CloudSync.Core.Commons;
using YMM4CloudSync.Core.Views;
using YukkuriMovieMaker.Plugin;

namespace YMM4CloudSync.Core;

public class Plugin : IPlugin, IToolPlugin
{
    public string Name => "YMM4 Cloud Sync";

    public Type ViewModelType => typeof(ToolView);
    public Type ViewType => typeof(ToolView);
    
    private static readonly string PluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
    private static readonly string LauncherPath = Path.Combine(PluginDirectory, "YMM4CloudSync.YMMX.Launcher.exe");
    private static readonly string IconPath = Path.Combine(PluginDirectory, "Resources", "YMMX_logo.ico");
    
    private readonly YmmxFileExtension _ymmxFileExtension = new(LauncherPath, IconPath);

    public Plugin()
    {
        CheckFileAssociation();
    }

    private void CheckFileAssociation()
    {
        if (_ymmxFileExtension.IsRegistered()) return;

        var result = MessageBox.Show(
            "YMM4 Cloud Sync用の拡張子がゆっくりMovieMaker4に関連付けられていません。\n以下の拡張子を関連付けしますか？\n\n- .ymmx: YMM4 Cloud Sync用拡張プロジェクトファイル\n\n関連付けると、各ファイルをダブルクリックでYMM4を起動できるようになります。",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.None
        );

        if (result != MessageBoxResult.Yes) return;
        
        _ymmxFileExtension.Register();
        MessageBox.Show("関連付けが完了しました。", "YMM4 CloudSync", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}