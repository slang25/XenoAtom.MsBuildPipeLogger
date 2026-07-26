// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

namespace XenoAtom.MsBuildPipeLogger.Tests;

[TestClass]
public class NamedPipeNameTests
{
    [TestMethod]
    public void CreatePipeName_FitsWithinThePlatformLimit()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName();

        Assert.IsTrue(
            pipeName.Length <= NamedPipeLoggerServer.MaxPipeNameLength,
            $"'{pipeName}' is {pipeName.Length} characters but the limit is {NamedPipeLoggerServer.MaxPipeNameLength}.");
    }

    [TestMethod]
    public void CreatePipeName_ReturnsUniqueNames()
    {
        Assert.AreNotEqual(NamedPipeLoggerServer.CreatePipeName(), NamedPipeLoggerServer.CreatePipeName());
    }

    [TestMethod]
    public void CreatePipeName_UsesThePrefix()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName("prefix-");

        StringAssert.StartsWith(pipeName, "prefix-");
        Assert.IsTrue(pipeName.Length <= NamedPipeLoggerServer.MaxPipeNameLength);
    }

    [TestMethod]
    public void CreatePipeName_ThrowsForAPrefixThatLeavesNoRoom()
    {
        var prefix = new string('x', NamedPipeLoggerServer.MaxPipeNameLength);

        Assert.ThrowsExactly<ArgumentException>(() => NamedPipeLoggerServer.CreatePipeName(prefix));
    }

    [TestMethod]
    public void CreatePipeName_ThrowsForANullPrefix()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => NamedPipeLoggerServer.CreatePipeName(null!));
    }

    [TestMethod]
    public void Constructor_AcceptsANameOfExactlyTheMaximumLength()
    {
        // Guards the off-by-one in the limit: the socket path buffer has to hold a terminator too, so
        // a name computed as "buffer size minus the fixed overhead" is one character too long.
        var pipeName = "xa-" + new string('x', NamedPipeLoggerServer.MaxPipeNameLength - 3);

        Assert.AreEqual(NamedPipeLoggerServer.MaxPipeNameLength, pipeName.Length);
        using var server = new NamedPipeLoggerServer(pipeName);
    }

    [TestMethod]
    public void Constructor_ReportsANameThatIsTooLongForThePlatform()
    {
        // Without this check the failure surfaces from deep inside UnixDomainSocketEndPoint as an
        // ArgumentOutOfRangeException about a path the caller never supplied.
        var pipeName = new string('x', NamedPipeLoggerServer.MaxPipeNameLength + 1);

        var exception = Assert.ThrowsExactly<ArgumentException>(() => new NamedPipeLoggerServer(pipeName));
        StringAssert.Contains(exception.Message, nameof(NamedPipeLoggerServer.CreatePipeName));
    }
}
