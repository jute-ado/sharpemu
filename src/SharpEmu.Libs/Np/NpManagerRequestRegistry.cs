// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Np;

/// <summary>
/// Process-local request registry shared by the synchronous NP manager calls
/// and the Gen5 asynchronous request surface.
/// </summary>
internal static class NpManagerRequestRegistry
{
    internal const int ErrorNotInitialized = unchecked((int)0x80550002);
    internal const int ErrorInvalidArgument = unchecked((int)0x80550003);
    internal const int ErrorAborted = unchecked((int)0x80550012);
    internal const int ErrorTooManyRequests = unchecked((int)0x80550013);
    internal const int ErrorRequestNotFound = unchecked((int)0x80550014);
    internal const int ErrorInvalidRequestState = unchecked((int)0x80550015);

    private const int MaximumSyncRequests = 128;
    private const int MaximumAsyncRequests = 32;
    private const int Pending = 1;

    private static readonly object RegistryGate = new();
    private static readonly SortedDictionary<int, Request> Requests = [];
    private static bool _initialized = true;

    internal enum RequestState
    {
        Created,
        Running,
        Complete,
    }

    internal readonly record struct RequestSnapshot(
        int Id,
        bool IsAsync,
        bool AbortRequested,
        bool OperationAssigned,
        RequestState State,
        int Result,
        uint Priority,
        ulong Affinity);

    internal sealed class LocalOperationContext
    {
        private readonly Request _request;

        internal LocalOperationContext(Request request)
        {
            _request = request;
        }

        internal bool IsAbortRequested
        {
            get
            {
                lock (RegistryGate)
                {
                    return _request.AbortRequested || _request.Deleted;
                }
            }
        }
    }

    internal sealed class Request(
        bool isAsync,
        uint priority,
        ulong affinity)
    {
        internal int Id;
        internal bool IsAsync { get; } = isAsync;
        internal uint Priority { get; } = priority;
        internal ulong Affinity { get; } = affinity;
        internal bool AbortRequested;
        internal bool OperationAssigned;
        internal bool Deleted;
        internal RequestState State;
        internal int Result;
        internal Task? Worker;
    }

    internal static bool IsInitialized
    {
        get
        {
            lock (RegistryGate)
            {
                return _initialized;
            }
        }
    }

    internal static int CreateSync() => Create(
        isAsync: false,
        priority: 0,
        affinity: 0,
        MaximumSyncRequests);

    internal static int CreateAsync(uint priority, ulong affinity) => Create(
        isAsync: true,
        priority,
        affinity,
        MaximumAsyncRequests);

    internal static bool ContainsSync(int requestId)
    {
        lock (RegistryGate)
        {
            return _initialized &&
                Requests.TryGetValue(requestId, out var request) &&
                !request.Deleted &&
                !request.IsAsync;
        }
    }

    internal static int Abort(int requestId)
    {
        lock (RegistryGate)
        {
            var lookup = FindAsyncUnderLock(requestId, out var request);
            if (lookup != 0)
            {
                return lookup;
            }

            request!.AbortRequested = true;
            return 0;
        }
    }

    internal static int Delete(int requestId)
    {
        Task? worker;
        lock (RegistryGate)
        {
            if (!_initialized)
            {
                return ErrorNotInitialized;
            }

            // Preserve the existing sceNpDeleteRequest contract: invalid and
            // unknown IDs both report REQUEST_NOT_FOUND.
            if (requestId <= 0 ||
                !Requests.Remove(requestId, out var request) ||
                request.Deleted)
            {
                return ErrorRequestNotFound;
            }

            request.AbortRequested = true;
            request.Deleted = true;
            worker = request.Worker;
        }

        worker?.GetAwaiter().GetResult();
        return 0;
    }

    internal static int Poll(
        int requestId,
        out bool completed,
        out int result)
    {
        completed = false;
        result = 0;
        lock (RegistryGate)
        {
            var lookup = FindAsyncUnderLock(requestId, out var request);
            if (lookup != 0)
            {
                return lookup;
            }

            if (request!.State != RequestState.Complete)
            {
                return Pending;
            }

            completed = true;
            result = request.Result;
            return 0;
        }
    }

    internal static int StartLocalOperation(
        int requestId,
        Func<LocalOperationContext, int> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (RegistryGate)
        {
            var lookup = FindAsyncUnderLock(requestId, out var request);
            if (lookup != 0)
            {
                return lookup;
            }

            if (request!.OperationAssigned)
            {
                return ErrorInvalidArgument;
            }

            request.OperationAssigned = true;
            request.State = RequestState.Running;
            var context = new LocalOperationContext(request);
            request.Worker = Task.Run(() =>
            {
                var operationResult = context.IsAbortRequested
                    ? ErrorAborted
                    : operation(context);
                lock (RegistryGate)
                {
                    request.Result =
                        request.AbortRequested || request.Deleted
                            ? ErrorAborted
                            : operationResult;
                    request.State = RequestState.Complete;
                }
            });
            return 0;
        }
    }

    internal static bool WaitForCompletionForTests(
        int requestId,
        TimeSpan timeout)
    {
        Task? worker;
        lock (RegistryGate)
        {
            if (!Requests.TryGetValue(requestId, out var request))
            {
                return false;
            }

            worker = request.Worker;
        }

        return worker?.Wait(timeout) ?? false;
    }

    internal static bool TryGetSnapshotForTests(
        int requestId,
        out RequestSnapshot snapshot)
    {
        lock (RegistryGate)
        {
            if (!Requests.TryGetValue(requestId, out var request))
            {
                snapshot = default;
                return false;
            }

            snapshot = new RequestSnapshot(
                request.Id,
                request.IsAsync,
                request.AbortRequested,
                request.OperationAssigned,
                request.State,
                request.Result,
                request.Priority,
                request.Affinity);
            return true;
        }
    }

    internal static int LiveCountForTests
    {
        get
        {
            lock (RegistryGate)
            {
                return Requests.Count;
            }
        }
    }

    internal static void Reset()
    {
        Shutdown();
        lock (RegistryGate)
        {
            _initialized = true;
        }
    }

    internal static void ShutdownForTests() => Shutdown();

    private static int Create(
        bool isAsync,
        uint priority,
        ulong affinity,
        int kindLimit)
    {
        lock (RegistryGate)
        {
            if (!_initialized)
            {
                return ErrorNotInitialized;
            }

            if (Requests.Values.Count(request => request.IsAsync == isAsync) >=
                kindLimit)
            {
                return ErrorTooManyRequests;
            }

            var requestId = 1;
            while (Requests.ContainsKey(requestId))
            {
                requestId++;
            }

            var request = new Request(isAsync, priority, affinity)
            {
                Id = requestId,
            };
            Requests.Add(requestId, request);
            return requestId;
        }
    }

    private static int FindAsyncUnderLock(
        int requestId,
        out Request? request)
    {
        request = null;
        if (!_initialized)
        {
            return ErrorNotInitialized;
        }

        if (requestId <= 0)
        {
            return ErrorInvalidArgument;
        }

        if (!Requests.TryGetValue(requestId, out request) || request.Deleted)
        {
            request = null;
            return ErrorRequestNotFound;
        }

        if (!request.IsAsync)
        {
            request = null;
            return ErrorInvalidRequestState;
        }

        return 0;
    }

    private static void Shutdown()
    {
        Task[] workers;
        lock (RegistryGate)
        {
            _initialized = false;
            foreach (var request in Requests.Values)
            {
                request.AbortRequested = true;
                request.Deleted = true;
            }

            workers = Requests.Values
                .Select(request => request.Worker)
                .Where(worker => worker is not null)
                .Cast<Task>()
                .ToArray();
            Requests.Clear();
        }

        foreach (var worker in workers)
        {
            worker.GetAwaiter().GetResult();
        }
    }
}
