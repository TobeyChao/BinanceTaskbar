using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using BinanceTaskbar.Models;
using BinanceTaskbar.Services;

namespace BinanceTaskbar;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _dockTimer;
    private readonly DispatcherTimer _priceRenderTimer;
    private readonly ConcurrentDictionary<string, TickerUpdate> _pendingTickerUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly BinanceTickerService _tickerService = new();
    private readonly StockTickerService _stockTickerService = new();
    private AppSettings _settings;
    private ThemePalette _palette;
    private MarketConnectionState _connectionState = MarketConnectionState.Connecting;
    private MarketConnectionState _cryptoState = MarketConnectionState.Connecting;
    private MarketConnectionState _stockState = MarketConnectionState.Connecting;
    private string? _cryptoDetail;
    private string? _stockDetail;
    private string _statusText = "正在连接行情";
    private Brush _statusBrush = Brushes.Orange;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconImage;
    private bool _dragging;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private NativeRect _dragStartWindow;
    private bool _allowClose;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        Coins = new ObservableCollection<TickerViewModel>();
        _palette = ThemeService.Create(settings.Theme, settings.BackgroundOpacity);

        InitializeComponent();
        DataContext = this;

        Width = settings.Width;
        Height = settings.Height;
        CreateCoins();
        ApplyTheme();
        BuildContextMenu();
        InitializeTrayIcon();

        SourceInitialized += (_, _) => TaskbarDockService.AttachToTaskbar(this);
        Loaded += MainWindow_OnLoaded;
        Closed += MainWindow_OnClosed;

        _dockTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, DockTimer_OnTick, Dispatcher);
        _dockTimer.Start();

        _priceRenderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(settings.PriceRefreshMilliseconds),
            DispatcherPriority.Render,
            PriceRenderTimer_OnTick,
            Dispatcher);
        _priceRenderTimer.Start();
    }

    public ObservableCollection<TickerViewModel> Coins { get; }

    public Brush PanelBackgroundBrush => _palette.WindowBackground;
    public Brush PanelBorderBrush => _palette.BorderBrush;
    public Brush PrimaryTextBrush => _palette.PrimaryText;
    public Brush SecondaryTextBrush => _palette.SecondaryText;
    public Brush SeparatorBrush => _palette.Separator;
    public Brush WarningBrush => _palette.Warning;
    public Brush CandleUpBrush => _palette.Up;
    public Brush CandleDownBrush => _palette.Down;
    public bool Show24hChange => _settings.Show24hChange;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set
        {
            if (ReferenceEquals(_statusBrush, value))
            {
                return;
            }

            _statusBrush = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        TaskbarDockService.DockToTaskbar(this, _settings);
        _tickerService.TickerUpdated += TickerService_OnTickerUpdated;
        _tickerService.ConnectionChanged += TickerService_OnConnectionChanged;
        _tickerService.KlinesSeeded += TickerService_OnKlinesSeeded;
        _tickerService.KlineUpdated += TickerService_OnKlineUpdated;
        _stockTickerService.TickerUpdated += TickerService_OnTickerUpdated;
        _stockTickerService.ConnectionChanged += StockTickerService_OnConnectionChanged;
        _tickerService.Start(_settings.Symbols, _settings.KlineInterval, _settings.KlineLimit, _settings.PriceRefreshMilliseconds);
        _stockTickerService.Start(_settings.Stocks);
        SystemEvents.DisplaySettingsChanged += SystemEvents_OnDisplaySettingsChanged;
    }

    private void DockTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_dragging && IsVisible)
        {
            TaskbarDockService.DockToTaskbar(this, _settings);
        }
    }

    private void SystemEvents_OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => TaskbarDockService.DockToTaskbar(this, _settings));
    }

    private void TickerService_OnTickerUpdated(object? sender, TickerUpdate update)
    {
        _pendingTickerUpdates[update.Symbol] = update;
    }

    private void PriceRenderTimer_OnTick(object? sender, EventArgs e)
    {
        if (_pendingTickerUpdates.IsEmpty)
        {
            return;
        }

        foreach (var symbol in _pendingTickerUpdates.Keys.ToArray())
        {
            if (!_pendingTickerUpdates.TryRemove(symbol, out var update))
            {
                continue;
            }

            var coin = Coins.FirstOrDefault(item =>
                string.Equals(item.Symbol, update.Symbol, StringComparison.OrdinalIgnoreCase));
            if (coin is null)
            {
                continue;
            }

            coin.Update(update.Price, update.ChangePercent, update.EventTime);
            coin.UpdateLastCandleFromPrice(update.Price, update.EventTime, _settings.KlineInterval);
            coin.ApplyTheme(_palette);
        }
    }

    private void TickerService_OnKlinesSeeded(object? sender, KlineSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var coin = Coins.FirstOrDefault(item =>
                string.Equals(item.Symbol, snapshot.Symbol, StringComparison.OrdinalIgnoreCase));
            coin?.SetKlines(snapshot.Points, _settings.KlineLimit);
        });
    }

    private void TickerService_OnKlineUpdated(object? sender, KlineUpdate update)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var coin = Coins.FirstOrDefault(item =>
                string.Equals(item.Symbol, update.Symbol, StringComparison.OrdinalIgnoreCase));
            coin?.UpsertKline(update.Point, _settings.KlineLimit);
        });
    }
    private void TickerService_OnConnectionChanged(object? sender, ConnectionStateChanged state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _cryptoState = state.State;
            _cryptoDetail = state.Detail;
            UpdateAggregateConnectionState();
        });
    }

    private void StockTickerService_OnConnectionChanged(object? sender, ConnectionStateChanged state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _stockState = state.State;
            _stockDetail = state.Detail;
            UpdateAggregateConnectionState();
        });
    }

    private void UpdateAggregateConnectionState()
    {
        var hasCrypto = _settings.Symbols.Count > 0;
        var hasStocks = _settings.Stocks.Count > 0;
        var cryptoReady = !hasCrypto || _cryptoState == MarketConnectionState.Connected;
        var stocksReady = !hasStocks || _stockState == MarketConnectionState.Connected;
        var allReady = cryptoReady && stocksReady;
        var anyReady = (hasCrypto && _cryptoState == MarketConnectionState.Connected) ||
                       (hasStocks && _stockState == MarketConnectionState.Connected);
        var allOffline = (!hasCrypto || _cryptoState == MarketConnectionState.Offline) &&
                         (!hasStocks || _stockState == MarketConnectionState.Offline);

        if (allReady)
        {
            _connectionState = MarketConnectionState.Connected;
            StatusText = "Binance 永续 + 美股行情已连接";
        }
        else if (allOffline)
        {
            _connectionState = MarketConnectionState.Offline;
            StatusText = JoinDetails("行情离线", _cryptoDetail, _stockDetail);
        }
        else if (anyReady)
        {
            _connectionState = MarketConnectionState.Reconnecting;
            StatusText = JoinDetails("部分行情正在重试", _cryptoDetail, _stockDetail);
        }
        else
        {
            _connectionState = MarketConnectionState.Connecting;
            StatusText = JoinDetails("正在连接行情", _cryptoDetail, _stockDetail);
        }

        StatusBrush = _connectionState switch
        {
            MarketConnectionState.Connected => _palette.Up,
            MarketConnectionState.Offline => _palette.Down,
            _ => _palette.Warning
        };
    }

    private static string JoinDetails(string prefix, params string?[] details)
    {
        var usefulDetails = details
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Select(detail => detail!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return usefulDetails.Length == 0 ? prefix : prefix + "；" + string.Join("；", usefulDetails);
    }

    private void CreateCoins()
    {
        Coins.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in _settings.Symbols)
        {
            if (!seen.Add(symbol))
            {
                continue;
            }

            var coin = new TickerViewModel(symbol, GetDisplayName(symbol), "USDT");
            coin.ApplyTheme(_palette);
            Coins.Add(coin);
        }

        foreach (var symbol in _settings.Stocks)
        {
            if (!seen.Add(symbol))
            {
                continue;
            }

            var coin = new TickerViewModel(symbol, GetDisplayName(symbol), "USD");
            coin.ApplyTheme(_palette);
            Coins.Add(coin);
        }
    }

    private static string GetDisplayName(string symbol) => symbol switch
    {
        "BTCUSDT" => "比特币",
        "NVDA" or "NVDAUSDT" => "英伟达",
        "SNDK" or "SNDKUSDT" => "闪迪",
        _ when symbol.EndsWith("USDT", StringComparison.Ordinal) => symbol[..^4],
        _ => symbol
    };

    private void ApplyTheme()
    {
        _palette = ThemeService.Create(_settings.Theme, _settings.BackgroundOpacity);

        foreach (var coin in Coins)
        {
            coin.ApplyTheme(_palette);
        }

        StatusBrush = _connectionState switch
        {
            MarketConnectionState.Connected => _palette.Up,
            MarketConnectionState.Offline => _palette.Down,
            _ => _palette.Warning
        };

        OnPropertyChanged(nameof(PanelBackgroundBrush));
        OnPropertyChanged(nameof(PanelBorderBrush));
        OnPropertyChanged(nameof(PrimaryTextBrush));
        OnPropertyChanged(nameof(SecondaryTextBrush));
        OnPropertyChanged(nameof(SeparatorBrush));
        OnPropertyChanged(nameof(WarningBrush));
        OnPropertyChanged(nameof(CandleUpBrush));
        OnPropertyChanged(nameof(CandleDownBrush));
        OnPropertyChanged(nameof(Show24hChange));
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var reconnectItem = new MenuItem { Header = "立即重连" };
        reconnectItem.Click += (_, _) => ReconnectAll();

        var changeItem = new MenuItem
        {
            Header = "显示 24h 涨跌",
            IsCheckable = true,
            IsChecked = _settings.Show24hChange
        };
        changeItem.Click += (_, _) =>
        {
            _settings.Show24hChange = changeItem.IsChecked;
            SettingsService.Save(_settings);
            OnPropertyChanged(nameof(Show24hChange));
        };

        var configItem = new MenuItem { Header = "打开配置文件" };
        configItem.Click += (_, _) => SettingsService.OpenInEditor();

        var reloadItem = new MenuItem { Header = "重新加载配置" };
        reloadItem.Click += (_, _) => ReloadSettings();

        var resetItem = new MenuItem { Header = "重置默认位置" };
        resetItem.Click += (_, _) =>
        {
            _settings.LeftOffset = 132;
            _settings.TopOffset = null;
            SettingsService.Save(_settings);
            TaskbarDockService.DockToTaskbar(this, _settings);
        };

        var lockPositionItem = new MenuItem
        {
            Header = "固定位置",
            IsCheckable = true,
            IsChecked = _settings.PositionLocked
        };
        lockPositionItem.Click += (_, _) =>
        {
            _settings.PositionLocked = lockPositionItem.IsChecked;
            SettingsService.Save(_settings);
        };
        var startupItem = new MenuItem
        {
            Header = "开机启动",
            IsCheckable = true,
            IsChecked = StartupService.IsEnabled()
        };
        startupItem.Click += (_, _) =>
        {
            if (!StartupService.SetEnabled(startupItem.IsChecked))
            {
                startupItem.IsChecked = StartupService.IsEnabled();
                System.Windows.MessageBox.Show("无法写入开机启动项，请检查当前用户权限。", "币安观察", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var hideItem = new MenuItem { Header = "隐藏到托盘" };
        hideItem.Click += (_, _) => Hide();

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(reconnectItem);
        menu.Items.Add(changeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(configItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(lockPositionItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(startupItem);
        menu.Items.Add(hideItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        menu.Opened += (_, _) =>
        {
            startupItem.IsChecked = StartupService.IsEnabled();
            changeItem.IsChecked = _settings.Show24hChange;
            lockPositionItem.IsChecked = _settings.PositionLocked;
        };

        ContextMenu = menu;
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            _trayIconImage = !string.IsNullOrWhiteSpace(processPath)
                ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
                : null;
            _trayIconImage ??= (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            var showItem = new System.Windows.Forms.ToolStripMenuItem("显示 / 隐藏");
            var reconnectItem = new System.Windows.Forms.ToolStripMenuItem("立即重连");
            var configItem = new System.Windows.Forms.ToolStripMenuItem("打开配置文件");
            var reloadItem = new System.Windows.Forms.ToolStripMenuItem("重新加载配置");
            var lockPositionItem = new System.Windows.Forms.ToolStripMenuItem("固定位置")
            {
                CheckOnClick = true,
                Checked = _settings.PositionLocked
            };
            var startupItem = new System.Windows.Forms.ToolStripMenuItem("开机启动")
            {
                CheckOnClick = true,
                Checked = StartupService.IsEnabled()
            };
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");

            showItem.Click += (_, _) => ToggleVisibility();
            reconnectItem.Click += (_, _) => ReconnectAll();
            configItem.Click += (_, _) => SettingsService.OpenInEditor();
            reloadItem.Click += (_, _) => ReloadSettings();
            lockPositionItem.Click += (_, _) =>
            {
                _settings.PositionLocked = lockPositionItem.Checked;
                SettingsService.Save(_settings);
            };
            startupItem.Click += (_, _) => { if (!StartupService.SetEnabled(startupItem.Checked)) startupItem.Checked = StartupService.IsEnabled(); };
            exitItem.Click += (_, _) => ExitApplication();

            menu.Items.Add(showItem);
            menu.Items.Add(reconnectItem);
            menu.Items.Add(configItem);
            menu.Items.Add(reloadItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(startupItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);
            menu.Opening += (_, _) =>
            {
                startupItem.Checked = StartupService.IsEnabled();
                lockPositionItem.Checked = _settings.PositionLocked;
            };

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = _trayIconImage,
                Text = "比特币 · 英伟达 · 闪迪行情",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => ToggleVisibility();
        }
        catch
        {
            // The price strip remains usable even if the tray icon cannot be created.
        }
    }

    private void ReconnectAll()
    {
        _tickerService.Reconnect();
        _stockTickerService.Reconnect();
    }

    private void ReloadSettings()
    {
        _settings = SettingsService.Load();
            _priceRenderTimer.Interval = TimeSpan.FromMilliseconds(_settings.PriceRefreshMilliseconds);
        Width = _settings.Width;
        Height = _settings.Height;
        _palette = ThemeService.Create(_settings.Theme, _settings.BackgroundOpacity);
        _cryptoState = MarketConnectionState.Connecting;
        _stockState = MarketConnectionState.Connecting;
        _cryptoDetail = null;
        _stockDetail = null;
        UpdateAggregateConnectionState();
        CreateCoins();
        ApplyTheme();
        _tickerService.Start(_settings.Symbols, _settings.KlineInterval, _settings.KlineLimit, _settings.PriceRefreshMilliseconds);
        _stockTickerService.Start(_settings.Stocks);
        TaskbarDockService.DockToTaskbar(this, _settings);
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        TaskbarDockService.DockToTaskbar(this, _settings);
    }

    private void ExitApplication()
    {
        _allowClose = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
        Close();
        Application.Current.Shutdown();
    }

    private void RootBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.PositionLocked ||
            e.LeftButton != MouseButtonState.Pressed ||
            !TaskbarDockService.TryGetCursorPosition(out var cursorX, out var cursorY) ||
            !TaskbarDockService.TryGetWindowRect(this, out var windowRect))
        {
            return;
        }

        _dragging = true;
        _dragStartCursorX = cursorX;
        _dragStartCursorY = cursorY;
        _dragStartWindow = windowRect;
        RootBorder.CaptureMouse();
        e.Handled = true;
    }

    private void RootBorder_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging ||
            e.LeftButton != MouseButtonState.Pressed ||
            !TaskbarDockService.TryGetCursorPosition(out var cursorX, out var cursorY) ||
            !TaskbarDockService.TryGetTaskbarRect(out var taskbar, out _))
        {
            return;
        }

        var x = _dragStartWindow.Left + cursorX - _dragStartCursorX;
        var y = _dragStartWindow.Top + cursorY - _dragStartCursorY;
        x = Math.Clamp(x, taskbar.Left, Math.Max(taskbar.Left, taskbar.Right - _dragStartWindow.Width));
        y = Math.Clamp(y, taskbar.Top, Math.Max(taskbar.Top, taskbar.Bottom - _dragStartWindow.Height));

        TaskbarDockService.MoveWindow(this, x, y);
        e.Handled = true;
    }

    private void RootBorder_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        RootBorder.ReleaseMouseCapture();
        TaskbarDockService.SaveCurrentOffsets(this, _settings);
        e.Handled = true;
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _dockTimer.Stop();
        _priceRenderTimer.Stop();
        _pendingTickerUpdates.Clear();
        SystemEvents.DisplaySettingsChanged -= SystemEvents_OnDisplaySettingsChanged;
        _tickerService.TickerUpdated -= TickerService_OnTickerUpdated;
        _tickerService.ConnectionChanged -= TickerService_OnConnectionChanged;
        _tickerService.KlinesSeeded -= TickerService_OnKlinesSeeded;
        _tickerService.KlineUpdated -= TickerService_OnKlineUpdated;
        _stockTickerService.TickerUpdated -= TickerService_OnTickerUpdated;
        _stockTickerService.ConnectionChanged -= StockTickerService_OnConnectionChanged;
        _tickerService.Dispose();
        _stockTickerService.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayIconImage?.Dispose();
        _trayIconImage = null;

        if (!_allowClose)
        {
            Application.Current.Shutdown();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

































