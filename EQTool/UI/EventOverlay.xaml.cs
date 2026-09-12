using EQTool.Models;
using EQTool.Services;
using EQTool.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EQTool.UI
{
    public class ChainOverlayData : ChainData
    {
        public Canvas Canvas { get; set; }
        public List<FrameworkElement> ChildrenInRow { get; set; }
        public string TargetName { get; set; }
        public int ActiveAnimations { get; set; } = 0;
        public RowDefinition RowDefinition { get; set; }
        public System.Windows.Threading.DispatcherTimer RemovalTimer { get; set; }
    }

    public class OverlayMessageData
    {
        public TextBlock TextBlock { get; set; }
        public System.Windows.Threading.DispatcherTimer RemovalTimer { get; set; }
    }

    public class TimerBarData
    {
        public string Name { get; set; }
        public List<FrameworkElement> ChildrenInRow { get; set; }
        public RowDefinition RowDefinition { get; set; }
        public System.Windows.Threading.DispatcherTimer TextTimer { get; set; }
    }

    public partial class EventOverlay : BaseSaveStateWindow
    {
        private readonly EQToolSettings settings;
        private readonly ActivePlayer activePlayer;
        private readonly List<ChainOverlayData> chainDatas = new List<ChainOverlayData>();
        private readonly List<TimerBarData> timerBarDatas = new List<TimerBarData>();
        private readonly List<OverlayMessageData> messageDatas = new List<OverlayMessageData>();
        private readonly IAppDispatcher appDispatcher;
        private readonly LogEvents logEvents;
        private readonly System.Windows.Threading.DispatcherTimer resizeChromeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        // The size the single center message used to be drawn at, kept so stacked messages look the same.
        private const double MessageFontSize = 44;

        public EventOverlay(LogEvents logEvents, EQToolSettings settings, EQToolSettingsLoad toolSettingsLoad, ActivePlayer activePlayer, IAppDispatcher appDispatcher, ConsoleViewModel consoleViewModel)
            : base(settings.OverlayWindowState, toolSettingsLoad, settings, consoleViewModel)
        {
            this.logEvents = logEvents;
            this.appDispatcher = appDispatcher;
            this.activePlayer = activePlayer;
            this.settings = settings;
            InitializeComponent();
            base.Init();
            Topmost = true;
            SaveState();
            resizeChromeTimer.Tick += (s, e) =>
            {
                if (Math.Abs((DateTime.Now - LastWindowInteraction).TotalSeconds) >= 10)
                {
                    resizeChromeTimer.Stop();
                    WindowResizeChrome.ResizeBorderThickness = new Thickness(0);
                    WindowBorder.BorderThickness = new Thickness(0, 0, 0, 0);
                }
            };
            logEvents.OverlayEvent += LogEvents_OverlayEvent;
            logEvents.CompleteHealEvent += LogParser_CHEvent;
            logEvents.TimerBarEvent += LogEvents_TimerBarEvent;
        }

        private void LogEvents_OverlayEvent(object sender, OverlayEvent e)
        {
            appDispatcher.DispatchUI(() => ShowMessage(e.Text, e.ForeGround, e.Duration));
        }

        private void ShowMessage(string text, Brush foreground, TimeSpan duration)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // A non-positive interval makes a DispatcherTimer fire on every idle, so the row would
            // flicker in and straight back out rather than being displayed.
            if (duration <= TimeSpan.Zero)
            {
                duration = TimeSpan.FromSeconds(5);
            }

            var message = new OverlayMessageData
            {
                TextBlock = new TextBlock
                {
                    Text = text,
                    Foreground = foreground ?? Brushes.Red,
                    FontSize = MessageFontSize,
                    FontWeight = FontWeights.Medium,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                },
                RemovalTimer = new System.Windows.Threading.DispatcherTimer { Interval = duration }
            };
            message.RemovalTimer.Tick += (s, e) =>
            {
                message.RemovalTimer.Stop();
                RemoveMessageRow(message);
            };

            _ = CenterTextPanel.Children.Add(message.TextBlock);
            messageDatas.Add(message);
            message.RemovalTimer.Start();
        }

        private void RemoveMessageRow(OverlayMessageData message)
        {
            if (!messageDatas.Remove(message))
            {
                return;
            }
            CenterTextPanel.Children.Remove(message.TextBlock);
        }

        private void LogParser_CHEvent(object sender, CompleteHealEvent e)
        {
            var overlay = activePlayer?.Player?.ChChainOverlay ?? false;
            var warningoverlay = activePlayer?.Player?.ChChainWarningOverlay ?? false;
            if (!overlay && !warningoverlay)
            {
                return;
            }

            appDispatcher.DispatchUI(() =>
            {
                var chaindata = GetOrCreateChain(e.Recipient);
                if (warningoverlay)
                {
                    var shouldwarn = CHService.ShouldWarnOfChain(chaindata, e);
                    if (shouldwarn)
                    {
                        ShowMessage("CH Chain Warning", Brushes.Red, TimeSpan.FromSeconds(2));
                    }
                }

                var random = new Random(DateTime.Now.Millisecond);
                var targetwidth = chaindata.Canvas.ActualWidth / 10.0;
                var target = new TextBlock
                {
                    FontSize = settings.FontSize.Value * 2,
                    Text = e.Position.ToString(),
                    Foreground = Brushes.White,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var textborder = new Border
                {
                    Height = 28,
                    Width = targetwidth,
                    Background = Brushes.ForestGreen,
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1),
                    Child = target
                };
                var animation = new DoubleAnimation
                {
                    From = -targetwidth,
                    To = chaindata.Canvas.ActualWidth,
                    Duration = TimeSpan.FromSeconds(11)
                };

                Storyboard.SetTarget(animation, textborder);
                Storyboard.SetTargetProperty(animation, new PropertyPath(Canvas.RightProperty));

                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);
                _ = chaindata.Canvas.Children.Add(textborder);

                storyboard.Completed += (s, ev) =>
                {
                    chaindata.ActiveAnimations--;
                    chaindata.Canvas.Children.Remove(textborder);
                    if (chaindata.ActiveAnimations <= 0)
                    {
                        chaindata.RemovalTimer.Stop();
                        chaindata.RemovalTimer.Start();
                    }
                };
                storyboard.Begin();
            });
        }

        private void RemoveChainRow(ChainOverlayData chaindata)
        {
            if (!chainDatas.Remove(chaindata))
            {
                return;
            }
            var rowremoved = Grid.GetRow(chaindata.ChildrenInRow.FirstOrDefault());
            Debug.WriteLine($"Removing Row {rowremoved}");
            foreach (var item in chaindata.ChildrenInRow)
            {
                ChainStackPanel.Children.Remove(item);
            }
            _ = ChainStackPanel.RowDefinitions.Remove(chaindata.RowDefinition);
            foreach (var item in chainDatas)
            {
                foreach (var cell in item.ChildrenInRow)
                {
                    var itemrow = Grid.GetRow(cell);
                    if (itemrow > rowremoved)
                    {
                        Debug.WriteLine($"Updating Row {itemrow} to {itemrow - 1}");
                        Grid.SetRow(cell, itemrow - 1);
                    }
                }
            }
            foreach (var item in timerBarDatas)
            {
                foreach (var cell in item.ChildrenInRow)
                {
                    var itemrow = Grid.GetRow(cell);
                    if (itemrow > rowremoved)
                    {
                        Grid.SetRow(cell, itemrow - 1);
                    }
                }
            }
        }

        // Picks black or white for the countdown text drawn over a timer bar, whichever has the
        // higher WCAG contrast ratio against the bar's fill color (white text vanishes on bright
        // bars like White/Gold; black text vanishes on dark ones).
        private static SolidColorBrush ContrastingTextBrush(Brush barBrush)
        {
            var color = (barBrush as SolidColorBrush)?.Color ?? Colors.SteelBlue;
            double Channel(double v)
            {
                v /= 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            var luminance = (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
            // Contrast vs white is 1.05/(L+0.05); vs black is (L+0.05)/0.05. White wins below
            // L ≈ 0.179 (sqrt(1.05*0.05) - 0.05), black wins above it.
            return luminance > 0.179 ? Brushes.Black : Brushes.White;
        }

        // The countdown number sits over both the bar's fill and its near-black drained portion as
        // the bar empties, so it needs contrast backing on every side, not just one. This was a
        // DropShadowEffect with ShadowDepth 0 - a glow rather than a shadow - but effects are pixel
        // shaders, among the likeliest features to fault under Wine/Proton's Direct3D9 translation.
        // Four offset copies behind the text approximate the same halo using nothing but TextBlocks.
        // Every layer is returned so whoever updates the countdown updates all of them together.
        private static List<TextBlock> AddCountdownText(Grid host, string text, Brush fill, Brush outline, double fontSize)
        {
            var layers = new List<TextBlock>();
            void AddLayer(Brush brush, double dx, double dy)
            {
                var layer = new TextBlock
                {
                    Text = text,
                    Foreground = brush,
                    FontSize = fontSize,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    // Left margin positions the text within the bar; the rest is the outline offset.
                    // Bottom cancels Top, otherwise a vertically centered layer would not shift.
                    Margin = new Thickness(10 + dx, dy, 0, -dy),
                    IsHitTestVisible = false
                };
                _ = host.Children.Add(layer);
                layers.Add(layer);
            }
            AddLayer(outline, -1, 0);
            AddLayer(outline, 1, 0);
            AddLayer(outline, 0, -1);
            AddLayer(outline, 0, 1);
            // added last so the real text draws over its outline
            AddLayer(fill, 0, 0);
            return layers;
        }

        private void RemoveTimerBarRow(TimerBarData timerdata)
        {
            // Removing the same row twice would run the row-shifting loops below a second time and
            // pull every row underneath it up one too many.
            if (!timerBarDatas.Remove(timerdata))
            {
                return;
            }
            var rowremoved = Grid.GetRow(timerdata.ChildrenInRow.FirstOrDefault());
            foreach (var item in timerdata.ChildrenInRow)
            {
                ChainStackPanel.Children.Remove(item);
            }
            _ = ChainStackPanel.RowDefinitions.Remove(timerdata.RowDefinition);
            foreach (var item in chainDatas)
            {
                foreach (var cell in item.ChildrenInRow)
                {
                    var itemrow = Grid.GetRow(cell);
                    if (itemrow > rowremoved)
                    {
                        Grid.SetRow(cell, itemrow - 1);
                    }
                }
            }
            foreach (var item in timerBarDatas)
            {
                foreach (var cell in item.ChildrenInRow)
                {
                    var itemrow = Grid.GetRow(cell);
                    if (itemrow > rowremoved)
                    {
                        Grid.SetRow(cell, itemrow - 1);
                    }
                }
            }
        }

        private void LogEvents_TimerBarEvent(object sender, Models.TimerBarEvent e)
        {
            appDispatcher.DispatchUI(() =>
            {
                var existing = timerBarDatas.FirstOrDefault(t => t.Name == e.Name);
                if (existing != null)
                {
                    existing.TextTimer?.Stop();
                    RemoveTimerBarRow(existing);
                }

                var timerdata = new TimerBarData
                {
                    Name = e.Name,
                    ChildrenInRow = new List<FrameworkElement>(),
                    RowDefinition = new RowDefinition { MaxHeight = settings.FontSize.Value * 2.2 }
                };

                var nameLabel = new TextBlock
                {
                    Height = settings.FontSize.Value * 2,
                    FontSize = settings.FontSize.Value * 1.5,
                    Text = e.Name,
                    Foreground = Brushes.Black,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                };
                var nameBorder = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Background = Brushes.Chocolate,
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Black,
                    Child = nameLabel
                };

                var barBrush = e.BarColor ?? Brushes.SteelBlue;

                // A Border/Grid rather than a ProgressBar. The stock control template differs per
                // Windows theme - the Aero one slides a glow rectangle across the bar continuously
                // even when it is determinate - so both what it costs to draw and what it looks like
                // depend on what Wine reports as the current theme. Two star-sized columns express
                // how full the bar is without measuring anything, so it also follows the overlay
                // being resized for free.
                var fillColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
                var drainedColumn = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
                var fillGrid = new Grid();
                fillGrid.ColumnDefinitions.Add(fillColumn);
                fillGrid.ColumnDefinitions.Add(drainedColumn);
                var fill = new Border
                {
                    Background = barBrush,
                    CornerRadius = new CornerRadius(2)
                };
                Grid.SetColumn(fill, 0);
                _ = fillGrid.Children.Add(fill);

                var trackBorder = new Border
                {
                    Height = settings.FontSize.Value * 2,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 10, 10, 10)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.White,
                    IsHitTestVisible = false,
                    Child = fillGrid
                };

                // Text color contrasts the bar's fill; the outline behind it is the opposite shade,
                // so the number stays readable over the drained part of the bar too.
                var textBrush = ContrastingTextBrush(barBrush);
                var outlineBrush = textBrush == Brushes.Black ? Brushes.White : Brushes.Black;

                var barGrid = new Grid();
                _ = barGrid.Children.Add(trackBorder);
                var countdownLayers = AddCountdownText(barGrid, e.TotalSeconds.ToString(), textBrush, outlineBrush, settings.FontSize.Value * 2);

                var getrow = ChainStackPanel.RowDefinitions.Count;
                Grid.SetRow(nameBorder, getrow);
                Grid.SetColumn(nameBorder, 0);
                Grid.SetRow(barGrid, getrow);
                Grid.SetColumn(barGrid, 1);

                ChainStackPanel.RowDefinitions.Add(timerdata.RowDefinition);
                _ = ChainStackPanel.Children.Add(nameBorder);
                _ = ChainStackPanel.Children.Add(barGrid);
                timerdata.ChildrenInRow.Add(nameBorder);
                timerdata.ChildrenInRow.Add(barGrid);
                timerBarDatas.Add(timerdata);

                // The bar's fill and countdown text are both driven off one plain DispatcherTimer tick
                // instead of a WPF Storyboard/DoubleAnimation. A Storyboard re-renders every frame for
                // the whole duration of every concurrent timer, which is heavy, continuous composition
                // work - exactly the kind of thing that destabilizes under Wine/Proton's rendering
                // translation on a raid with several timers running at once. A 200ms poll is imperceptible
                // for a countdown bar and removes that whole animation-clock dependency.
                var startTime = DateTime.UtcNow;
                var totalSeconds = e.TotalSeconds;
                var textTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                timerdata.TextTimer = textTimer;
                textTimer.Tick += (s, ev) =>
                {
                    var remaining = totalSeconds - (DateTime.UtcNow - startTime).TotalSeconds;
                    if (remaining <= 0)
                    {
                        textTimer.Stop();
                        RemoveTimerBarRow(timerdata);
                        return;
                    }
                    var remainingtext = Math.Ceiling(remaining).ToString();
                    foreach (var layer in countdownLayers)
                    {
                        layer.Text = remainingtext;
                    }
                    var fraction = remaining / totalSeconds;
                    fillColumn.Width = new GridLength(fraction, GridUnitType.Star);
                    drainedColumn.Width = new GridLength(1 - fraction, GridUnitType.Star);
                };
                textTimer.Start();
            });
        }

        private ChainOverlayData GetOrCreateChain(string targetname)
        {
            var chaindata = chainDatas.FirstOrDefault(a => a.TargetName == targetname);
            if (chaindata != null)
            {
                chaindata.ActiveAnimations += 1;
                return chaindata;
            }
            chaindata = new ChainOverlayData
            {
                Canvas = new Canvas(),
                ChildrenInRow = new List<FrameworkElement>(),
                TargetName = targetname,
                ActiveAnimations = 1,
                RowDefinition = new RowDefinition { MaxHeight = 30 },
                RemovalTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }
            };
            chaindata.RemovalTimer.Tick += (s, e) =>
            {
                chaindata.RemovalTimer.Stop();
                if (chaindata.ActiveAnimations <= 0)
                {
                    RemoveChainRow(chaindata);
                }
            };

            chaindata.Canvas.IsHitTestVisible = false;
            chaindata.Canvas.Background = Brushes.Transparent;
            var target = new TextBlock
            {
                Height = 30,
                FontSize = settings.FontSize.Value * 1.5,
                Text = targetname,
                Padding = new Thickness(4),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var textborder = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Chocolate,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Black,
                Child = target
            };
            var getrow = ChainStackPanel.RowDefinitions.Count;
            Debug.WriteLine($"Adding row {getrow}");
            Grid.SetRow(textborder, getrow);
            Grid.SetZIndex(textborder, 1);
            Grid.SetColumn(textborder, 0);
            Grid.SetRow(chaindata.Canvas, getrow);
            Grid.SetColumn(chaindata.Canvas, 1);
            Grid.SetZIndex(chaindata.Canvas, 0);
            var stackpanel = new StackPanel { Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 10, 10, 10)), Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 30 };
            Grid.SetRow(stackpanel, getrow);
            Grid.SetColumn(stackpanel, 1);
            ChainStackPanel.RowDefinitions.Add(chaindata.RowDefinition);
            _ = ChainStackPanel.Children.Add(textborder);
            _ = ChainStackPanel.Children.Add(stackpanel);
            _ = ChainStackPanel.Children.Add(chaindata.Canvas);
            chaindata.ChildrenInRow.Add(textborder);
            chaindata.ChildrenInRow.Add(stackpanel);
            chaindata.ChildrenInRow.Add(chaindata.Canvas);
            chainDatas.Add(chaindata);
            chaindata.Canvas.UpdateLayout();
            for (var i = 0; i < 10; i++)
            {
                _ = stackpanel.Children.Add(new Border
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = 30,
                    Width = chaindata.Canvas.ActualWidth / 10,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    BorderBrush = Brushes.WhiteSmoke,
                    BorderThickness = new Thickness(1, 0, 1, 2),
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        Foreground = Brushes.Red,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        Width = chaindata.Canvas.ActualWidth / 10,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    }
                });

            }
            return chaindata;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (logEvents != null)
            {
                logEvents.OverlayEvent -= LogEvents_OverlayEvent;
                logEvents.CompleteHealEvent -= LogParser_CHEvent;
                logEvents.TimerBarEvent -= LogEvents_TimerBarEvent;
            }
            resizeChromeTimer.Stop();
            foreach (var chaindata in chainDatas)
            {
                chaindata.RemovalTimer?.Stop();
            }
            foreach (var message in messageDatas)
            {
                message.RemovalTimer?.Stop();
            }
            // These drive the timer bars and tear their rows back down, so one left running past
            // the window's life keeps mutating a closed window - and keeps it alive to do it.
            foreach (var timerbar in timerBarDatas)
            {
                timerbar.TextTimer?.Stop();
            }

            base.OnClosing(e);
        }

        private void Grid_MouseEnter(object sender, MouseEventArgs e)
        {
            WindowResizeChrome.ResizeBorderThickness = new Thickness(8);
            WindowBorder.BorderThickness = new Thickness(1, 1, 1, 1);
            LastWindowInteraction = DateTime.Now;
        }

        private void Grid_MouseLeave(object sender, MouseEventArgs e)
        {
            resizeChromeTimer.Stop();
            resizeChromeTimer.Start();
        }
    }
}
