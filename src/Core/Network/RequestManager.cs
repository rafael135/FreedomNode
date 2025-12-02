using System.Collections.Concurrent;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;

namespace FalconNode.Core.Network;

/// <summary>
/// Manages network requests by tracking pending requests and handling their completion or timeout.
/// Provides thread-safe registration and completion of requests using unique identifiers.
/// </summary>
public class RequestManager
{
    /// <summary>
    /// A thread-safe dictionary that maps request IDs to their corresponding TaskCompletionSource objects.
    /// </summary>
    private readonly ConcurrentDictionary<
        uint,
        TaskCompletionSource<NetworkPacket>
    > _pendingRequests = new();

    /// <summary>
    /// A counter to generate unique request IDs.
    /// </summary>
    private int _nextId = 0;

    /// <summary>
    /// Generates the next unique request ID in a thread-safe manner.
    /// </summary>
    /// <returns></returns>
    public uint NextId() => (uint)Interlocked.Increment(ref _nextId);

    /// <summary>
    /// Registers a network request with a specified timeout and returns a task that completes when the request is fulfilled or times out.
    /// </summary>
    /// <param name="requestId">The unique identifier for the network request.</param>
    /// <param name="timeout">The duration to wait before timing out the request.</param>
    /// <returns>
    /// A <see cref="Task{NetworkPacket}"/> that completes when the network request is fulfilled or throws a <see cref="TimeoutException"/> if the timeout elapses.
    /// </returns>
    public Task<NetworkPacket> RegisterRequestAsync(uint requestId, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<NetworkPacket>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        _pendingRequests.TryAdd(requestId, tcs);

        var cts = new CancellationTokenSource(timeout);

        cts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(requestId, out var removedTcs))
            {
                removedTcs.TrySetException(new TimeoutException("Request timed out."));
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// Attempts to complete a pending network request by setting its result with the provided response packet.
    /// </summary>
    /// <param name="requestId">The unique identifier of the request to complete.</param>
    /// <param name="responsePacket">The <see cref="NetworkPacket"/> containing the response data.</param>
    /// <returns>
    /// <c>true</c> if the request was found and completed; otherwise, <c>false</c>.
    /// </returns>
    public bool TryCompleteRequest(uint requestId, NetworkPacket responsePacket)
    {
        if (_pendingRequests.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(responsePacket);
            return true;
        }

        return false;
    }
}
