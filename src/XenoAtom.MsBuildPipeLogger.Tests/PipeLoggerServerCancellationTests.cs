// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Microsoft.Build.Framework;

namespace XenoAtom.MsBuildPipeLogger.Tests;

[TestClass]
[DoNotParallelize]
public class PipeLoggerServerCancellationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task NamedPipe_ReadReturnsNullWhenCanceledBeforeClientConnects()
    {
        var buildEvent = await ReadWithCancellationAsync(
            token => new NamedPipeLoggerServer(CreatePipeName(), token)).ConfigureAwait(false);

        Assert.IsNull(buildEvent);
    }

    [TestMethod]
    public async Task AnonymousPipe_ReadReturnsNullWhenCanceledBeforeClientConnects()
    {
        var buildEvent = await ReadWithCancellationAsync(
            token => new AnonymousPipeLoggerServer(token)).ConfigureAwait(false);

        Assert.IsNull(buildEvent);
    }

    [TestMethod]
    public async Task AnonymousPipe_ReadReturnsNullWhenCanceledAfterClientHandleIsCreated()
    {
        using var tokenSource = new CancellationTokenSource();
        using var server = new AnonymousPipeLoggerServer(tokenSource.Token);
        _ = server.GetClientHandle();
        var readTask = Task.Run(server.Read);

        tokenSource.CancelAfter(TimeSpan.FromMilliseconds(100));

        var buildEvent = await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        Assert.IsNull(buildEvent);
    }

    [TestMethod]
    public async Task NamedPipe_DisposeUnblocksPendingRead()
    {
        using var server = new NamedPipeLoggerServer(CreatePipeName());
        var readTask = Task.Run(server.Read);

        await Task.Delay(100).ConfigureAwait(false);
        server.Dispose();

        var buildEvent = await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        Assert.IsNull(buildEvent);
    }

    [TestMethod]
    public async Task ConnectSocketExceptionAfterCancellationCompletesRead()
    {
        using var tokenSource = new CancellationTokenSource();
        using var server = new SocketExceptionOnCancellationServer(tokenSource.Token);
        var readTask = Task.Run(server.Read);

        tokenSource.Cancel();

        var buildEvent = await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        Assert.IsNull(buildEvent);
    }

    [TestMethod]
    public async Task AnonymousPipe_DisposeUnblocksPendingRead()
    {
        using var server = new AnonymousPipeLoggerServer();
        var readTask = Task.Run(server.Read);

        await Task.Delay(100).ConfigureAwait(false);
        server.Dispose();

        var buildEvent = await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        Assert.IsNull(buildEvent);
    }

    [TestMethod]
    public async Task AnonymousPipe_DisposeUnblocksPendingReadAfterClientHandleIsCreated()
    {
        using var server = new AnonymousPipeLoggerServer();
        _ = server.GetClientHandle();
        var readTask = Task.Run(server.Read);

        await Task.Delay(100).ConfigureAwait(false);
        server.Dispose();

        var buildEvent = await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        Assert.IsNull(buildEvent);
    }

    [TestMethod]
    public void Cancel_DoesNotSynchronouslyWaitForTransportDispose()
    {
        using var tokenSource = new CancellationTokenSource();
        var pipeStream = new BlockingDisposePipeStream();
        using var server = new BlockingDisposeServer(pipeStream, tokenSource.Token);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            tokenSource.Cancel();
            stopwatch.Stop();

            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Cancel took {stopwatch.Elapsed}.");
            Assert.IsTrue(pipeStream.DisposeStarted.Wait(TestTimeout), "The pipe stream dispose was not requested.");
        }
        finally
        {
            pipeStream.AllowDispose.Set();
        }
    }

    private static async Task<PipeBuildEventArgs?> ReadWithCancellationAsync(Func<CancellationToken, IPipeLoggerServer> createServer)
    {
        using var tokenSource = new CancellationTokenSource();
        using var server = createServer(tokenSource.Token);
        tokenSource.CancelAfter(TimeSpan.FromMilliseconds(100));
        return await Task.Run(server.Read).WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    private static string CreatePipeName() => PipeLoggerServer.CreateUniquePipeName("xa-test");

    private sealed class SocketExceptionOnCancellationServer : PipeLoggerServer<AnonymousPipeServerStream>
    {
        private const int UnixOperationCanceledErrorCode = 125;

        public SocketExceptionOnCancellationServer(CancellationToken cancellationToken)
            : base(new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None), cancellationToken, false)
        {
            StartReading();
        }

        protected override void Connect()
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(1);
            }

            throw new SocketException(UnixOperationCanceledErrorCode);
        }
    }

    private sealed class BlockingDisposeServer : PipeLoggerServer<BlockingDisposePipeStream>
    {
        public BlockingDisposeServer(BlockingDisposePipeStream pipeStream, CancellationToken cancellationToken)
            : base(pipeStream, cancellationToken, false)
        {
            StartReading();
        }

        protected override void Connect()
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(1);
            }

            throw new OperationCanceledException(CancellationToken);
        }
    }

    private sealed class BlockingDisposePipeStream : PipeStream
    {
        public BlockingDisposePipeStream()
            : base(PipeDirection.In, 0)
        {
        }

        public ManualResetEventSlim DisposeStarted { get; } = new(false);

        public ManualResetEventSlim AllowDispose { get; } = new(false);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeStarted.Set();
                AllowDispose.Wait(TestTimeout);
                DisposeStarted.Dispose();
                AllowDispose.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
