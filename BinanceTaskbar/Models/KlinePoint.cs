namespace BinanceTaskbar.Models;

public readonly record struct KlinePoint(
    long OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close);
