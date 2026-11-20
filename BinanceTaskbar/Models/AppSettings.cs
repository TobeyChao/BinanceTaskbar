namespace BinanceTaskbar.Models;

public sealed class AppSettings
{
    public List<string> Symbols { get; set; } = ["BTCUSDT", "NVDAUSDT", "SNDKUSDT"];

    public List<string> Stocks { get; set; } = [];
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 34;
    public double LeftOffset { get; set; } = 132;
    public double? TopOffset { get; set; }
    public double BackgroundOpacity { get; set; } = 0.95;
    public string Theme { get; set; } = "auto";
    public bool Show24hChange { get; set; } = true;
    public bool PositionLocked { get; set; } = true;
    public string KlineInterval { get; set; } = "1m";
    public int KlineLimit { get; set; } = 14;
    public int PriceRefreshMilliseconds { get; set; } = 250;

    public void Normalize()
    {
        Symbols = (Symbols ?? [])
            .Select(NormalizeSymbol)
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (Symbols.Count == 0)
        {
            Symbols = ["BTCUSDT", "NVDAUSDT", "SNDKUSDT"];
        }



        Stocks = (Stocks ?? [])
            .Select(symbol => (symbol ?? string.Empty).Trim().ToUpperInvariant())
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        Width = Math.Clamp(Width, 360, 1400);
        Height = Math.Clamp(Height, 28, 48);
        LeftOffset = Math.Max(0, LeftOffset);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.55, 1.0);
        KlineInterval = KlineInterval?.Trim().ToLowerInvariant() switch
        {
            "1m" or "3m" or "5m" or "15m" or "1h" or "4h" or "1d" => KlineInterval.Trim().ToLowerInvariant(),
            _ => "1m"
        };
        KlineLimit = Math.Clamp(KlineLimit, 8, 24);
        PriceRefreshMilliseconds = Math.Clamp(PriceRefreshMilliseconds, 200, 1000);
        Theme = Theme?.Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "auto"
        };
    }

    private static string NormalizeSymbol(string? symbol)
    {
        var value = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value.EndsWith("USDT", StringComparison.Ordinal) ? value : value + "USDT";
    }
}








