// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using Microsoft.Build.Framework;

namespace XenoAtom.MsBuildPipeLogger.Logger.Tests;

[TestClass]
public class PipeLoggerTests
{
    [TestMethod]
    public void Initialize_ForwardsAnyEventsToPipeWriter()
    {
        var eventSource = new TestEventSource();
        var logger = new TestPipeLogger();
        var buildEvent = new BuildMessageEventArgs("message", "help", "sender", MessageImportance.High);

        logger.Initialize(eventSource);
        eventSource.RaiseAnyEvent(buildEvent);
        logger.Shutdown();

        CollectionAssert.AreEqual(new[] { buildEvent }, logger.Writer.WrittenEvents.ToArray());
    }

    [TestMethod]
    public void Shutdown_UnsubscribesFromEventSourceAndDisposesPipeWriter()
    {
        var eventSource = new TestEventSource();
        var logger = new TestPipeLogger();
        var beforeShutdown = new BuildMessageEventArgs("before", null, null, MessageImportance.Normal);
        var afterShutdown = new BuildMessageEventArgs("after", null, null, MessageImportance.Normal);

        logger.Initialize(eventSource);
        eventSource.RaiseAnyEvent(beforeShutdown);
        logger.Shutdown();
        eventSource.RaiseAnyEvent(afterShutdown);

        Assert.IsTrue(logger.Writer.IsDisposed);
        CollectionAssert.AreEqual(new[] { beforeShutdown }, logger.Writer.WrittenEvents.ToArray());
    }

    [TestMethod]
    public void Shutdown_CanBeCalledBeforeInitializeAndMoreThanOnce()
    {
        var logger = new TestPipeLogger();

        logger.Shutdown();
        logger.Initialize(new TestEventSource());
        logger.Shutdown();
        logger.Shutdown();

        Assert.IsTrue(logger.Writer.IsDisposed);
    }

    [TestMethod]
    public void EventForwarding_WhenPipeWriterThrows_DoesNotThrow()
    {
        var eventSource = new TestEventSource();
        var logger = new ThrowingPipeLogger();

        logger.Initialize(eventSource);
        eventSource.RaiseAnyEvent(new BuildMessageEventArgs("message", null, null, MessageImportance.Normal));
        logger.Shutdown();
    }

    [TestMethod]
    public void Initialize_SetsMsBuildEnvironmentVariables()
    {
        var oldTargetOutputLogging = Environment.GetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING");
        var oldLogImports = Environment.GetEnvironmentVariable("MSBUILDLOGIMPORTS");
        try
        {
            var logger = new TestPipeLogger();
            logger.Initialize(new TestEventSource());

            Assert.AreEqual("true", Environment.GetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING"));
            Assert.AreEqual("1", Environment.GetEnvironmentVariable("MSBUILDLOGIMPORTS"));

            logger.Shutdown();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING", oldTargetOutputLogging);
            Environment.SetEnvironmentVariable("MSBUILDLOGIMPORTS", oldLogImports);
        }
    }

    [TestMethod]
    public void Shutdown_RestoresMsBuildEnvironmentVariables()
    {
        var oldTargetOutputLogging = Environment.GetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING");
        var oldLogImports = Environment.GetEnvironmentVariable("MSBUILDLOGIMPORTS");
        try
        {
            // These are process-wide and MSBuild reuses nodes across builds, so a variable left set would
            // raise the event volume of the next, unrelated build that lands on this node.
            Environment.SetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING", null);
            Environment.SetEnvironmentVariable("MSBUILDLOGIMPORTS", "existing");

            var logger = new TestPipeLogger();
            logger.Initialize(new TestEventSource());
            logger.Shutdown();

            Assert.IsNull(Environment.GetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING"));
            Assert.AreEqual("existing", Environment.GetEnvironmentVariable("MSBUILDLOGIMPORTS"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING", oldTargetOutputLogging);
            Environment.SetEnvironmentVariable("MSBUILDLOGIMPORTS", oldLogImports);
        }
    }

    [TestMethod]
    public void Shutdown_WithoutInitialize_LeavesEnvironmentVariablesAlone()
    {
        var oldLogImports = Environment.GetEnvironmentVariable("MSBUILDLOGIMPORTS");
        try
        {
            Environment.SetEnvironmentVariable("MSBUILDLOGIMPORTS", "existing");

            new TestPipeLogger().Shutdown();

            Assert.AreEqual("existing", Environment.GetEnvironmentVariable("MSBUILDLOGIMPORTS"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBUILDLOGIMPORTS", oldLogImports);
        }
    }

    private sealed class TestPipeLogger : PipeLogger
    {
        public TestPipeWriter Writer { get; } = new();

        protected override IPipeWriter InitializePipeWriter() => Writer;
    }

    private sealed class TestPipeWriter : IPipeWriter
    {
        public List<BuildEventArgs> WrittenEvents { get; } = new();

        public bool IsDisposed { get; private set; }

        public void Write(BuildEventArgs e)
        {
            WrittenEvents.Add(e);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class ThrowingPipeLogger : PipeLogger
    {
        protected override IPipeWriter InitializePipeWriter() => new ThrowingPipeWriter();
    }

    private sealed class ThrowingPipeWriter : IPipeWriter
    {
        public void Write(BuildEventArgs e)
        {
            throw new InvalidOperationException("The writer failed.");
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestEventSource : IEventSource
    {
        public event BuildMessageEventHandler? MessageRaised { add { } remove { } }

        public event BuildErrorEventHandler? ErrorRaised { add { } remove { } }

        public event BuildWarningEventHandler? WarningRaised { add { } remove { } }

        public event BuildStartedEventHandler? BuildStarted { add { } remove { } }

        public event BuildFinishedEventHandler? BuildFinished { add { } remove { } }

        public event ProjectStartedEventHandler? ProjectStarted { add { } remove { } }

        public event ProjectFinishedEventHandler? ProjectFinished { add { } remove { } }

        public event TargetStartedEventHandler? TargetStarted { add { } remove { } }

        public event TargetFinishedEventHandler? TargetFinished { add { } remove { } }

        public event TaskStartedEventHandler? TaskStarted { add { } remove { } }

        public event TaskFinishedEventHandler? TaskFinished { add { } remove { } }

        public event CustomBuildEventHandler? CustomEventRaised { add { } remove { } }

        public event BuildStatusEventHandler? StatusEventRaised { add { } remove { } }

        public event AnyEventHandler? AnyEventRaised;

        public void RaiseAnyEvent(BuildEventArgs eventArgs)
        {
            AnyEventRaised?.Invoke(this, eventArgs);
        }
    }
}
