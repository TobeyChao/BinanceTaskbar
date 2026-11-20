using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using BinanceTaskbar.Services;

namespace BinanceTaskbar.Models;

public sealed class TickerViewModel : INotifyPropertyChanged
{
    private decimal? _price;
    private decimal? _changePercent;
    private DateTimeOffset? _updatedAt;
    private Brush _changeBrush = Brushes.Gray;

    public TickerViewModel(string symbol, string? displayName = null, string quoteCurrency = "USDT")
    {
        Symbol = symbol;
        QuoteCurrency = quoteCurrency;
        DisplaySymbol = string.IsNullOrWhiteSpace(displayName)
            ? (symbol.EndsWith("USDT", StringComparison.Ordinal) ? symbol[..^4] : symbol)
            : displayName;
    }

    public string Symbol { get; }
    public string DisplaySymbol { get; }
    public string QuoteCurrency { get; }
    public ObservableCollection<KlinePoint> Candles { get; } = new();

    public string PriceText => _price is null
        ? "--"
        : _price.Value.ToString(GetPriceFormat(_price.Value, QuoteCurrency), CultureInfo.InvariantCulture);

    public string ChangeText
    {
        get
        {
            if (_changePercent is null)
            {
                return "--";
            }

            var arrow = _changePercent.Value >= 0 ? "▲" : "▼";
            return $"{arrow} {Math.Abs(_changePercent.Value):0.00}%";
        }
    }

    public Brush ChangeBrush
    {
        get => _changeBrush;
        private set
        {
            _changeBrush = value;
            OnPropertyChanged();
        }
    }

    public string ToolTipText => _updatedAt is null
        ? $"{Symbol}\n等待行情数据..."
        : $"{Symbol}\n最新价：{PriceText} {QuoteCurrency}\n24h 涨跌：{ChangeText}\n更新时间：{_updatedAt.Value.ToLocalTime():HH:mm:ss}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(decimal price, decimal changePercent, DateTimeOffset updatedAt)
    {
        _price = price;
        _changePercent = changePercent;
        _updatedAt = updatedAt;
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(ToolTipText));
    }


    public void SetKlines(IEnumerable<KlinePoint> points, int limit)
    {
        Candles.Clear();
        foreach (var point in points.TakeLast(Math.Max(1, limit)))
        {
            Candles.Add(point);
        }
    }

    public void UpsertKline(KlinePoint point, int limit)
    {
        var index = -1;
        for (var i = Candles.Count - 1; i >= 0; i--)
        {
            if (Candles[i].OpenTime == point.OpenTime)
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            Candles[index] = point;
        }
        else
        {
            Candles.Add(point);
        }

        while (Candles.Count > Math.Max(1, limit))
        {
            Candles.RemoveAt(0);
        }
    }

    public void UpdateLastCandleFromPrice(decimal price, DateTimeOffset eventTime, string interval)
    {
        var intervalMilliseconds = GetIntervalMilliseconds(interval);
        var unixMilliseconds = eventTime.ToUnixTimeMilliseconds();
        var openTime = unixMilliseconds - unixMilliseconds % intervalMilliseconds;

        if (Candles.Count == 0)
        {
            Candles.Add(new KlinePoint(openTime, price, price, price, price));
            return;
        }

        var lastIndex = Candles.Count - 1;
        var last = Candles[lastIndex];
        if (last.OpenTime == openTime)
        {
            Candles[lastIndex] = last with
            {
                High = Math.Max(last.High, price),
                Low = Math.Min(last.Low, price),
                Close = price
            };
            return;
        }

        if (last.OpenTime < openTime)
        {
            Candles.Add(new KlinePoint(openTime, price, price, price, price));
        }
    }

    private static long GetIntervalMilliseconds(string interval) => interval.ToLowerInvariant() switch
    {
        "1m" => 60_000,
        "3m" => 180_000,
        "5m" => 300_000,
        "15m" => 900_000,
        "1h" => 3_600_000,
        "4h" => 14_400_000,
        "1d" => 86_400_000,
        _ => 60_000
    };
    public void ApplyTheme(ThemePalette palette)
    {
        ChangeBrush = _changePercent switch
        {
            > 0 => palette.Up,
            < 0 => palette.Down,
            _ => palette.SecondaryText
        };
    }

    public void ResetPrice()
    {
        _price = null;
        _changePercent = null;
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(ToolTipText));
    }

    private static string GetPriceFormat(decimal price, string quoteCurrency) => quoteCurrency switch
    {
        "USD" => "#,##0.00",
        _ => price switch
        {
            >= 1000 => "#,##0.00",
            >= 1 => "#,##0.000",
            >= 0.1m => "0.0000",
            _ => "0.000000"
        }
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}









