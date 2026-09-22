namespace Offload.Core.Util;

/// <summary>
/// Прогресс длительной операции (установка, загрузка). Stage — текст для пользователя на русском.
/// Fraction — 0..1 или null (неопределённый прогресс).
/// </summary>
public sealed record StepProgress(string Stage, double? Fraction = null, string? Detail = null);
