using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Sessions;

internal sealed class SessionEventChannel
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<Subscriber>> _subscribers = [];

    public SessionEventChannel(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public SessionEventFeed Subscribe(
        Guid threadId,
        long afterSequence)
    {
        var subscriber = new Subscriber(
            threadId,
            afterSequence,
            Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(_capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            }));
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(threadId, out var subscribers))
            {
                subscribers = [];
                _subscribers.Add(threadId, subscribers);
            }

            subscribers.Add(subscriber);
        }

        return new SessionEventFeed(this, subscriber);
    }

    public void Publish(SessionEvent sessionEvent)
    {
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(sessionEvent.ThreadId, out var subscribers))
            {
                return;
            }

            for (var index = subscribers.Count - 1; index >= 0; index--)
            {
                var subscriber = subscribers[index];
                if (sessionEvent.Sequence <= subscriber.AfterSequence)
                {
                    continue;
                }

                if (subscriber.Channel.Writer.TryWrite(sessionEvent))
                {
                    subscriber.AfterSequence = sessionEvent.Sequence;
                    continue;
                }

                subscriber.Channel.Writer.TryComplete(
                    new SessionSubscriptionException(
                        new SessionError(
                            SessionErrorCodes.SubscriberLagged,
                            "The session subscription could not keep up.",
                            IsRetryable: true)));
                subscribers.RemoveAt(index);
            }

            if (subscribers.Count == 0)
            {
                _subscribers.Remove(sessionEvent.ThreadId);
            }
        }
    }

    private async IAsyncEnumerable<SessionEvent> ReadAllAsync(
        Subscriber subscriber,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var sessionEvent in subscriber.Channel.Reader.ReadAllAsync(
                               cancellationToken))
            {
                yield return sessionEvent;
            }
        }
        finally
        {
            Remove(subscriber);
        }
    }

    private void Remove(Subscriber subscriber)
    {
        lock (_gate)
        {
            if (_subscribers.TryGetValue(subscriber.ThreadId, out var subscribers))
            {
                subscribers.Remove(subscriber);
                if (subscribers.Count == 0)
                {
                    _subscribers.Remove(subscriber.ThreadId);
                }
            }

            subscriber.Channel.Writer.TryComplete();
        }
    }

    internal sealed class SessionEventFeed(
        SessionEventChannel owner,
        Subscriber subscriber) : IAsyncEnumerable<SessionEvent>, IAsyncDisposable
    {
        public IAsyncEnumerator<SessionEvent> GetAsyncEnumerator(
            CancellationToken cancellationToken = default) =>
            owner.ReadAllAsync(subscriber, cancellationToken).GetAsyncEnumerator(
                cancellationToken);

        public ValueTask DisposeAsync()
        {
            owner.Remove(subscriber);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Subscriber(
        Guid threadId,
        long afterSequence,
        Channel<SessionEvent> channel)
    {
        public Guid ThreadId { get; } = threadId;

        public long AfterSequence { get; set; } = afterSequence;

        public Channel<SessionEvent> Channel { get; } = channel;
    }
}
