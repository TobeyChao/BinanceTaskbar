using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace BinanceTaskbar.Services;

public sealed class StockTickerService : IDisposable
{
    private const string ChartEndpoint = "https://query1.finance.yahoo.com/v8/finance/chart/";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private CancellationTokenSource? _cts;
    private IReadOnlyList<string> _symbols = [];

    public event EventHandler<TickerUpdate>? TickerUpdated;
    public event EventHandler<ConnectionStateChanged>? ConnectionChanged;

    public StockTickerService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BinanceTaskbar/1.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void Start(IReadOnlyList<string> symbols)
    {
        Stop();
        _symbols = symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_symbols.Count == 0)
        {
            ConnectionChanged?.Invoke(this, new ConnectionStateChanged(MarketConnectionState.Offline, "未配置美股标的"));
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Reconnect()
    {
        if (_symbols.Count > 0)
        {
            Start(_symbols);
        }
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
        }

        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;

        while (!cancellationToken.IsCancellationRequested && _symbols.Count > 0)
        {
            try
            {
                var updates = await Task.WhenAll(_symbols.Select(symbol => FetchAsync(symbol, cancellationToken)));
                var validUpdates = updates.Where(update => update is not null).Cast<TickerUpdate>().ToArray();

                if (validUpdates.Length > 0)
                {
                    failures = 0;
                    ConnectionChanged?.Invoke(this, new ConnectionStateChanged(
                        MarketConnectionState.Connected,
                        $"美股行情已连接（Yahoo Finance，{PollInterval.TotalSeconds:0} 秒刷新）"));

                    foreach (var update in validUpdates)
                    {
                        TickerUpdated?.Invoke(this, update);
                    }
                }
                else
                {
                    failures++;
                    ConnectionChanged?.Invoke(this, new ConnectionStateChanged(
                        MarketConnectionState.Reconnecting,
                        "美股行情暂无有效数据，正在重试"));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                ConnectionChanged?.Invoke(this, new ConnectionStateChanged(
                    MarketConnectionState.Reconnecting,
                    $"美股行情连接中断：{ShortMessage(ex)}"));
            }

            var delaySeconds = failures == 0 ? PollInterval.TotalSeconds : Math.Min(60, PollInterval.TotalSeconds * Math.Max(1, failures));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        ConnectionChanged?.Invoke(this, new ConnectionStateChanged(MarketConnectionState.Offline));
    }

    private async Task<TickerUpdate?> FetchAsync(string symbol, CancellationToken cancellationToken)
    {
        var url = ChartEndpoint + Uri.EscapeDataString(symbol) +
                  "?interval=1m&range=1d&includePrePost=false&events=div%2Csplits";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("chart", out var chart) ||
            !chart.TryGetProperty("result", out var resultElement) ||
            resultElement.ValueKind != JsonValueKind.Array ||
            resultElement.GetArrayLength() == 0)
        {
            return null;
        }

        var meta = resultElement[0].GetProperty("meta");
        if (!meta.TryGetProperty("regularMarketPrice", out var priceElement) ||
            !TryGetDecimal(priceElement, out var price))
        {
            return null;
        }

        var previousClose = 0m;
        if (meta.TryGetProperty("chartPreviousClose", out var previousCloseElement))
        {
            TryGetDecimal(previousCloseElement, out previousClose);
        }

        if (previousClose <= 0 && meta.TryGetProperty("previousClose", out var previousCloseElement2))
        {
            TryGetDecimal(previousCloseElement2, out previousClose);
        }

        var changePercent = previousClose > 0
            ? (price / previousClose - 1m) * 100m
            : 0m;

        var eventTime = meta.TryGetProperty("regularMarketTime", out var timeElement) &&
                        timeElement.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UtcNow;

        var displaySymbol = meta.TryGetProperty("symbol", out var symbolElement)
            ? symbolElement.GetString() ?? symbol
            : symbol;

        return new TickerUpdate(displaySymbol, price, changePercent, eventTime);
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        value = 0m;
if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        return element.ValueKind == JsonValueKind.String &&
               decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string ShortMessage(Exception exception)
    {
        var message = exception.GetBaseException().Message.ReplaceLineEndings(" ").Trim();
        return message.Length > 80 ? message[..80] : message;
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}


