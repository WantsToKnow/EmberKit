// Modified for EmberKit, based on ktxiaok/FireAxe 0.7.3; see MODIFICATIONS.md.
using System;

using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using ReactiveUI;

namespace FireAxe.ViewModels;

public class AddonNodeListItemViewModel : AddonNodeSimpleViewModel
{
    public string DisplayName => (Addon is RefAddonNode ? "↗ 引用 · " : "") + DisplayTitle;
    public string TargetSummary => Addon is AddonGroup ? "模组分组" : ModCatalogViewModel.GetVpk(Addon) is { } vpk && ModStorage.IsUninstalledDownload(vpk)
        ? vpk is WorkshopVpkAddon { DownloadCheckTask.IsCompleted: false } ? "下载中 · 未安装" : "未安装 · 可重试下载或删除"
        : ModCatalogViewModel.GetVpk(Addon)?.Inspection?.Summary ?? "等待识别…";
    public string TargetEvidence => ModCatalogViewModel.GetVpk(Addon)?.Inspection?.Evidence ?? "点击「重新识别」读取 VPK 内容。";

    public AddonNodeListItemViewModel(AddonNode addon) : base(addon)
    {
        
    }

    protected override void OnNewAddon(AddonNode addon, CompositeDisposable disposables)
    {
        base.OnNewAddon(addon, disposables);
        void Notify()
        {
            this.RaisePropertyChanged(nameof(DisplayName));
            this.RaisePropertyChanged(nameof(TargetSummary));
            this.RaisePropertyChanged(nameof(TargetEvidence));
        }
        addon.WhenAnyValue(node => node.Name).Subscribe(_ => Notify()).DisposeWith(disposables);
        this.WhenAnyValue(model => model.DisplayTitle).Subscribe(_ => Notify()).DisposeWith(disposables);
        if (ModCatalogViewModel.GetVpk(addon) is { } vpk)
        {
            vpk.WhenAnyValue(node => node.Inspection).Subscribe(_ => Notify()).DisposeWith(disposables);
            vpk.WhenAnyValue(node => node.VpkFilePath).Subscribe(_ => Notify()).DisposeWith(disposables);
            if (vpk is WorkshopVpkAddon workshop)
                workshop.WhenAnyValue(node => node.DownloadCheckTask).Subscribe(_ => Notify()).DisposeWith(disposables);
        }
    }
}
