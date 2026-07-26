// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Text;

namespace XenoAtom.MsBuildPipeLogger.Tests;

[TestClass]
public class PipeNameLengthTests
{
    [TestMethod]
    public void CreateUniquePipeName_FitsThePlatformLimit()
    {
        var pipeName = PipeLoggerServer.CreateUniquePipeName();

        Assert.IsTrue(
            Encoding.UTF8.GetByteCount(pipeName) <= PipeLoggerServer.GetMaximumPipeNameLength(),
            $"'{pipeName}' ({pipeName.Length}) exceeds the {PipeLoggerServer.GetMaximumPipeNameLength()} character limit.");
    }

    [TestMethod]
    public void CreateUniquePipeName_IsAcceptedByTheServer()
    {
        // The regression: a name that is reasonable on Windows becomes an over-long domain socket path
        // on Unix, so the only way to know a name is usable is to hand it to the server.
        using var server = new NamedPipeLoggerServer(PipeLoggerServer.CreateUniquePipeName());

        Assert.IsNotNull(server.PipeName);
    }

    [TestMethod]
    public void CreateUniquePipeName_IsUnique()
    {
        var names = Enumerable.Range(0, 100).Select(_ => PipeLoggerServer.CreateUniquePipeName()).ToArray();

        Assert.AreEqual(names.Length, names.Distinct().Count());
    }

    [TestMethod]
    public void CreateUniquePipeName_KeepsTheUniquePortionWhenThePrefixCannotFit()
    {
        var maximum = PipeLoggerServer.GetMaximumPipeNameLength();
        var longPrefix = new string('p', maximum + 50);

        var pipeName = PipeLoggerServer.CreateUniquePipeName(longPrefix);

        Assert.IsTrue(pipeName.Length <= maximum);
        // Truncating the prefix must not cost uniqueness.
        Assert.AreNotEqual(pipeName, PipeLoggerServer.CreateUniquePipeName(longPrefix));
    }

    [TestMethod]
    public void CreateUniquePipeName_IncludesThePrefixWhenItFits()
    {
        var pipeName = PipeLoggerServer.CreateUniquePipeName("myapp");

        Assert.IsTrue(pipeName.StartsWith("myapp-", StringComparison.Ordinal), pipeName);
    }

    [TestMethod]
    public void CreateUniquePipeName_WithInvalidPrefix_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PipeLoggerServer.CreateUniquePipeName(null!));
        Assert.Throws<ArgumentException>(() => PipeLoggerServer.CreateUniquePipeName("a/b"));
        Assert.Throws<ArgumentException>(() => PipeLoggerServer.CreateUniquePipeName("a\\b"));
    }

    [TestMethod]
    public void GetMaximumPipeNameLength_LeavesRoomForTheSocketPathOnUnix()
    {
        var maximum = PipeLoggerServer.GetMaximumPipeNameLength();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.AreEqual(256, maximum);
            return;
        }

        // The name has to share a 104 byte socket path with the temp directory and the CoreFxPipe_ prefix.
        var prefixLength = Encoding.UTF8.GetByteCount(Path.Combine(Path.GetTempPath(), "CoreFxPipe_"));
        Assert.AreEqual(104 - prefixLength, maximum);
    }

    [TestMethod]
    public void NamedPipeLoggerServer_WithTooLongName_ThrowsArgumentExceptionNamingTheHelper()
    {
        var tooLong = new string('p', PipeLoggerServer.GetMaximumPipeNameLength() + 1);

        // Without the guard this surfaces as ArgumentOutOfRangeException about a 'path' parameter the
        // caller never passed, which is what made the real failure so hard to place.
        var exception = Assert.Throws<ArgumentException>(() => new NamedPipeLoggerServer(tooLong));

        Assert.AreEqual("pipeName", exception.ParamName);
        StringAssert.Contains(exception.Message, nameof(PipeLoggerServer.CreateUniquePipeName));
    }
}
