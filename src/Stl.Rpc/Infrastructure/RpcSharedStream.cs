using System.Diagnostics;
using Stl.Rpc.Internal;

namespace Stl.Rpc.Infrastructure;

#pragma warning disable MA0042

public abstract class RpcSharedStream(RpcStream stream) : WorkerBase, IRpcSharedObject
{
    protected static readonly Exception NoError = new();

    private ILogger? _log;
    private long _lastKeepAliveAt = CpuTimestamp.Now.Value;

    protected ILogger Log => _log ??= Peer.Hub.Services.LogFor(GetType());

    public RpcObjectId Id { get; } = stream.Id;
    public RpcObjectKind Kind { get; } = stream.Kind;
    public RpcStream Stream { get; } = stream;
    public RpcPeer Peer { get; } = stream.Peer!;
    public CpuTimestamp LastKeepAliveAt {
        get => new(Interlocked.Read(ref _lastKeepAliveAt));
        set => Interlocked.Exchange(ref _lastKeepAliveAt, value.Value);
    }

    Task IRpcObject.Reconnect(CancellationToken cancellationToken)
        => throw Stl.Internal.Errors.InternalError(
            $"This method should never be called on {nameof(RpcSharedStream)}.");

    void IRpcObject.Disconnect()
        => throw Stl.Internal.Errors.InternalError(
            $"This method should never be called on {nameof(RpcSharedStream)}.");

    public void KeepAlive()
        => LastKeepAliveAt = CpuTimestamp.Now;

    public abstract Task OnAck(long nextIndex, Guid hostId);
}

public sealed class RpcSharedStream<T>(RpcStream stream) : RpcSharedStream(stream)
{
    private readonly RpcSystemCallSender _systemCallSender = stream.Peer!.Hub.SystemCallSender;
    private readonly Channel<(long NextIndex, bool MustReset)> _acks = Channel.CreateUnbounded<(long, bool)>(
        new() {
            SingleReader = true,
            SingleWriter = true,
        });

    public new RpcStream<T> Stream { get; } = (RpcStream<T>)stream;

    protected override async Task DisposeAsyncCore()
    {
        _acks.Writer.TryWrite((long.MaxValue, false)); // Just in case
        try {
            await base.DisposeAsyncCore().ConfigureAwait(false);
        }
        finally {
            Peer.SharedObjects.Unregister(this);
        }
    }

    public override Task OnAck(long nextIndex, Guid hostId)
    {
        var mustReset = hostId != default;
        if (mustReset && !Equals(Stream.Id.HostId, hostId))
            return SendMissing();

        LastKeepAliveAt = CpuTimestamp.Now;
        lock (Lock) {
            var whenRunning = WhenRunning;
            if (whenRunning == null) {
                if (mustReset && nextIndex == 0)
                    this.Start();
                else
                    return SendMissing();
            }
            else if (whenRunning.IsCompleted)
                return SendMissing();

            _acks.Writer.TryWrite((nextIndex, mustReset)); // Must always succeed for unbounded channel
            return Task.CompletedTask;
        }
    }

    // Protected & private methods

    protected override async Task OnRun(CancellationToken cancellationToken)
    {
        IAsyncEnumerator<T>? enumerator = null;
        try {
            enumerator = Stream.GetLocalSource().GetAsyncEnumerator(cancellationToken);
            var isEnumerationEnded = false;
            var ackReader = _acks.Reader;
            var buffer = new RingBuffer<Result<T>>(Stream.AckAdvance + 1);
            var bufferStart = 0L;
            var index = 0L;
            var whenAckReady = ackReader.WaitToReadAsync(cancellationToken).AsTask();
            var whenMovedNext = SafeMoveNext(enumerator);
            var whenMovedNextAsTask = (Task<bool>?)null;
            while (true) {
                nextAck:
                // 1. Await for acknowledgement & process accumulated acknowledgements
                (long NextIndex, bool MustReset) ack = (-1L, false);
                if (!whenAckReady.IsCompleted) {
                    // Debug.WriteLine($"{Id}: ?ACK");
                    await whenAckReady.ConfigureAwait(false);
                }
                while (ackReader.TryRead(out var nextAck)) {
                    ack = nextAck;
                    // Debug.WriteLine($"{Id}: +ACK: {ack}");
                    if (ack.NextIndex == long.MaxValue)
                        return; // Client tells us it's done w/ this stream

                    if (ack.MustReset || index < ack.NextIndex)
                        index = ack.NextIndex;
                }
                whenAckReady = ackReader.WaitToReadAsync(cancellationToken).AsTask();
                if (ack.NextIndex < 0)
                    goto nextAck;

                // 2. Remove what's useless from buffer
                var bufferOffset = (int)(ack.NextIndex - bufferStart).Clamp(0, buffer.Count);
                buffer.MoveHead(bufferOffset);
                bufferStart += bufferOffset;

                // 3. Recalculate the next range to send
                var ackIndex = ack.NextIndex + Stream.AckPeriod;
                var maxIndex = ack.NextIndex + Stream.AckAdvance;
                if (index < bufferStart) {
                    // The requested item is somewhere before the buffer start position
                    await SendInvalidPosition(index).ConfigureAwait(false);
                    goto nextAck;
                }
                var bufferIndex = (int)(index - bufferStart);

                // 3. Send as much as we can
                while (index < maxIndex) {
                    Result<T> item;
                    // Add enough items to buffer
                    var missingCount = 1 + bufferIndex - buffer.Count;
                    while (missingCount-- > 0) {
                        if (isEnumerationEnded) {
                            // The requested item is somewhere after the sequence's end
                            await SendInvalidPosition(index).ConfigureAwait(false);
                            goto nextAck;
                        }

                        try {
                            if (whenAckReady.IsCompleted)
                                goto nextAck; // Got Ack, must restart
                            if (!whenMovedNext.IsCompleted) {
                                // Both tasks aren't completed yet
                                whenMovedNextAsTask ??= whenMovedNext.AsTask();
                                var tCompleted = await Task.WhenAny(whenAckReady, whenMovedNextAsTask).ConfigureAwait(false);
                                if (tCompleted == whenAckReady)
                                    goto nextAck; // Got Ack, must restart
                            }
                            var canMove = whenMovedNext.Result;
                            if (canMove) {
                                item = enumerator.Current;
                                whenMovedNextAsTask = null;
                                whenMovedNext = SafeMoveNext(enumerator);
                            }
                            else
                                item = Result.Error<T>(NoError);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                            item = Result.Error<T>(Errors.RpcStreamNotFound());
                        }
                        catch (Exception e) {
                            item = Result.Error<T>(e);
                        }

                        isEnumerationEnded |= item.HasError;
                        buffer.PushTail(item);
                    }

                    item = buffer[bufferIndex++];
                    await Send(index++, item).ConfigureAwait(false);
                    if (item.HasError)
                        goto nextAck; // It's the last item -> all we can do now is to wait for Ack
                }
            }
        }
        finally {
            _ = DisposeAsync();
            _ = enumerator?.DisposeAsync();
        }
    }

    private Task SendMissing()
        => _systemCallSender.Disconnect(Peer, new[] { Id.LocalId });

    private Task SendInvalidPosition(long index)
        => Send(index, Result.Error<T>(Errors.RpcStreamInvalidPosition()));

    private Task Send(long index, Result<T> item)
    {
        // Debug.WriteLine($"{Id}: <- #{index} (ack @ {ackIndex})");
        if (item.IsValue(out var value))
            return _systemCallSender.Item(Peer, Id.LocalId, index, value);

        var error = ReferenceEquals(item.Error, NoError) ? null : item.Error;
        return _systemCallSender.End(Peer, Id.LocalId, index, error);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<bool> SafeMoveNext(IAsyncEnumerator<T> enumerator)
    {
        try {
            return enumerator.MoveNextAsync();
        }
        catch (Exception e) {
            return ValueTaskExt.FromException<bool>(e);
        }
    }
}