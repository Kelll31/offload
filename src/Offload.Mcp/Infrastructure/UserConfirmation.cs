using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

/// <summary>Ответ пользователя на запрос подтверждения.</summary>
internal enum Confirmation
{
    /// <summary>Клиент не поддерживает elicitation — действует прежнее поведение инструмента.</summary>
    Unsupported,
    Confirmed,
    Declined,
}

/// <summary>
/// Подтверждение опасных действий у пользователя через elicitation (MCP 2025-06-18+): форма с одним флажком.
/// Спрашивает человека в IDE, а не модель: облачная модель не может «подтвердить» сама. Если клиент не объявил
/// capability elicitation (или только URL-режим) — <see cref="Confirmation.Unsupported"/>, и инструмент ведёт себя как раньше.
/// Ошибка или таймаут запроса — это отказ (безопасная сторона).
/// </summary>
internal static class UserConfirmation
{
    /// <summary>Сколько ждать ответа пользователя.</summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Клиент умеет form-elicitation (пустой объект elicitation по спецификации = form).</summary>
    public static bool IsSupported(McpServer? server) =>
        server?.ClientCapabilities?.Elicitation is { } e && (e.Form is not null || e.Url is null);

    /// <summary>
    /// Спросить пользователя. message — что именно будет сделано (на английском: его показывает IDE вместе с именем сервера).
    /// </summary>
    public static async Task<Confirmation> AskAsync(ToolContext ctx, string message, string checkboxTitle)
    {
        if (!IsSupported(ctx.Server)) return Confirmation.Unsupported;
        var request = new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = checkboxTitle, Default = false },
                },
                Required = ["confirm"],
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        cts.CancelAfter(Timeout);
        ctx.Progress.Report("Waiting for the user's confirmation in the IDE…");
        try
        {
            var result = await ctx.Server!.ElicitAsync(request, cts.Token).ConfigureAwait(false);
            var ok = result.IsAccepted && result.Content is { } content && content.TryGetValue("confirm", out var v)
                     && v.ValueKind == JsonValueKind.True;
            Log.Info("mcp", $"{ctx.Tool}: подтверждение пользователя — {(ok ? "да" : $"нет ({result.Action})")}");
            return ok ? Confirmation.Confirmed : Confirmation.Declined;
        }
        catch (OperationCanceledException) when (!ctx.Ct.IsCancellationRequested)
        {
            Log.Warn("mcp", $"{ctx.Tool}: пользователь не ответил на запрос подтверждения за {Timeout.TotalMinutes:0} мин");
            return Confirmation.Declined;
        }
        catch (Exception ex) when (ex is McpException or InvalidOperationException or JsonException)
        {
            Log.Warn("mcp", $"{ctx.Tool}: запрос подтверждения не удался: {ex.Message}");
            return Confirmation.Declined;
        }
    }
}
