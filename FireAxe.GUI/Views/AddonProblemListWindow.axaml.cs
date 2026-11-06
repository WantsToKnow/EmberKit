using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using FireAxe.ViewModels;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace FireAxe.Views;

public partial class AddonProblemListWindow : ReactiveWindow<AddonProblemListViewModel>
{
    public AddonProblemListWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (Owner is not Window owner) return;
            var scale = owner.RenderScaling;
            var width = (int)(Width * scale);
            var height = (int)(Height * scale);
            var x = owner.Position.X + (int)(owner.Bounds.Width * scale) + 8;
            var y = owner.Position.Y + 42;
            if (Screens.ScreenFromWindow(owner)?.WorkingArea is { } area)
            {
                x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width - 8));
                y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height - 8));
            }
            Position = new PixelPoint(x, y);

            var workbench = owner.GetVisualDescendants().OfType<AddonNodeExplorerView>().FirstOrDefault();
            if (workbench is null) return;
            var margin = workbench.Margin;
            // Reserve the covered right edge so the floating list does not hide Mod switches.
            void UpdateSpace(object? sender, EventArgs args)
            {
                var ownerRight = owner.Position.X + owner.Bounds.Width * owner.RenderScaling;
                var besideRight = Position.X > owner.Position.X + owner.Bounds.Width * owner.RenderScaling / 2
                    && Position.Y < owner.Position.Y + owner.Bounds.Height * owner.RenderScaling
                    && Position.Y + Bounds.Height * RenderScaling > owner.Position.Y;
                var reserved = besideRight ? Math.Clamp((ownerRight - Position.X + 8) / owner.RenderScaling,
                    0, Math.Max(0, owner.Bounds.Width - 600)) : 0;
                workbench.Margin = new Thickness(margin.Left, margin.Top, margin.Right + reserved, margin.Bottom);
            }
            PositionChanged += UpdateSpace;
            SizeChanged += UpdateSpace;
            owner.PositionChanged += UpdateSpace;
            owner.SizeChanged += UpdateSpace;
            Closed += (_, _) =>
            {
                owner.PositionChanged -= UpdateSpace;
                owner.SizeChanged -= UpdateSpace;
                workbench.Margin = margin;
            };
            UpdateSpace(this, EventArgs.Empty);
        };

        this.WhenActivated((CompositeDisposable disposables) =>
        {

        });

        this.RegisterViewModelConnection((viewModel, disposables) =>
        {
            viewModel.RegisterInvalidHandler(Close)
                .DisposeWith(disposables);
            if (!viewModel.IsValid)
            {
                return;
            }
        });
    }
}
