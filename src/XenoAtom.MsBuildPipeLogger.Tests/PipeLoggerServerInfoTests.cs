// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

namespace XenoAtom.MsBuildPipeLogger.Tests;

[TestClass]
public class PipeLoggerServerInfoTests
{
    [TestMethod]
    public void GetLoggerAssemblyPath_ReturnsBundledLoggerInIsolatedOutputDirectory()
    {
        var loggerAssemblyPath = PipeLoggerServer.GetLoggerAssemblyPath();
        var loggerDirectory = Path.GetDirectoryName(loggerAssemblyPath);

        Assert.IsNotNull(loggerDirectory);
        Assert.AreEqual(PipeLoggerServer.LoggerAssemblyFileName, Path.GetFileName(loggerAssemblyPath));
        Assert.AreEqual(PipeLoggerServer.LoggerDirectoryName, Path.GetFileName(loggerDirectory));
        Assert.IsTrue(File.Exists(loggerAssemblyPath), $"Expected the bundled logger assembly at '{loggerAssemblyPath}'.");
        CollectionAssert.AreEquivalent(new[] { PipeLoggerServer.LoggerAssemblyFileName }, Directory.GetFiles(loggerDirectory).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void GetLoggerSpecification_ReturnsMsBuildLoggerSyntax()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "base");
        var expectedPath = Path.Combine(baseDirectory, PipeLoggerServer.LoggerDirectoryName, PipeLoggerServer.LoggerAssemblyFileName);

        var specification = PipeLoggerServer.GetLoggerSpecification(baseDirectory, "name=pipe");

        Assert.AreEqual($"{PipeLoggerServer.LoggerTypeName},{expectedPath};name=pipe", specification);
    }

    [TestMethod]
    public void GetLoggerSpecification_WithInvalidParameters_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PipeLoggerServer.GetLoggerSpecification(null!));
        Assert.Throws<ArgumentException>(() => PipeLoggerServer.GetLoggerSpecification(" "));
        Assert.Throws<ArgumentNullException>(() => PipeLoggerServer.GetLoggerSpecification(null!, "name=pipe"));
        Assert.Throws<ArgumentException>(() => PipeLoggerServer.GetLoggerSpecification(" ", "name=pipe"));
    }

    [TestMethod]
    public void CreateUniquePipeName_ReturnsUsableUniqueNames()
    {
        var first = PipeLoggerServer.CreateUniquePipeName();
        var second = PipeLoggerServer.CreateUniquePipeName();

        Assert.AreNotEqual(first, second);
        Assert.StartsWith(PipeLoggerServer.DefaultPipeNamePrefix, first);
        Assert.IsTrue(
            first.Length <= PipeLoggerServer.GetMaximumPipeNameLength(),
            $"'{first}' is longer than the {PipeLoggerServer.GetMaximumPipeNameLength()} characters allowed on this platform.");

        // The point of the helper is that the name can actually be turned into a pipe.
        using var server = new NamedPipeLoggerServer(first);
        Assert.AreEqual(first, server.PipeName);
    }

    [TestMethod]
    public void CreateUniquePipeName_UsesTheLongestUniquePartThatFits()
    {
        var maximumLength = PipeLoggerServer.GetMaximumPipeNameLength();
        var prefix = new string('p', maximumLength - 20);

        var pipeName = PipeLoggerServer.CreateUniquePipeName(prefix);

        Assert.AreEqual(maximumLength, pipeName.Length);
        Assert.StartsWith(prefix, pipeName);
    }

    [TestMethod]
    public void CreateUniquePipeName_WithInvalidPrefix_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PipeLoggerServer.CreateUniquePipeName(null!));
        Assert.Throws<ArgumentException>(() => PipeLoggerServer.CreateUniquePipeName(new string('p', PipeLoggerServer.GetMaximumPipeNameLength())));
    }

    [TestMethod]
    public void NamedPipeLoggerServer_WithTooLongPipeName_ThrowsWithAnActionableMessage()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Only Unix domain socket paths are short enough to make this reachable.");
        }

        var pipeName = new string('p', PipeLoggerServer.GetMaximumPipeNameLength() + 1);

        var exception = Assert.Throws<ArgumentException>(() => new NamedPipeLoggerServer(pipeName));

        Assert.AreEqual("pipeName", exception.ParamName);
        Assert.Contains(nameof(PipeLoggerServer.CreateUniquePipeName), exception.Message);
    }
}
