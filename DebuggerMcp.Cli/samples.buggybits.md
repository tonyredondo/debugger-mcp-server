# Crash Analysis Report

---

| Property | Value |
|----------|-------|
| **Generated** | 2025-12-09 15:30:23 UTC |
| **Dump ID** | b0f33af7-5df9-435a-9134-fcef6d99ac7a |
| **Crash Type** | .NET Managed Exception |
| **Debugger** | LLDB |
| **Platform** |  |
| **Architecture** | x64 (64-bit) |
| **.NET Runtime** | 8.0.2225.52707 |

## 📋 Executive Summary

> Crash Type: Unknown. Found 33 threads (0 total frames, 0 in faulting thread), 18 modules.  .NET Analysis: CLR 8.0.2225.52707. Managed Exception: System.BadImageFormatException. Heap has 1343 types. 

## 🔴 Crash Information

### Exception Details

- **Type**: `System.BadImageFormatException`
- **Message**: An attempt was made to load a program with an incorrect format.
- **HResult**: `8007000b`

## 📚 Thread Call Stacks

### Thread #1 (tid: 4857) "Samples.BuggyBi" ⚠️ **Faulting** - signal SIGSTOP 🔥 System.BadImageFormatException @ 0x00007ef0df81b390

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1BB27]` | - |
| 01 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[[BuggyBits.Program+<Main>d__1, Samples.BuggyBits]](<Main>d__1 ByRef)` | [AsyncMethodBuilderCore.cs:38](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncMethodBuilderCore.cs#L38) |
| 02 | 🟢 | Samples.BuggyBits.dll | `BuggyBits.Program.Main(System.String[])` | - |
| 03 | 🟢 | Samples.BuggyBits.dll | `BuggyBits.Program.<Main>(System.String[])` | - |

#### 📍 Frame Variables (Faulting Thread)

<details>
<summary><strong>Frame 01</strong>: System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[[BuggyBits.Program+<Main>d__1, Samples.BuggyBits]](<Main>d__1 ByRef)</summary>

**Parameters:**

| Name | Type | Value |
|------|------|-------|
| `[unnamed]` | `<Main>d__1(ByRef)` | *no data* |

**Local Variables:**

| Name | Type | Value |
|------|------|-------|
| `[unnamed]` | `-` | `0x00007ef0df823ad8` |
| `[unnamed]` | `-` | `0x00007ef0de037720` |
| `[unnamed]` | `-` | `0x0000000000000000` |

</details>

<details>
<summary><strong>Frame 02</strong>: BuggyBits.Program.Main(System.String[])</summary>

**Parameters:**

| Name | Type | Value |
|------|------|-------|
| `[unnamed]` | `System.String[]` | *no data* |

</details>

<details>
<summary><strong>Frame 03</strong>: BuggyBits.Program.<Main>(System.String[])</summary>

**Parameters:**

| Name | Type | Value |
|------|------|-------|
| `[unnamed]` | `System.String[]` | *no data* |

</details>

### Thread #13 (tid: 4869) - signal 0

```
  00 [N] ![Native Code @ 0x00007F3156E1F453]
  01 [N] ![Native Code @ 0x00007EF03C9635F0]
```

### Thread #18 (tid: 4874) - signal 0 (Finalizer)

```
  00 [N] ![Native Code @ 0x00007F3156E1F453]
  01 [M] ![DebuggerU2MCatchHandlerFrame: 00007ef0400f53a0]
  02 [N] ![Native Code @ 0x00007EF03C9633E8]
```

### Thread #19 (tid: 4896) - signal 0 (Threadpool Worker)

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1BB27]` | - |
| 01 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ThreadPoolWorkQueue.Dispatch()` | [ThreadPoolWorkQueue.cs:1010](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPoolWorkQueue.cs#L1010) |
| 02 | 🟢 | System.Private.CoreLib.dll | `System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()` | [PortableThreadPool.WorkerThread.NonBrowser.cs:102](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.WorkerThread.NonBrowser.cs#L102) |
| 03 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef03cfcd2e0]` | - |

### Thread #20 (tid: 4897) - signal 0 (Threadpool Worker)

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1BB27]` | - |
| 01 | 🟢 | System.Private.CoreLib.dll | `System.Threading.PortableThreadPool+WorkerThread.CreateWorkerThread()` | [PortableThreadPool.WorkerThread.NonBrowser.cs:116](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.WorkerThread.NonBrowser.cs#L116) |
| 02 | 🟢 | System.Private.CoreLib.dll | `System.Threading.PortableThreadPool+WorkerThread.MaybeAddWorkingWorker(System.Threading.PortableThreadPool)` | [PortableThreadPool.WorkerThread.cs:214](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.WorkerThread.cs#L214) |
| 03 | 🟢 | System.Private.CoreLib.dll | `System.Threading.PortableThreadPool+GateThread.GateThreadStart()` | [PortableThreadPool.GateThread.cs:186](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.GateThread.cs#L186) |
| 04 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef03ce0a2e0]` | - |

### Thread #21 (tid: 4898) - signal 0 (Threadpool Worker)

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1C62B]` | - |
| 01 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 02 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 03 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task`1[[Datadog.Trace.Agent.Api+SendResult, Datadog.Trace]].TrySetResult(SendResult)` | [Future.cs:398](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L398) |
| 04 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[[Datadog.Trace.Agent.Api+SendResult, Datadog.Trace]].SetExistingTaskResult(System.Threading.Tasks.Task`1<SendResult>, SendResult)` | [AsyncTaskMethodBuilderT.cs:490](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L490) |
| 05 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Agent.Api+<SendTracesAsyncImpl>d__27.MoveNext()` | [Api.cs:417](https://github.com/DataDog/dd-trace-dotnet/blob/14f72c17f2e0ed8ce952d10647a9418bd4d28553/tracer/src/Datadog.Trace/Agent/Api.cs#L417) |
| 06 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 07 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[Datadog.Trace.Agent.Api+SendResult, Datadog.Trace],[Datadog.Trace.Agent.Api+<SendTracesAsyncImpl>d__27, Datadog.Trace]].MoveNext(System.Threading.Thread)` | [AsyncTaskMethodBuilderT.cs:368](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L368) |
| 08 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 09 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 10 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Agent.Transports.HttpClientRequest+<PostAsync>d__9.MoveNext()` | [HttpClientRequest.cs:68](https://github.com/DataDog/dd-trace-dotnet/blob/14f72c17f2e0ed8ce952d10647a9418bd4d28553/tracer/src/Datadog.Trace/Agent/Transports/HttpClientRequest.cs#L68) |
| 11 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 12 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.__Canon, System.Private.CoreLib],[Datadog.Trace.Agent.Transports.HttpClientRequest+<PostAsync>d__9, Datadog.Trace]].MoveNext(System.Threading.Thread)` | - |
| 13 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 14 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 15 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task`1[[System.__Canon, System.Private.CoreLib]].TrySetResult(System.__Canon)` | [Future.cs:398](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L398) |
| 16 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[[System.__Canon, System.Private.CoreLib]].SetExistingTaskResult(System.Threading.Tasks.Task`1<System.__Canon>, System.__Canon)` | [AsyncTaskMethodBuilderT.cs:490](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L490) |
| 17 | 🟢 | System.Net.Http.dll | `System.Net.Http.HttpClient+<<SendAsync>g__Core|83_0>d.MoveNext()` | [HttpClient.cs:556](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Net.Http/src/System/Net/Http/HttpClient.cs#L556) |
| 18 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 19 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.__Canon, System.Private.CoreLib],[System.Net.Http.HttpClient+<<SendAsync>g__Core|83_0>d, System.Net.Http]].MoveNext(System.Threading.Thread)` | - |
| 20 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 21 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 22 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task`1[[System.__Canon, System.Private.CoreLib]].TrySetResult(System.__Canon)` | [Future.cs:398](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L398) |
| 23 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[[System.__Canon, System.Private.CoreLib]].SetExistingTaskResult(System.Threading.Tasks.Task`1<System.__Canon>, System.__Canon)` | [AsyncTaskMethodBuilderT.cs:490](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L490) |
| 24 | 🟢 | System.Net.Http.dll | `System.Net.Http.RedirectHandler+<SendAsync>d__4.MoveNext()` | [RedirectHandler.cs:89](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/RedirectHandler.cs#L89) |
| 25 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 26 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.__Canon, System.Private.CoreLib],[System.Net.Http.RedirectHandler+<SendAsync>d__4, System.Net.Http]].MoveNext(System.Threading.Thread)` | - |
| 27 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 28 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 29 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task`1[[System.__Canon, System.Private.CoreLib]].TrySetResult(System.__Canon)` | [Future.cs:398](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L398) |
| 30 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[[System.__Canon, System.Private.CoreLib]].SetExistingTaskResult(System.Threading.Tasks.Task`1<System.__Canon>, System.__Canon)` | [AsyncTaskMethodBuilderT.cs:490](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L490) |
| 31 | 🟢 | System.Net.Http.dll | `System.Net.Http.HttpConnectionPool+<SendWithVersionDetectionAndRetryAsync>d__89.MoveNext()` | [HttpConnectionPool.cs:1185](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnectionPool.cs#L1185) |
| 32 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 33 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.__Canon, System.Private.CoreLib],[System.Net.Http.HttpConnectionPool+<SendWithVersionDetectionAndRetryAsync>d__89, System.Net.Http]].MoveNext(System.Threading.Thread)` | - |
| 34 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 35 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 36 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task`1[[System.__Canon, System.Private.CoreLib]].TrySetResult(System.__Canon)` | [Future.cs:398](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L398) |
| 37 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[[System.__Canon, System.Private.CoreLib]].SetExistingTaskResult(System.Threading.Tasks.Task`1<System.__Canon>, System.__Canon)` | [AsyncTaskMethodBuilderT.cs:490](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L490) |
| 38 | 🟢 | System.Net.Http.dll | `System.Net.Http.HttpConnection+<SendAsync>d__57.MoveNext()` | [HttpConnection.cs:867](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnection.cs#L867) |
| 39 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 40 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.__Canon, System.Private.CoreLib],[System.Net.Http.HttpConnection+<SendAsync>d__57, System.Net.Http]].MoveNext(System.Threading.Thread)` | - |
| 41 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 42 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 43 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib]].TrySetResult(System.Threading.Tasks.VoidTaskResult)` | [Future.cs:399](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L399) |
| 44 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib]].SetExistingTaskResult(System.Threading.Tasks.Task`1<System.Threading.Tasks.VoidTaskResult>, System.Threading.Tasks.VoidTaskResult)` | [AsyncTaskMethodBuilderT.cs:490](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L490) |
| 45 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder.SetResult()` | [AsyncValueTaskMethodBuilder.cs:47](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncValueTaskMethodBuilder.cs#L47) |
| 46 | 🟢 | System.Net.Http.dll | `System.Net.Http.HttpConnection+<InitialFillAsync>d__82.MoveNext()` | [HttpConnection.cs:1600](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnection.cs#L1600) |
| 47 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 48 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[System.Net.Http.HttpConnection+<InitialFillAsync>d__82, System.Net.Http]].MoveNext(System.Threading.Thread)` | [AsyncTaskMethodBuilderT.cs:368](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L368) |
| 49 | 🟢 | System.Net.Sockets.dll | `System.Net.Sockets.SocketAsyncEventArgs.TransferCompletionCallbackCore(Int32, System.Memory`1<Byte>, System.Net.Sockets.SocketFlags, System.Net.Sockets.SocketError)` | [SocketAsyncEventArgs.Unix.cs:101](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.Unix.cs#L101) |
| 50 | 🟢 | System.Net.Sockets.dll | `System.Net.Sockets.SocketAsyncEngine.System.Threading.IThreadPoolWorkItem.Execute()` | [SocketAsyncEngine.Unix.cs:242](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEngine.Unix.cs#L242) |
| 51 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ThreadPoolWorkQueue.Dispatch()` | [ThreadPoolWorkQueue.cs:1010](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPoolWorkQueue.cs#L1010) |
| 52 | 🟢 | System.Private.CoreLib.dll | `System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()` | [PortableThreadPool.WorkerThread.NonBrowser.cs:102](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.WorkerThread.NonBrowser.cs#L102) |
| 53 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef03cdc72e0]` | - |
| 54 | 🔵 |  | `[Native Code @ 0x00007EF03C963380]` | - |

### Thread #22 (tid: 4899) - signal 0

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1F453]` | - |
| 01 | 🟢 | System.Private.CoreLib.dll | `System.Threading.WaitHandle.WaitOneNoCheck(Int32)` | [WaitHandle.cs:128](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/WaitHandle.cs#L128) |
| 02 | 🟢 | System.Private.CoreLib.dll | `System.Threading.TimerQueue.TimerThread()` | [TimerQueue.Portable.cs:87](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/TimerQueue.Portable.cs#L87) |
| 03 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef03cbbc2e0]` | - |
| 04 | 🔵 |  | `[Native Code @ 0x00007EF03C963318]` | - |

### Thread #24 (tid: 4903) - signal 0 (Threadpool Worker)

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1BB27]` | - |
| 01 | 🟢 |  | `[InlinedCallFrame: 00007ef03adb5340]` | - |
| 02 | 🟢 |  | `[InlinedCallFrame: 00007ef03adb5340]` | - |
| 03 | 🟢 | System.IO.Compression.dll | `System.IO.Compression.ZLibNative+ZLibStreamHandle.DeflateInit2_(CompressionLevel, Int32, Int32, CompressionStrategy)` | [ZLibNative.cs:269](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/Common/src/System/IO/Compression/ZLibNative.cs#L269) |
| 04 | 🟢 | System.IO.Compression.dll | `System.IO.Compression.Deflater..ctor(System.IO.Compression.CompressionLevel, Int32)` | [Deflater.cs:70](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/Deflater.cs#L70) |
| 05 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.Transports.JsonTelemetryTransport.SerializeTelemetryWithGzip[[System.__Canon, System.Private.CoreLib]](System.__Canon)` | - |
| 06 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.Transports.JsonTelemetryTransport+<PushTelemetry>d__11.MoveNext()` | [JsonTelemetryTransport.cs:62](https://github.com/DataDog/dd-trace-dotnet/blob/14f72c17f2e0ed8ce952d10647a9418bd4d28553/tracer/src/Datadog.Trace/Telemetry/Transports/JsonTelemetryTransport.cs#L62) |
| 07 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[[Datadog.Trace.Telemetry.Transports.JsonTelemetryTransport+<PushTelemetry>d__11, Datadog.Trace]](<PushTelemetry>d__11 ByRef)` | [AsyncMethodBuilderCore.cs:38](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncMethodBuilderCore.cs#L38) |
| 08 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.Transports.JsonTelemetryTransport.PushTelemetry(Datadog.Trace.Telemetry.TelemetryData)` | - |
| 09 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.TelemetryTransportManager+<TryPushTelemetry>d__12.MoveNext()` | [TelemetryTransportManager.cs:88](https://github.com/DataDog/dd-trace-dotnet/blob/14f72c17f2e0ed8ce952d10647a9418bd4d28553/tracer/src/Datadog.Trace/Telemetry/TelemetryTransportManager.cs#L88) |
| 10 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[[Datadog.Trace.Telemetry.TelemetryTransportManager+<TryPushTelemetry>d__12, Datadog.Trace]](<TryPushTelemetry>d__12 ByRef)` | [AsyncMethodBuilderCore.cs:38](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncMethodBuilderCore.cs#L38) |
| 11 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.TelemetryTransportManager.TryPushTelemetry(Datadog.Trace.Telemetry.TelemetryData)` | - |
| 12 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.TelemetryController+<PushTelemetry>d__39.MoveNext()` | [TelemetryController.cs:337](https://github.com/DataDog/dd-trace-dotnet/blob/14f72c17f2e0ed8ce952d10647a9418bd4d28553/tracer/src/Datadog.Trace/Telemetry/TelemetryController.cs#L337) |
| 13 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[[Datadog.Trace.Telemetry.TelemetryController+<PushTelemetry>d__39, Datadog.Trace]](<PushTelemetry>d__39 ByRef)` | [AsyncMethodBuilderCore.cs:38](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncMethodBuilderCore.cs#L38) |
| 14 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.TelemetryController.PushTelemetry(Boolean, Boolean)` | - |
| 15 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.TelemetryController+<PushTelemetryLoopAsync>d__38.MoveNext()` | [TelemetryController.cs:285](https://github.com/DataDog/dd-trace-dotnet/blob/14f72c17f2e0ed8ce952d10647a9418bd4d28553/tracer/src/Datadog.Trace/Telemetry/TelemetryController.cs#L285) |
| 16 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 17 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[Datadog.Trace.Telemetry.TelemetryController+<PushTelemetryLoopAsync>d__38, Datadog.Trace]].MoveNext(System.Threading.Thread)` | [AsyncTaskMethodBuilderT.cs:368](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L368) |
| 18 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 19 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 20 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib]].TrySetResult(System.Threading.Tasks.VoidTaskResult)` | [Future.cs:399](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L399) |
| 21 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib]].SetExistingTaskResult(System.Threading.Tasks.Task`1<System.Threading.Tasks.VoidTaskResult>, System.Threading.Tasks.VoidTaskResult)` | [AsyncTaskMethodBuilderT.cs:490](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L490) |
| 22 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder.SetResult()` | [AsyncTaskMethodBuilder.cs:107](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilder.cs#L107) |
| 23 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Telemetry.TelemetryController+Scheduler+<WaitForNextInterval>d__26.MoveNext()` | [TelemetryController.cs:568](https://github.com/DataDog/dd-trace-dotnet/blob/14f72c17f2e0ed8ce952d10647a9418bd4d28553/tracer/src/Datadog.Trace/Telemetry/TelemetryController.cs#L568) |
| 24 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 25 | 🟢 | System.Private.CoreLib.dll | `System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[Datadog.Trace.Telemetry.TelemetryController+Scheduler+<WaitForNextInterval>d__26, Datadog.Trace]].MoveNext(System.Threading.Thread)` | [AsyncTaskMethodBuilderT.cs:368](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs#L368) |
| 26 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(System.Runtime.CompilerServices.IAsyncStateMachineBox, Boolean)` | [TaskContinuation.cs:795](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L795) |
| 27 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.RunContinuations(System.Object)` | [Task.cs:3477](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L3477) |
| 28 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task`1[[System.__Canon, System.Private.CoreLib]].TrySetResult(System.__Canon)` | [Future.cs:398](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L398) |
| 29 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.TaskFactory+CompleteOnInvokePromise`1[[System.__Canon, System.Private.CoreLib]].Invoke(System.Threading.Tasks.Task)` | [TaskFactory.cs:2308](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskFactory.cs#L2308) |
| 30 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ThreadPoolWorkQueue.Dispatch()` | [ThreadPoolWorkQueue.cs:1010](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPoolWorkQueue.cs#L1010) |
| 31 | 🟢 | System.Private.CoreLib.dll | `System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()` | [PortableThreadPool.WorkerThread.NonBrowser.cs:102](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.WorkerThread.NonBrowser.cs#L102) |
| 32 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef03adb6060]` | - |

### Thread #25 (tid: 4904) - signal 0 (Threadpool Worker)

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1F453]` | - |
| 01 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ThreadPoolWorkQueue.Dispatch()` | [ThreadPoolWorkQueue.cs:1010](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPoolWorkQueue.cs#L1010) |
| 02 | 🟢 | System.Private.CoreLib.dll | `System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()` | [PortableThreadPool.WorkerThread.NonBrowser.cs:102](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.WorkerThread.NonBrowser.cs#L102) |
| 03 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef03abf1060]` | - |
| 04 | 🔵 |  | `[Native Code @ 0x00007EF03ABF12D0]` | - |

### Thread #26 (tid: 4905) - signal 0

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1F453]` | - |
| 01 | 🟢 |  | `[InlinedCallFrame: 00007ef03a97ae30]` | - |
| 02 | 🟢 |  | `[InlinedCallFrame: 00007ef03a97ae30]` | - |
| 03 | 🟢 | System.Net.Sockets.dll | `Interop+Sys.WaitForSocketEvents(IntPtr, SocketEvent*, Int32*) + -637601808` | - |
| 04 | 🟢 | System.Net.Sockets.dll | `System.Net.Sockets.SocketAsyncEngine.EventLoop()` | [SocketAsyncEngine.Unix.cs:183](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEngine.Unix.cs#L183) |
| 05 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef03a97b060]` | - |
| 06 | 🔵 |  | `[Native Code @ 0x00007EF03A97B2D0]` | - |

### Thread #27 (tid: 4906) - signal 0

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1F453]` | - |
| 01 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Monitor.Wait(System.Object, Int32)` | [Monitor.CoreCLR.cs:156](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/coreclr/System.Private.CoreLib/src/System/Threading/Monitor.CoreCLR.cs#L156) |
| 02 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ManualResetEventSlim.Wait(Int32, System.Threading.CancellationToken)` | [ManualResetEventSlim.cs:561](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ManualResetEventSlim.cs#L561) |
| 03 | 🟢 | Datadog.Trace.dll | `Datadog.Trace.Agent.AgentWriter.SerializeTracesLoop()` | [AgentWriter.cs:597](https://github.com/DataDog/dd-trace-dotnet/blob/14f72c17f2e0ed8ce952d10647a9418bd4d28553/tracer/src/Datadog.Trace/Agent/AgentWriter.cs#L597) |
| 04 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)` | [ExecutionContext.cs:179](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs#L179) |
| 05 | 🟢 | System.Private.CoreLib.dll | `System.Threading.Tasks.Task.ExecuteWithThreadLocal(System.Threading.Tasks.Task ByRef, System.Threading.Thread)` | [Task.cs:2345](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L2345) |
| 06 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef03a77e060]` | - |
| 07 | 🔵 |  | `[Native Code @ 0x00007EF03A77E2D0]` | - |

### Thread #33 (tid: 4912) - signal 0 (Threadpool Worker)

| # | Type | Module | Function | Source |
|---|------|--------|----------|--------|
| 00 | 🔵 |  | `[Native Code @ 0x00007F3156E1F453]` | - |
| 01 | 🟢 | System.Private.CoreLib.dll | `System.Threading.ThreadPoolWorkQueue.Dispatch()` | [ThreadPoolWorkQueue.cs:1010](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPoolWorkQueue.cs#L1010) |
| 02 | 🟢 | System.Private.CoreLib.dll | `System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()` | [PortableThreadPool.WorkerThread.NonBrowser.cs:102](https://github.com/dotnet/runtime/blob/a2266c728f63a494ccb6786d794da2df135030be/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.WorkerThread.NonBrowser.cs#L102) |
| 03 | 🟢 |  | `[DebuggerU2MCatchHandlerFrame: 00007ef0399a1060]` | - |
| 04 | 🔵 |  | `[Native Code @ 0x00007EF0399A12D0]` | - |

## 💾 Memory Analysis

### Heap Statistics

```
Top Types by Size
-----------------
Datadog.Trace.Agent.NullStatsAggregator                      █████████████████████████  10.0% (24.0 B)
Datadog.Trace.Agent.MovingAverageKeepRateCalculator+<>c      █████████████████████████  10.0% (24.0 B)
Datadog.Trace.Agent.MessagePack.SpanFormatterResolver        █████████████████████████  10.0% (24.0 B)
Datadog.Trace.ClrProfiler.AutomaticTracer                    █████████████████████████  10.0% (24.0 B)
Datadog.Trace.Util.RuntimeId+<>c                             █████████████████████████  10.0% (24.0 B)
Datadog.Trace.Agent.MessagePack.SpanMessagePackFormatter+<>c █████████████████████████  10.0% (24.0 B)
System.Threading.Tasks.ThreadPoolTaskScheduler+<>c           █████████████████████████  10.0% (24.0 B)
Datadog.Trace.Agent.AgentWriter+<>c                          █████████████████████████  10.0% (24.0 B)
Datadog.Trace.AsyncLocalScopeManager                         █████████████████████████  10.0% (24.0 B)
Datadog.Trace.ContinuousProfiler.Profiler+<>c                █████████████████████████  10.0% (24.0 B)

```

### Top Memory Consumers

```
System.String                                                                       █████████████████████████  32.8% (619.0 KB)
System.Byte[]                                                                       ██████████████░░░░░░░░░░░  18.8% (355.1 KB)
System.Diagnostics.Tracing.EventSource+EventMetadata[]                              ███████░░░░░░░░░░░░░░░░░░   9.3% (176.0 KB)
System.Int32[]                                                                      █████░░░░░░░░░░░░░░░░░░░░   7.5% (141.3 KB)
System.Object[]                                                                     █████░░░░░░░░░░░░░░░░░░░░   7.5% (141.1 KB)
System.Reflection.RuntimeMethodInfo                                                 ████░░░░░░░░░░░░░░░░░░░░░   6.4% (119.9 KB)
System.Reflection.RuntimeParameterInfo                                              ████░░░░░░░░░░░░░░░░░░░░░   5.8% (110.2 KB)
System.Char[]                                                                       ███░░░░░░░░░░░░░░░░░░░░░░   4.6% (86.8 KB)
Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2 ███░░░░░░░░░░░░░░░░░░░░░░   4.3% (80.4 KB)
System.String[]                                                                     ██░░░░░░░░░░░░░░░░░░░░░░░   3.1% (57.8 KB)

```

| Type                                                                                | Count | Total Size |
|-------------------------------------------------------------------------------------|-------|------------|
| System.String                                                                       | 7,138 | 619.0 KB   |
| System.Byte[]                                                                       | 1,020 | 355.1 KB   |
| System.Diagnostics.Tracing.EventSource+EventMetadata[]                              | 7     | 176.0 KB   |
| System.Int32[]                                                                      | 554   | 141.3 KB   |
| System.Object[]                                                                     | 190   | 141.1 KB   |
| System.Reflection.RuntimeMethodInfo                                                 | 1,181 | 119.9 KB   |
| System.Reflection.RuntimeParameterInfo                                              | 1,175 | 110.2 KB   |
| System.Char[]                                                                       | 260   | 86.8 KB    |
| Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2 | 935   | 80.4 KB    |
| System.String[]                                                                     | 1,265 | 57.8 KB    |


## 🧵 Thread Information

**Total Threads**: 12

### Thread State Distribution

```
Thread States
-------------
signal 0       ████████████████████  97.0% (32)
signal SIGSTOP ░░░░░░░░░░░░░░░░░░░░   3.0% (1)

```

| Thread ID      | Type              | GC Mode     | Locks | State          | Exception                                           |
|----------------|-------------------|-------------|-------|----------------|-----------------------------------------------------|
| ⚠️M#1 (0x12f9) | -                 | Preemptive  | -1    | signal SIGSTOP | System.BadImageFormatException @ 0x00007ef0df81b390 |
| 2 (tid: 4858)  | -                 | -           | -     | signal 0       | -                                                   |
| 3 (tid: 4859)  | -                 | -           | -     | signal 0       | -                                                   |
| 4 (tid: 4860)  | -                 | -           | -     | signal 0       | -                                                   |
| 5 (tid: 4861)  | -                 | -           | -     | signal 0       | -                                                   |
| 6 (tid: 4862)  | -                 | -           | -     | signal 0       | -                                                   |
| 7 (tid: 4863)  | -                 | -           | -     | signal 0       | -                                                   |
| 8 (tid: 4864)  | -                 | -           | -     | signal 0       | -                                                   |
| 9 (tid: 4865)  | -                 | -           | -     | signal 0       | -                                                   |
| 10 (tid: 4866) | -                 | -           | -     | signal 0       | -                                                   |
| 11 (tid: 4867) | -                 | -           | -     | signal 0       | -                                                   |
| 12 (tid: 4868) | -                 | -           | -     | signal 0       | -                                                   |
| M#11 (0x1305)  | -                 | Preemptive  | -1    | signal 0       | -                                                   |
| 14 (tid: 4870) | -                 | -           | -     | signal 0       | -                                                   |
| 15 (tid: 4871) | -                 | -           | -     | signal 0       | -                                                   |
| 16 (tid: 4872) | -                 | -           | -     | signal 0       | -                                                   |
| 17 (tid: 4873) | -                 | -           | -     | signal 0       | -                                                   |
| M#2 (0x130a)   | Finalizer         | Preemptive  | -1    | signal 0       | -                                                   |
| M#3 (0x1320)   | Threadpool Worker | Preemptive  | -1    | signal 0       | -                                                   |
| M#4 (0x1321)   | Threadpool Worker | Cooperative | -1    | signal 0       | -                                                   |
| M#5 (0x1322)   | Threadpool Worker | Preemptive  | -1    | signal 0       | -                                                   |
| M#6 (0x1323)   | -                 | Preemptive  | -1    | signal 0       | -                                                   |
| 23 (tid: 4902) | -                 | -           | -     | signal 0       | -                                                   |
| M#7 (0x1327)   | Threadpool Worker | Preemptive  | -1    | signal 0       | -                                                   |
| M#8 (0x1328)   | Threadpool Worker | Preemptive  | -1    | signal 0       | -                                                   |
| M#9 (0x1329)   | -                 | Preemptive  | -1    | signal 0       | -                                                   |
| M#10 (0x132a)  | -                 | Preemptive  | -1    | signal 0       | -                                                   |
| 28 (tid: 4907) | -                 | -           | -     | signal 0       | -                                                   |
| 29 (tid: 4908) | -                 | -           | -     | signal 0       | -                                                   |
| 30 (tid: 4909) | -                 | -           | -     | signal 0       | -                                                   |
| 31 (tid: 4910) | -                 | -           | -     | signal 0       | -                                                   |
| 32 (tid: 4911) | -                 | -           | -     | signal 0       | -                                                   |
| M#12 (0x1330)  | Threadpool Worker | Preemptive  | -1    | signal 0       | -                                                   |


## 🟣 .NET Runtime Information

**CLR Version**: 8.0.2225.52707

### Managed Thread Statistics

| Metric | Count |
|--------|-------|
| Total Threads | 12 |
| Background | 11 |

### 🔥 Managed Exception

**Type**: `System.BadImageFormatException`

**Message**: An attempt was made to load a program with an incorrect format.

**HResult**: 0x8007000b

### 🔄 Thread Pool Status

| Metric | Value |
|--------|-------|
| CPU Utilization | 0% |
| Workers Total | 6 |
| Workers Running | 6 |
| Workers Idle | 0 |
| Min Threads | 4 |
| Max Threads | 32,767 |
| Thread Pool Type | Portable |

> ⚠️ **Thread Pool Saturation**: All worker threads are busy. Consider increasing thread pool limits or reducing blocking operations.

### ⏱️ Active Timers

**Total Active Timers**: 7

| State Type | Count | Due Time | Period |
|------------|-------|----------|--------|
| `System.Threading.Tasks.Task+DelayPromise` | 5 | 1,000ms | one-shot |
| `System.WeakReference<System.Net.Http.HttpConnectionPoolManager>` | 2 | 15,000ms | one-shot |

## 🔎 Exception Deep Analysis

### Exception Details

| Property | Value |
|----------|-------|
| **Type** | `System.BadImageFormatException` |
| **Message** | An attempt was made to load a program with an incorrect format.
 (0x8007000B) |
| **HResult** | `0x8007000b` |
| **Address** | `0x00007ef0df81b390` |

### Exception Chain

🔴 **System.BadImageFormatException**
   - Message: An attempt was made to load a program with an incorrect format.
 (0x8007000B)
   - HResult: `0x8007000b`

### Custom Properties

| Property | Value |
|----------|-------|
| `isFormatException` | True |

## 🚀 NativeAOT / Trimming Analysis

| Property | Status |
|----------|--------|
| NativeAOT | 🟢 **Not NativeAOT** |
| JIT Compiler | ✅ JIT Present |

### Trimming Analysis

> 🟡 Possible version mismatch or configuration issue (Confidence: low)

- **Exception Type**: `System.BadImageFormatException`
- **Missing Member**: `An attempt was made to load a program with an incorrect format.`

### Recommendations

• Ensure all assemblies are built for the correct target architecture (x64/ARM64)
• NativeAOT cannot load IL assemblies at runtime - all code must be AOT compiled
• Check for mixed-mode assemblies or platform-specific native dependencies
• Enable trimming warnings during build: <SuppressTrimAnalysisWarnings>false</SuppressTrimAnalysisWarnings>
• Run with IL Linker in analysis mode to find trimming issues

## 📦 Assembly Versions

<details>
<summary>View all 51 assemblies</summary>

| Assembly | Version | Info Version | Config | Company | Repository |
|----------|---------|--------------|--------|---------|------------|
| `(Dynamic) []` 🔄 | - | - | - | - | - |
| `Datadog.Demos.Util` | 1.0.0.0 | 1.0.0+14f72c17f2e0ed8ce952d106... | Release | Datadog.Demos.Util | - |
| `Datadog.Trace` | 3.33.0.0 | 3.33.0+14f72c17f2e0ed8ce952d10... [`14f72c1`](https://github.com/DataDog/dd-trace-dotnet.git/commit/14f72c17f2e0ed8ce952d10647a9418bd4d28553) | Release | Datadog | [🔗](https://github.com/DataDog/dd-trace-dotnet.git) |
| `Microsoft.AspNetCore` | 8.0.0.0 | 8.0.22+ee417479933278bb5aadc59... [`ee41747`](https://github.com/dotnet/aspnetcore/commit/ee417479933278bb5aadc5944706a96b5ef74a5d) | Release | Microsoft Corporation | [🔗](https://github.com/dotnet/aspnetcore) |
| `Microsoft.Extensions.Configuration.Abstractions` | 8.0.0.0 | 8.0.0+5535e31a712343a63f5d7d79... [`5535e31`](https://github.com/dotnet/runtime/commit/5535e31a712343a63f5d7d796cd874e563e5ac14) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `Microsoft.Extensions.Hosting` | 8.0.0.0 | 8.0.10+81cabf2857a01351e5ab578... [`81cabf2`](https://github.com/dotnet/runtime/commit/81cabf2857a01351e5ab578947c7403a5b128ad1) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `Microsoft.Extensions.Hosting.Abstractions` | 8.0.0.0 | 8.0.10+81cabf2857a01351e5ab578... [`81cabf2`](https://github.com/dotnet/runtime/commit/81cabf2857a01351e5ab578947c7403a5b128ad1) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `Microsoft.Win32.Primitives` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `Samples.BuggyBits` | 1.0.0.0 | 1.0.0+1234567890ABCDEF [`1234567`](http://github.com/DataDog/dd-trace-dotnet/commit/1234567890ABCDEF) | Release | Samples.BuggyBits | [🔗](http://github.com/DataDog/dd-trace-dotnet) |
| `System.Collections` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Collections.Concurrent` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Collections.Specialized` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.ComponentModel` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.ComponentModel.Primitives` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.ComponentModel.TypeConverter` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Console` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Diagnostics.DiagnosticSource` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Diagnostics.Process` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Diagnostics.TraceSource` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Diagnostics.Tracing` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.IO.Compression` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.IO.FileSystem` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Linq` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Linq.Expressions` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Memory` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Net.Http` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Net.NameResolution` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Net.Primitives` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Net.Security` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Net.Sockets` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.ObjectModel` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Private.CoreLib` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | Release | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Private.Uri` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Reflection.Emit.ILGeneration` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Reflection.Emit.Lightweight` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Reflection.Primitives` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Runtime` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Runtime.Extensions` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Runtime.InteropServices` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Runtime.InteropServices.RuntimeInformation` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Runtime.Intrinsics` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Runtime.Loader` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Runtime.Numerics` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Runtime.Serialization.Formatters` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Security.Cryptography` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Text.Encoding.Extensions` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Text.RegularExpressions` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Threading` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Threading.Thread` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `System.Threading.ThreadPool` | 8.0.0.0 | 8.0.22+a2266c728f63a494ccb6786... [`a2266c7`](https://github.com/dotnet/runtime/commit/a2266c728f63a494ccb6786d794da2df135030be) | - | Microsoft Corporation | [🔗](https://github.com/dotnet/runtime) |
| `[]` | - | - | - | - | - |

</details>

### 🗑️ GC Heap Summary (ClrMD)

| Property | Value |
|----------|-------|
| Mode | Server |
| Heap Count | 4 |
| Total Heap Size | 3.12 MB |
| Fragmentation | 0.48 % (14.85 KB) |
| Finalizable Objects | 298 |

**Generation Sizes:**

```
Gen0: 2.77 MB
Gen1: 0 B
Gen2: 229.05 KB
LOH:  0 B
POH:  128.64 KB
```

### 📊 Top Memory Consumers (ClrMD)

*34,531 objects, 3.03 MB, 1,491 unique types*
*Analysis time: 144ms*

**By Total Size:**

| Type | Count | Total Size | % |
|------|------:|----------:|--:|
| System.String | 7,138 | 618.98 KB | 20.0% |
| System.Byte[] | 1,020 | 355.07 KB | 11.4% |
| System.Diagnostics.Tracing.EventSource+EventMetadata[] | 7 | 176.04 KB | 5.7% |
| System.Int32[] | 554 | 141.29 KB | 4.6% |
| System.Object[] | 190 | 141.08 KB | 4.5% |
| System.Reflection.RuntimeMethodInfo | 1,181 | 119.95 KB | 3.9% |
| System.Reflection.RuntimeParameterInfo | 1,175 | 110.16 KB | 3.5% |
| System.Char[] | 260 | 86.79 KB | 2.8% |
| Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2 | 935 | 80.35 KB | 2.6% |
| System.String[] | 1,265 | 57.8 KB | 1.9% |
| System.Collections.Generic.Dictionary<System.String, Datadog.Trace.Vendors.Serilog.Events.LogEventPropertyValue>+Entry[] | 309 | 51.66 KB | 1.7% |
| Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.PathFilter[] | 936 | 51.16 KB | 1.6% |
| System.Signature | 624 | 48.75 KB | 1.6% |
| System.Reflection.RuntimeMethodInfo[] | 188 | 41.27 KB | 1.3% |
| Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.JPath | 935 | 36.52 KB | 1.2% |

## ⚡ Async Analysis

### Task Summary

| Metric | Count |
|--------|-------|
| Total Tasks | 2 |
| Pending | 1 |
| Completed | 1 |

### Pending State Machines

| State Machine | State |
|---------------|-------|
| `System.Resources.ResourceFallbackManager+<GetEnumerator>d__5` | 2 |
| `System.Resources.ResourceFallbackManager+<GetEnumerator>d__5` | 2 |
| `System.Resources.ResourceFallbackManager+<GetEnumerator>d__5` | 2 |
| `Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2` | -2 |
| `Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2` | -2 |
| `Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2` | -2 |
| `Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2` | -2 |
| `Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2` | -2 |
| `Datadog.Trace.Vendors.Newtonsoft.Json.Linq.JsonPath.FieldFilter+<ExecuteFilter>d__2` | -2 |
| `Datadog.Trace.Vendors.Serilog.Parsing.MessageTemplateParser+<Tokenize>d__1` | -2 |
| ... | +91 more |

> Analysis completed in 144ms

### 📝 String Duplicate Analysis (ClrMD)

| Metric | Value |
|--------|------:|
| Total Strings | 7,138 |
| Unique Strings | 5,164 |
| Duplicate Count | 1,974 |
| Total Size | 618.98 KB |
| Wasted Size | 149.11 KB (24.1%) |

*Analysis time: 144ms*

**Length Distribution:**

```
Empty (0):       1
Short (1-10):    2,093
Medium (11-100): 4,837
Long (101-1000): 204
Very Long (>1k): 3
```

**Top Duplicates (by wasted bytes):**

| Value | Count | Wasted | Suggestion |
|-------|------:|-------:|------------|
| `0024000004800000940000000602000000240000...` | 28 | 17.46 KB | Consider caching or using StringPool |
| `System.Diagnostics.Tracing.EventKeywords` | 151 | 14.94 KB | Consider caching or using StringPool |
| `System.Diagnostics.Tracing.EventLevel` | 151 | 14.06 KB | Consider caching or using StringPool |
| `\"\" Datadog.Trace.ClrProfiler.Managed.L...` | 77 | 13.95 KB | Consider caching or using StringPool |
| `ClrInstanceID` | 122 | 5.67 KB | Consider caching or using StringPool |
| `Keywords` | 151 | 5.57 KB | Consider string.Intern() for frequently used short strings |
| `Version` | 152 | 5.31 KB | Consider string.Intern() for frequently used short strings |
| `Level` | 152 | 4.72 KB | Consider string.Intern() for frequently used short strings |
| `file:///project/profiler/_build/bin/Rele...` | 18 | 4.12 KB | Consider caching or using StringPool |
| `/project/profiler/build_data/Application...` | 19 | 3.23 KB | Consider caching or using StringPool |
| `Name:\tSamples.BuggyBi\nUmask:\t0022\nSt...` | 2 | 2.94 KB | Consider caching or using StringPool |
| `8.0.0.0` | 46 | 1.58 KB | Consider string.Intern() for frequently used short strings |
| `/project/shared/bin/monitoring-home/linu...` | 10 | 1.49 KB | Consider caching or using StringPool |
| `libSystem.Native` | 29 | 1.48 KB | Consider caching or using StringPool |
| `System.Diagnostics.Tracing.EventOpcode` | 15 | 1.34 KB | Consider caching or using StringPool |

## 🔒 Security Analysis

**Overall Risk**: None

Security Analysis: None risk. Found 0 potential vulnerabilities (0 critical, 0 high, 0 medium).

### Recommendations

- No critical security issues detected. Continue monitoring and apply security best practices.

## 📦 Loaded Modules

| Module                              | Base Address       | Symbols |
|-------------------------------------|--------------------|---------|
| Samples.BuggyBits.dbg               | 0x00006539f7d15000 | ✓       |
| [vdso] (0x00007f3156dc5000)         | 0x00007f3156dc5000 | ✗       |
| libdatadog_profiling.so             | 0x00007ef03c000000 | ✓       |
| libSystem.Native.so                 | 0x00007ef03eecf000 | ✓       |
| libSystem.Native.so.dbg             | Unknown            | ✗       |
| libclrjit.so                        | 0x00007ef03ef49000 | ✓       |
| libclrjit.so.dbg                    | Unknown            | ✗       |
| Datadog.Tracer.Native.so            | 0x00007f30d5600000 | ✓       |
| libdatadog_profiling.so             | 0x00007ef03c000000 | ✓       |
| Datadog.Profiler.Native.so          | 0x00007f30d6600000 | ✓       |
| Datadog.Trace.ClrProfiler.Native.so | 0x00007f30d69bf000 | ✓       |
| libcoreclr.so                       | 0x00007f315633d000 | ✓       |
| libcoreclr.so.dbg                   | Unknown            | ✗       |
| libhostpolicy.so                    | 0x00007f3156a99000 | ✓       |
| libhostpolicy.so.dbg                | Unknown            | ✗       |
| libhostfxr.so                       | 0x00007f3156ae9000 | ✓       |
| libhostfxr.so.dbg                   | Unknown            | ✗       |
| Datadog.Linux.ApiWrapper.x64.so     | 0x00007f3156db9000 | ✓       |


## 🖥️ Process Information

### Environment Variables (87 total)

| Variable | Value |
|----------|-------|
| `ASPNETCORE_URLS` |  |
| `COMPlus_DbgEnableMiniDump` | 1 |
| `COMPlus_DbgMiniDumpType` | 4 |
| `COMPlus_EnableCrashReport` | 1 |
| `COMPlus_TieredCompilation` | 0 |
| `CORECLR_ENABLE_PROFILING` | 1 |
| `CORECLR_PROFILER` | {846F5F1C-F9AE-4B07-969E-05C26BC060D8} |
| `CORECLR_PROFILER_PATH` | /project/shared/bin/monitoring-home/linux-musl-x64/Datadog.Trace.ClrProfiler.Native.so |
| `DD_CIVISIBILITY_CODE_COVERAGE_SNK_FILEPATH` | /project/Datadog.Trace.snk |
| `DD_CLR_ENABLE_NGEN` | 1 |
| `DD_DOTNET_TRACER_HOME` | /project/shared/bin/monitoring-home |
| `DD_INTERNAL_PROFILING_OUTPUT_DIR` | /project/profiler/build_data/ApplicationInfoTest/UseTracerServiceName/net8.0/pprofs |
| `DD_LOGGER_BUILD_BUILDID` | 191979 |
| `DD_LOGGER_BUILD_DEFINITIONNAME` | consolidated-pipeline |
| `DD_LOGGER_BUILD_REPOSITORY_URI` | https://github.com/DataDog/dd-trace-dotnet |
| `DD_LOGGER_BUILD_REQUESTEDFOREMAIL` |  |
| `DD_LOGGER_BUILD_REQUESTEDFORID` | 00000002-0000-8888-8000-000000000000 |
| `DD_LOGGER_BUILD_SOURCEBRANCH` | refs/heads/master |
| `DD_LOGGER_BUILD_SOURCEBRANCHNAME` | master |
| `DD_LOGGER_BUILD_SOURCESDIRECTORY` | /project |
| `DD_LOGGER_BUILD_SOURCEVERSION` | 14f72c17f2e0ed8ce952d10647a9418bd4d28553 |
| `DD_LOGGER_BUILD_SOURCEVERSIONMESSAGE` | Reduce allocations in `TraceContext` (#7874) |
| `DD_LOGGER_DD_API_KEY` | <redacted> |
| `DD_LOGGER_DD_ENV` | CI |
| `DD_LOGGER_DD_SERVICE` | dd-trace-dotnet |
| `DD_LOGGER_DD_TAGS` | test.configuration.job:Test alpine |
| `DD_LOGGER_DD_TRACE_LOG_DIRECTORY` | /project/artifacts/build_data/infra_logs |
| `DD_LOGGER_ENABLED` | true |
| `DD_LOGGER_SYSTEM_JOBDISPLAYNAME` | Test alpine |
| `DD_LOGGER_SYSTEM_JOBID` | 5635f724-ec42-5e82-7816-fb263b6cfcc0 |
| `DD_LOGGER_SYSTEM_PULLREQUEST_SOURCEBRANCH` |  |
| `DD_LOGGER_SYSTEM_PULLREQUEST_SOURCECOMMITID` |  |
| `DD_LOGGER_SYSTEM_PULLREQUEST_SOURCEREPOSITORYURI` |  |
| `DD_LOGGER_SYSTEM_STAGEDISPLAYNAME` | profiler_integration_tests_linux |
| `DD_LOGGER_SYSTEM_TASKINSTANCEID` | e80b9b00-921f-539a-741c-f91926c040ef |
| `DD_LOGGER_SYSTEM_TEAMFOUNDATIONSERVERURI` | https://dev.azure.com/datadoghq/ |
| `DD_LOGGER_SYSTEM_TEAMPROJECTID` | a51c4863-3eb4-4c5d-878a-58b41a049e4e |
| `DD_LOGGER_TF_BUILD` | True |
| `DD_NATIVELOADER_CONFIGFILE` | /tmp/tmpEAeLGI.tmp |
| `DD_PROFILING_ENABLED` | 1 |
| `DD_PROFILING_UPLOAD_PERIOD` | 3 |
| `DD_TESTING_OUPUT_DIR` | /project/profiler/build_data |
| `DD_TRACE_AGENT_HOSTNAME` | 127.0.0.1 |
| `DD_TRACE_AGENT_PORT` | 38161 |
| `DD_TRACE_DEBUG` | 1 |
| `DD_TRACE_ENABLED` | 1 |
| `DD_TRACE_LOG_DIRECTORY` | /project/profiler/build_data/ApplicationInfoTest/UseTracerServiceName/net8.0/logs |
| `DOTNET_CLI_TELEMETRY_OPTOUT` | 1 |
| `DOTNET_CLI_UI_LANGUAGE` | en |
| `DOTNET_GENERATE_ASPNET_CERTIFICATE` | false |
| `DOTNET_HOST_PATH` | /usr/share/dotnet/dotnet |
| `DOTNET_MULTILEVEL_LOOKUP` | 0 |
| `DOTNET_NOLOGO` | 1 |
| `DOTNET_NUGET_SIGNATURE_VERIFICATION` | True |
| `DOTNET_ROLL_FORWARD_TO_PRERELEASE` | 1 |
| `DOTNET_ROOT` | /usr/share/dotnet |
| `DOTNET_ROOT_X64` | /usr/share/dotnet |
| `DOTNET_SDK_VERSION` | 10.0.100 |
| `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` | 0 |
| `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` | false |
| `DOTNET_USE_POLLING_FILE_WATCHER` | true |
| `HOME` | /root |
| `HOSTNAME` | integrationtests |
| `IncludeMinorPackageVersions` | false |
| `IsAlpine` | true |
| `LDAP_SERVER` | openldap-server:389 |
| `LD_PRELOAD` | /project/shared/bin/monitoring-home/linux-musl-x64/Datadog.Linux.ApiWrapper.x64.so |
| `MSBUILDENSURESTDOUTFORTASKPROCESSES` | 1 |
| `MSBUILDFAILONDRIVEENUMERATINGWILDCARD` | 1 |
| `MSBUILDUSESERVER` | 0 |
| `MSBuildExtensionsPath` | /usr/share/dotnet/sdk/10.0.100/ |
| `MSBuildLoadMicrosoftTargetsReadOnly` | true |
| `MSBuildSDKsPath` | /usr/share/dotnet/sdk/10.0.100/Sdks |
| `MonitoringHomeDirectory` | /project/shared/bin/monitoring-home |
| `NUGET_XMLDOC_MODE` | skip |
| `NugetPackageDirectory` | /project/packages |
| `PATH` | /usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin |
| `PreferredUILang` | en |
| `QUIC_LTTng` | 0 |
| `VSLANG` | 9 |
| `VSTEST_DOTNET_ROOT_ARCHITECTURE` | X64 |
| `VSTEST_DOTNET_ROOT_PATH` | /usr/share/dotnet |
| `_MSBUILDTLENABLED` | 0 |
| `artifacts` | /project/tracer/src/bin/artifacts |
| `baseImage` | alpine |
| `enable_crash_dumps` | true |
| `framework` | netcoreapp3.1 |

### Extraction Metadata

- **argc**: 0
- **Note**: ⚠️ Sensitive environment variables were filtered

## 💡 Recommendations

- No call stack found. Ensure symbols are configured correctly.
- Some modules are missing symbols. Upload symbol files for better analysis.
- Exception: System.BadImageFormatException - An attempt was made to load a program with an incorrect format.
- All 6 thread pool workers are running. This may indicate thread pool saturation.

---

*Report generated by Debugger MCP Server v1.0.0*

*Timestamp: 2025-12-09 15:30:23 UTC*
