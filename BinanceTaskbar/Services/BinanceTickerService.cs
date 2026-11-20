using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BinanceTaskbar.Models;

namespace BinanceTaskbar.Services;

public enum MarketConnectionState
{
    Connecting,
    Connected,
    Reconnecting,
    Offline
}

public sealed record TickerUpdate(
    string Symbol,
    decimal Price,
    decimal ChangePercent,
    DateTimeOffset EventTime);

public sealed record ConnectionStateChanged(MarketConnectionState State, string? Detail = null);

public sealed record KlineSnapshot(string Symbol, IReadOnlyList<KlinePoint> Points);

public sealed record KlineUpdate(string Symbol, KlinePoint Point);

public sealed class BinanceTickerService : IDisposable
{
    private const string RestEndpoint = "https://fapi.binance.com/fapi/v1/ticker/24hr";
    private const string WebSocketEndpoint = "wss://fstream.binance.com/stream?streams=";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Task? _pollingTask;
    private IReadOnlyList<string> _symbols = [];
    private string _klineInterval = "1m";
    private int _klineLimit = 14;
    private int _priceRefreshMilliseconds = 250;
    private readonly ConcurrentDictionary<string, decimal> _changePercents = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<TickerUpdate>? TickerUpdated;
    public event EventHandler<ConnectionStateChanged>? ConnectionChanged;
    public event EventHandler<KlineSnapshot>? KlinesSeeded;
    public event EventHandler<KlineUpdate>? KlineUpdated;

    public void Start(IReadOnlyList<string> symbols, string klineInterval = "1m", int klineLimit = 14, int priceRefreshMilliseconds = 250)
    {
        Stop();
        _changePercents.Clear();
        _klineInterval = string.IsNullOrWhiteSpace(klineInterval) ? "1m" : klineInterval.Trim().ToLowerInvariant();
        _klineLimit = Math.Clamp(klineLimit, 8, 24);
        _priceRefreshMilliseconds = Math.Clamp(priceRefreshMilliseconds, 200, 1000);

        _symbols = symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _cts = new CancellationTokenSource();
                _pollingTask = Task.Run(() => PollPricesAsync(_cts.Token));
_runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Reconnect()
    {
        if (_symbols.Count > 0)
        {
            Start(_symbols, _klineInterval, _klineLimit, _priceRefreshMilliseconds);
        }
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }
    }


    private async Task PollPricesAsync(CancellationToken cancellationToken)
    {
        if (_symbols.Count == 0)
        {
            return;
        }

        var effectiveInterval = Math.Max(_priceRefreshMilliseconds, _symbols.Count * 70);
        var cyclesPerSnapshot = Math.Max(1, (int)Math.Ceiling(5000d / effectiveInterval));
        var cycle = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAll(_symbols.Select(symbol => PollPriceAsync(symbol, cancellationToken)));

                if (cycle > 0 && cycle % cyclesPerSnapshot == 0)
                {
                    await LoadSnapshotAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Keep polling; transient REST errors should not stop live prices.
            }

            cycle++;
            try
            {
                await Task.Delay(effectiveInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        var url = "https://fapi.binance.com/fapi/v1/ticker/price?symbol=" + Uri.EscapeDataString(symbol);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (!root.TryGetProperty("price", out var priceElement) ||
            !decimal.TryParse(priceElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
        {
            return;
        }

        var responseSymbol = root.TryGetProperty("symbol", out var symbolElement)
            ? symbolElement.GetString() ?? symbol
            : symbol;
        var eventTime = root.TryGetProperty("time", out var timeElement) && timeElement.TryGetInt64(out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.UtcNow;

        TickerUpdated?.Invoke(this, new TickerUpdate(
            responseSymbol,
            price,
            _changePercents.GetValueOrDefault(responseSymbol),
            eventTime));
    }
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested && _symbols.Count > 0)
        {
            try
            {
                SetState(MarketConnectionState.Connecting, "正在连接 Binance Futures");

                try
                {
                    await LoadSnapshotAsync(cancellationToken);
                    await LoadKlineSnapshotsAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                }

                await RunWebSocketAsync(cancellationToken);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                var delaySeconds = Math.Min(30, 1 << Math.Min(5, attempt));
                SetState(MarketConnectionState.Reconnecting, $"连接中断，{delaySeconds} 秒后重试：{ShortMessage(ex)}");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        SetState(MarketConnectionState.Offline);
    }

    private async Task RunWebSocketAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("User-Agent", "BinanceTaskbar/1.0");

        var streams = string.Join(
            "/",
            _symbols.SelectMany(symbol =>
            {
                var streamSymbol = symbol.ToLowerInvariant();
                return new[]
                {
                    streamSymbol + "@ticker",
                    streamSymbol + "@kline_" + _klineInterval,
                    streamSymbol + "@aggTrade"
                };
            }));
        var uri = new Uri(WebSocketEndpoint + streams);

        await socket.ConnectAsync(uri, cancellationToken);
        SetState(MarketConnectionState.Connected);

        var receiveBuffer = new byte[32 * 1024];
        var messageBuffer = new MemoryStream(64 * 1024);

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            messageBuffer.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                result = await socket.ReceiveAsync(receiveBuffer, receiveTimeout.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                {
                    messageBuffer.Write(receiveBuffer, 0, result.Count);
                }
            }
            while (!result.EndOfMessage);

            if (messageBuffer.Length == 0)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(
                messageBuffer.GetBuffer(),
                0,
                checked((int)messageBuffer.Length));
            ParseMessage(json);
        }
    }

    private void ParseMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data))
            {
                return;
            }

            if (data.TryGetProperty("e", out var eventType))
            {
                if (string.Equals(eventType.GetString(), "aggTrade", StringComparison.Ordinal))
                {
                    ParseAggTrade(data);
                    return;
                }

                if (string.Equals(eventType.GetString(), "kline", StringComparison.Ordinal))
                {
                    ParseKline(data);
                    return;
                }
            }

            if (!data.TryGetProperty("s", out var symbolElement) ||
                !data.TryGetProperty("c", out var priceElement) ||
                !data.TryGetProperty("P", out var changeElement))
            {
                return;
            }

            var symbol = symbolElement.GetString();
            if (string.IsNullOrWhiteSpace(symbol) ||
                !decimal.TryParse(priceElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
            {
                return;
            }

            decimal.TryParse(changeElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var change);
            _changePercents[symbol] = change;
            var eventTime = data.TryGetProperty("E", out var timeElement) && timeElement.TryGetInt64(out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : DateTimeOffset.UtcNow;

            TickerUpdated?.Invoke(this, new TickerUpdate(symbol, price, change, eventTime));
        }
        catch (JsonException)
        {
        }
    }



    private void ParseAggTrade(JsonElement data)
    {
        if (!data.TryGetProperty("s", out var symbolElement) ||
            !data.TryGetProperty("p", out var priceElement) ||
            !decimal.TryParse(priceElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
        {
            return;
        }

        var symbol = symbolElement.GetString();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var change = _changePercents.GetValueOrDefault(symbol);
        var eventTime = data.TryGetProperty("T", out var timeElement) && timeElement.TryGetInt64(out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.UtcNow;

        TickerUpdated?.Invoke(this, new TickerUpdate(symbol, price, change, eventTime));
    }
    private void ParseKline(JsonElement data)
    {
        if (!data.TryGetProperty("s", out var symbolElement) ||
            !data.TryGetProperty("k", out var kline))
        {
            return;
        }

        var symbol = symbolElement.GetString();
        if (string.IsNullOrWhiteSpace(symbol) ||
            !kline.TryGetProperty("t", out var openTimeElement) ||
            !openTimeElement.TryGetInt64(out var openTime) ||
            !TryGetDecimal(kline, "o", out var open) ||
            !TryGetDecimal(kline, "h", out var high) ||
            !TryGetDecimal(kline, "l", out var low) ||
            !TryGetDecimal(kline, "c", out var close))
        {
            return;
        }

        KlineUpdated?.Invoke(this, new KlineUpdate(
            symbol,
            new KlinePoint(openTime, open, high, low, close)));
    }

    private static bool TryGetDecimal(JsonElement parent, string propertyName, out decimal value)
    {
        value = 0m;
        return parent.TryGetProperty(propertyName, out var element) &&
               decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private async Task LoadKlineSnapshotsAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_symbols.Select(symbol => LoadKlineSnapshotAsync(symbol, cancellationToken)));
    }

    private async Task LoadKlineSnapshotAsync(string symbol, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://fapi.binance.com/fapi/v1/klines" +
                      $"?symbol={Uri.EscapeDataString(symbol)}" +
                      $"&interval={Uri.EscapeDataString(_klineInterval)}" +
                      $"&limit={_klineLimit}";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var points = new List<KlinePoint>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 5 ||
                    !item[0].TryGetInt64(out var openTime) ||
                    !TryParseDecimal(item[1], out var open) ||
                    !TryParseDecimal(item[2], out var high) ||
                    !TryParseDecimal(item[3], out var low) ||
                    !TryParseDecimal(item[4], out var close))
                {
                    continue;
                }

                points.Add(new KlinePoint(openTime, open, high, low, close));
            }

            if (points.Count > 0)
            {
                KlinesSeeded?.Invoke(this, new KlineSnapshot(symbol, points));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The WebSocket kline stream can still populate the chart after a REST failure.
        }
    }

    private static bool TryParseDecimal(JsonElement element, out decimal value)
    {
        value = 0m;
        return decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
    private async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var symbolsJson = JsonSerializer.Serialize(_symbols);
        var url = RestEndpoint + "?symbols=" + Uri.EscapeDataString(symbolsJson);
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("symbol", out var symbolElement) ||
                !item.TryGetProperty("lastPrice", out var priceElement) ||
                !item.TryGetProperty("priceChangePercent", out var changeElement))
            {
                continue;
            }

            var symbol = symbolElement.GetString();
            if (string.IsNullOrWhiteSpace(symbol) ||
                !decimal.TryParse(priceElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
            {
                continue;
            }

            decimal.TryParse(changeElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var change);
            _changePercents[symbol] = change;
            var eventTime = item.TryGetProperty("closeTime", out var timeElement) && timeElement.TryGetInt64(out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : DateTimeOffset.UtcNow;

            TickerUpdated?.Invoke(this, new TickerUpdate(symbol, price, change, eventTime));
        }
    }

    private void SetState(MarketConnectionState state, string? detail = null)
    {
        ConnectionChanged?.Invoke(this, new ConnectionStateChanged(state, detail));
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

















