using System.Buffers;
using System.Text;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Agents;

internal sealed class BoundedSseReader(
    Stream stream,
    TimeProvider timeProvider,
    TimeSpan streamIdleTimeout)
{
    private const int MaximumEventBytes = 1024 * 1024;
    private const int MaximumBodyBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly byte[] _buffer = new byte[8192];
    private int _offset;
    private int _count;
    private int _bodyBytes;

    public async ValueTask<string?> ReadEventAsync(CancellationToken cancellationToken)
    {
        var data = new ArrayBufferWriter<byte>();
        var hasData = false;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return hasData ? DecodeEvent(data) : null;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    return DecodeEvent(data);
                }

                continue;
            }

            if (line[0] == (byte)':')
            {
                continue;
            }

            var colon = line.AsSpan().IndexOf((byte)':');
            var field = colon < 0 ? line.AsSpan() : line.AsSpan(0, colon);
            if (!field.SequenceEqual("data"u8))
            {
                continue;
            }

            var value = colon < 0 ? ReadOnlySpan<byte>.Empty : line.AsSpan(colon + 1);
            if (!value.IsEmpty && value[0] == (byte)' ')
            {
                value = value[1..];
            }

            if (data.WrittenCount + value.Length + 1 > MaximumEventBytes)
            {
                throw InvalidStream();
            }

            data.Write(value);
            data.Write("\n"u8);
            hasData = true;
        }
    }

    private async ValueTask<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (_offset == _count)
            {
                using var timeout = new CancellationTokenSource(
                    streamIdleTimeout,
                    timeProvider);
                using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
                try
                {
                    _count = await stream.ReadAsync(_buffer, readCancellation.Token);
                }
                catch (OperationCanceledException) when (
                    timeout.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    throw ProviderTimeout();
                }

                _offset = 0;
                if (_count == 0)
                {
                    return line.WrittenCount == 0
                        ? null
                        : TrimCarriageReturn(line.WrittenSpan).ToArray();
                }

                _bodyBytes = checked(_bodyBytes + _count);
                if (_bodyBytes > MaximumBodyBytes)
                {
                    throw new ProviderException(
                        AgentErrorCodes.ProviderOutputTooLarge,
                        "Provider response exceeded the size limit.");
                }
            }

            var available = _buffer.AsSpan(_offset, _count - _offset);
            var newline = available.IndexOf((byte)'\n');
            var length = newline < 0 ? available.Length : newline;
            if (line.WrittenCount + length > MaximumEventBytes)
            {
                throw InvalidStream();
            }

            line.Write(available[..length]);
            _offset += length + (newline >= 0 ? 1 : 0);
            if (newline >= 0)
            {
                return TrimCarriageReturn(line.WrittenSpan).ToArray();
            }
        }
    }

    private static string DecodeEvent(ArrayBufferWriter<byte> data)
    {
        try
        {
            return StrictUtf8.GetString(data.WrittenSpan[..^1]);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidStream();
        }
    }

    private static ReadOnlySpan<byte> TrimCarriageReturn(ReadOnlySpan<byte> line) =>
        !line.IsEmpty && line[^1] == (byte)'\r' ? line[..^1] : line;

    private static ProviderException InvalidStream() =>
        new(
            AgentErrorCodes.ProviderInvalidStream,
            "Provider returned an invalid streaming response.");

    private static ProviderException ProviderTimeout() =>
        new(
            AgentErrorCodes.ProviderTimeout,
            "Provider response timed out.",
            isTransient: true);
}
