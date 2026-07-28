using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;

namespace OpenCoWork.App;

public static class ChatCommandRunner
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        Guid? requestedThreadId,
        string? providerId,
        string? modelId,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool isInteractive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var sessions = services.GetRequiredService<ISessionService>();
        var models = services.GetRequiredService<ModelsConfig>();
        var redactor = services.GetRequiredService<SecretRedactor>();
        var thread = await ResolveThreadAsync(
            services,
            sessions,
            models,
            requestedThreadId,
            providerId,
            modelId,
            error,
            redactor,
            cancellationToken);
        if (thread is null)
        {
            return 1;
        }

        await error.WriteLineAsync($"thread {thread.ThreadId:D}");
        await error.WriteLineAsync(
            $"name {redactor.RedactText(thread.DisplayName)}");
        await error.WriteLineAsync(
            thread.AgentMode == AgentMode.Plan ? "mode plan" : "mode agent");
        await error.WriteLineAsync($"provider {thread.ProviderId}");
        await error.WriteLineAsync($"model {thread.ModelId}");
        var reader = new BoundedLineReader(input);
        var inputCancellation = cancellationToken;
        while (true)
        {
            if (isInteractive)
            {
                await error.WriteAsync("> ");
            }

            BoundedLineResult line;
            try
            {
                line = await reader.ReadAsync(inputCancellation);
            }
            catch (OperationCanceledException)
            {
                return isInteractive ? 0 : 1;
            }

            if (line.Status == BoundedLineStatus.EndOfStream)
            {
                return 0;
            }

            if (line.Status is BoundedLineStatus.Invalid or BoundedLineStatus.TooLarge)
            {
                var code = line.Status == BoundedLineStatus.TooLarge
                    ? AgentErrorCodes.ContextInputTooLarge
                    : AgentErrorCodes.ContextInputInvalid;
                await error.WriteLineAsync(
                    $"error[{code}]: Input is invalid.");
                if (!isInteractive)
                {
                    return 1;
                }

                continue;
            }

            var text = line.Text!;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (isInteractive)
            {
                var command = text.Trim();
                if (string.Equals(command, "/exit", StringComparison.Ordinal))
                {
                    return 0;
                }

                if (string.Equals(command, "/mode agent", StringComparison.Ordinal) ||
                    string.Equals(command, "/mode plan", StringComparison.Ordinal))
                {
                    var mode = command.EndsWith(
                        "plan",
                        StringComparison.Ordinal)
                        ? AgentMode.Plan
                        : AgentMode.Agent;
                    if (!await SetModeAsync(
                            sessions,
                            thread.ThreadId,
                            mode,
                            error,
                            redactor,
                            inputCancellation))
                    {
                        return 1;
                    }

                    continue;
                }

                if (command.StartsWith("//", StringComparison.Ordinal))
                {
                    text = text.Remove(
                        text.IndexOf("//", StringComparison.Ordinal),
                        1);
                }
            }

            var turn = await RunTurnAsync(
                sessions,
                thread.ThreadId,
                text,
                reader,
                output,
                error,
                isInteractive,
                redactor,
                inputCancellation);
            if (turn == ChatTurnResult.Cancelled && isInteractive)
            {
                inputCancellation = CancellationToken.None;
                continue;
            }

            if (turn != ChatTurnResult.Completed && !isInteractive)
            {
                return 1;
            }
        }
    }

    private static async Task<ThreadSnapshot?> ResolveThreadAsync(
        IServiceProvider services,
        ISessionService sessions,
        ModelsConfig models,
        Guid? requestedThreadId,
        string? providerId,
        string? modelId,
        TextWriter error,
        SecretRedactor redactor,
        CancellationToken cancellationToken)
    {
        if (requestedThreadId is null)
        {
            var selectedProvider = providerId ?? models.DefaultProvider;
            var selectedModel = modelId ?? models.DefaultModel;
            services.ValidateOpenCoWorkAgentModel(
                selectedProvider,
                selectedModel);
            var created = await sessions.CreateThreadAsync(
                new CreateThreadRequest(
                    Guid.CreateVersion7(),
                    ExpectedSequence: 0,
                    ProviderId: selectedProvider,
                    ModelId: selectedModel),
                cancellationToken);
            return await ValueOrErrorAsync(created, error, redactor);
        }

        var found = await sessions.GetThreadAsync(
            requestedThreadId.Value,
            cancellationToken);
        if (!found.IsSuccess || found.Value is null)
        {
            await WriteErrorAsync(found.Error, error, redactor);
            return null;
        }

        var thread = found.Value;
        var selectedResumeProvider = providerId ?? thread.ProviderId;
        var selectedResumeModel = modelId ?? thread.ModelId;
        if (string.IsNullOrWhiteSpace(selectedResumeProvider) ||
            string.IsNullOrWhiteSpace(selectedResumeModel))
        {
            await error.WriteLineAsync(
                $"error[{AgentErrorCodes.ContextInputInvalid}]: " +
                "Thread model selection is missing.");
            return null;
        }

        services.ValidateOpenCoWorkAgentModel(
            selectedResumeProvider,
            selectedResumeModel);
        if (providerId is null ||
            string.Equals(thread.ProviderId, selectedResumeProvider, StringComparison.Ordinal) &&
            string.Equals(thread.ModelId, selectedResumeModel, StringComparison.Ordinal))
        {
            return thread;
        }

        var changed = await sessions.SetThreadModelAsync(
            new SetThreadModelRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                selectedResumeProvider,
                selectedResumeModel),
            cancellationToken);
        return await ValueOrErrorAsync(changed, error, redactor);
    }

    private static async Task<bool> SetModeAsync(
        ISessionService sessions,
        Guid threadId,
        AgentMode mode,
        TextWriter error,
        SecretRedactor redactor,
        CancellationToken cancellationToken)
    {
        var current = await sessions.GetThreadAsync(threadId, cancellationToken);
        if (!current.IsSuccess || current.Value is null)
        {
            await WriteErrorAsync(current.Error, error, redactor);
            return false;
        }

        var changed = await sessions.SetAgentModeAsync(
            new SetAgentModeRequest(
                threadId,
                Guid.CreateVersion7(),
                current.Value.CurrentSequence,
                mode),
            cancellationToken);
        if (changed.Status == SessionCommandStatus.Rejected)
        {
            await WriteErrorAsync(changed.Error, error, redactor);
            return false;
        }

        await error.WriteLineAsync(
            mode == AgentMode.Plan ? "mode plan" : "mode agent");
        return true;
    }

    private static async Task<ChatTurnResult> RunTurnAsync(
        ISessionService sessions,
        Guid threadId,
        string text,
        BoundedLineReader reader,
        TextWriter output,
        TextWriter error,
        bool isInteractive,
        SecretRedactor redactor,
        CancellationToken cancellationToken)
    {
        await using var subscription = await sessions.SubscribeAsync(
            new SessionSubscriptionRequest(
                threadId,
                SessionSubscriptionMode.SnapshotThenLive),
            cancellationToken);
        var queued = await sessions.EnqueueInputAsync(
            new EnqueueInputRequest(
                threadId,
                Guid.CreateVersion7(),
                subscription.Snapshot.CurrentSequence,
                text),
            cancellationToken);
        if (queued.Status == SessionCommandStatus.Rejected)
        {
            await WriteErrorAsync(queued.Error, error, redactor);
            return ChatTurnResult.Failed;
        }

        var offsets = new Dictionary<Guid, int>();
        Guid? turnId = null;
        Guid? invocationId = null;
        var wroteContent = false;
        var contentEndsWithNewline = false;
        var reasoningStarted = false;
        var cancellationObserved = false;
        var cancelRequested = false;
        var resolvedApprovals = new HashSet<Guid>();
        var cancellation = cancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : null;
        await using var events = subscription.Events.GetAsyncEnumerator();
        while (true)
        {
            var moveNext = events.MoveNextAsync().AsTask();
            if (!cancellationObserved &&
                cancellation is not null &&
                await Task.WhenAny(moveNext, cancellation) == cancellation)
            {
                cancellationObserved = true;
                cancelRequested = true;
                await CancelActiveTurnAsync(sessions, threadId);
            }

            if (!await moveNext)
            {
                await error.WriteLineAsync(
                    "error[session.subscriptionEnded]: Session event stream ended.");
                return ChatTurnResult.Failed;
            }

            var sessionEvent = events.Current;
            if (sessionEvent.Type == SessionEventType.TurnStarted)
            {
                turnId = sessionEvent.Payload.Turn?.TurnId;
                if (cancelRequested)
                {
                    await CancelActiveTurnAsync(sessions, threadId);
                }
            }
            else if (sessionEvent.Type ==
                     SessionEventType.AgentInvocationSnapshotRecorded)
            {
                invocationId = sessionEvent.Payload.Invocation?.InvocationId;
            }
            else if (sessionEvent.Type == SessionEventType.ItemStarted &&
                     sessionEvent.Payload.Item is { } started)
            {
                offsets[started.ItemId] =
                    (started.Content as TextItemContent)?.Text.Length ?? 0;
                if (started.Content is SystemNoticeContent notice)
                {
                    await error.WriteLineAsync(notice.Message);
                }
            }
            else if (sessionEvent.Type == SessionEventType.ItemDeltaAppended &&
                     sessionEvent.Payload.Item is { Content: TextItemContent content } item)
            {
                var offset = offsets.GetValueOrDefault(item.ItemId);
                if (offset > content.Text.Length)
                {
                    offset = 0;
                }

                var delta = content.Text[offset..];
                offsets[item.ItemId] = content.Text.Length;
                if (item.Type == SessionItemType.AgentMessage)
                {
                    await output.WriteAsync(delta);
                    wroteContent |= delta.Length != 0;
                    if (delta.Length != 0)
                    {
                        contentEndsWithNewline =
                            delta.EndsWith('\n');
                    }
                }
                else if (item.Type == SessionItemType.Reasoning)
                {
                    if (isInteractive && !reasoningStarted)
                    {
                        await error.WriteAsync("thinking> ");
                        reasoningStarted = true;
                    }

                    await error.WriteAsync(delta);
                }
            }
            else if (sessionEvent.Type == SessionEventType.TurnWaitingApproval &&
                     sessionEvent.Payload.Interaction is { } interaction &&
                     sessionEvent.Payload.Item?.Content is ApprovalRequestContent approval &&
                     resolvedApprovals.Add(interaction.InteractionId))
            {
                var approved = false;
                if (isInteractive)
                {
                    await error.WriteLineAsync(
                        $"approval> {redactor.RedactText(approval.Prompt)}");
                    await error.WriteAsync("approve [y/N]> ");
                    BoundedLineResult response;
                    try
                    {
                        response = await reader.ReadAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        await CancelActiveTurnAsync(sessions, threadId);
                        return ChatTurnResult.Cancelled;
                    }

                    approved = response.Status == BoundedLineStatus.Line &&
                               response.Text is not null &&
                               (string.Equals(
                                    response.Text.Trim(),
                                    "y",
                                    StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(
                                    response.Text.Trim(),
                                    "yes",
                                    StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    await error.WriteLineAsync(
                        "approval denied in non-interactive mode");
                }

                var current = await sessions.GetThreadAsync(
                    threadId,
                    cancellationToken);
                if (!current.IsSuccess || current.Value is null)
                {
                    await WriteErrorAsync(current.Error, error, redactor);
                    return ChatTurnResult.Failed;
                }

                var resolved = await sessions.ResolveInteractionAsync(
                    new ResolveInteractionRequest(
                        threadId,
                        interaction.TurnId,
                        interaction.InteractionId,
                        new ApprovalResponseContent(approved, Comment: null),
                        Guid.CreateVersion7(),
                        current.Value.CurrentSequence),
                    cancellationToken);
                if (resolved.Status == SessionCommandStatus.Rejected)
                {
                    await WriteErrorAsync(resolved.Error, error, redactor);
                    return ChatTurnResult.Failed;
                }
            }

            if (turnId is null ||
                sessionEvent.Payload.Turn?.TurnId != turnId ||
                sessionEvent.Type is not (
                    SessionEventType.TurnCompleted or
                    SessionEventType.TurnFailed or
                    SessionEventType.TurnCancelled))
            {
                continue;
            }

            if (wroteContent && !contentEndsWithNewline)
            {
                await output.WriteLineAsync();
            }

            if (sessionEvent.Type == SessionEventType.TurnCompleted)
            {
                return ChatTurnResult.Completed;
            }

            if (sessionEvent.Type == SessionEventType.TurnCancelled)
            {
                await error.WriteLineAsync("cancelled");
                return ChatTurnResult.Cancelled;
            }

            await WriteErrorAsync(
                sessionEvent.Payload.Error,
                error,
                redactor);
            if (invocationId is not null)
            {
                await error.WriteLineAsync($"invocation {invocationId:D}");
            }

            return ChatTurnResult.Failed;
        }
    }

    private static async Task CancelActiveTurnAsync(
        ISessionService sessions,
        Guid threadId)
    {
        var current = await sessions.GetThreadAsync(
            threadId,
            CancellationToken.None);
        if (!current.IsSuccess ||
            current.Value?.ActiveTurnId is not { } turnId)
        {
            return;
        }

        await sessions.CancelTurnAsync(
            new CancelTurnRequest(
                threadId,
                turnId,
                Guid.CreateVersion7(),
                current.Value.CurrentSequence),
            CancellationToken.None);
    }

    private static async Task<T?> ValueOrErrorAsync<T>(
        SessionCommandResult<T> result,
        TextWriter error,
        SecretRedactor redactor)
    {
        if (result.Status != SessionCommandStatus.Rejected &&
            result.Value is not null)
        {
            return result.Value;
        }

        await WriteErrorAsync(result.Error, error, redactor);
        return default;
    }

    private static Task WriteErrorAsync(
        SessionError? failure,
        TextWriter error,
        SecretRedactor redactor) =>
        error.WriteLineAsync(
            failure is null
                ? "error[session.unknown]: Session operation failed."
                : $"error[{failure.Code}]: " +
                  redactor.RedactText(failure.Message));

    private enum ChatTurnResult
    {
        Completed,
        Failed,
        Cancelled,
    }
}

internal enum BoundedLineStatus
{
    Line,
    EndOfStream,
    Invalid,
    TooLarge,
}

internal sealed record BoundedLineResult(
    BoundedLineStatus Status,
    string? Text = null);

internal sealed class BoundedLineReader(TextReader input)
{
    private const int MaximumUtf8Bytes = 256 * 1024;
    private readonly char[] _buffer = new char[1];
    private bool _skipLineFeed;

    public async ValueTask<BoundedLineResult> ReadAsync(
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var byteCount = 0;
        var invalid = false;
        var tooLarge = false;
        char? highSurrogate = null;
        while (true)
        {
            var read = await input.ReadAsync(_buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                if (highSurrogate is not null)
                {
                    invalid = true;
                }

                if (text.Length == 0 && !invalid && !tooLarge)
                {
                    return new BoundedLineResult(BoundedLineStatus.EndOfStream);
                }

                return Result();
            }

            var value = _buffer[0];
            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (value == '\n')
                {
                    continue;
                }
            }

            if (value is '\r' or '\n')
            {
                _skipLineFeed = value == '\r';
                if (highSurrogate is not null)
                {
                    invalid = true;
                }

                return Result();
            }

            if (highSurrogate is { } high)
            {
                if (char.IsLowSurrogate(value))
                {
                    Append(high, 0);
                    Append(value, 4);
                    highSurrogate = null;
                    continue;
                }

                invalid = true;
                highSurrogate = null;
            }

            if (value == '\0' || char.IsLowSurrogate(value))
            {
                invalid = true;
                continue;
            }

            if (char.IsHighSurrogate(value))
            {
                highSurrogate = value;
                continue;
            }

            Append(
                value,
                value <= '\u007f'
                    ? 1
                    : value <= '\u07ff'
                        ? 2
                        : 3);
        }

        void Append(char value, int bytes)
        {
            byteCount += bytes;
            if (byteCount > MaximumUtf8Bytes)
            {
                tooLarge = true;
                return;
            }

            if (!invalid && !tooLarge)
            {
                text.Append(value);
            }
        }

        BoundedLineResult Result() =>
            tooLarge
                ? new BoundedLineResult(BoundedLineStatus.TooLarge)
                : invalid
                    ? new BoundedLineResult(BoundedLineStatus.Invalid)
                    : new BoundedLineResult(BoundedLineStatus.Line, text.ToString());
    }
}
