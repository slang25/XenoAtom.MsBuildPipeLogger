// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.IO;

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// Reads records in the XenoAtom pipe wire format and materializes them as <see cref="PipeBuildEventArgs"/>.
/// Each record is <c>[kind][payload-length][payload]</c>; unknown record kinds are skipped and any trailing
/// bytes in a known record are ignored, so a newer writer stays readable by an older reader (and vice versa).
/// </summary>
internal sealed class PipeEventReader : IDisposable
{
    // Generous upper bound for a single record payload (and, via WireIO, for a base header): a
    // corrupt or hostile length prefix must fail cleanly instead of triggering a huge allocation.
    private const int MaxRecordLength = WireIO.MaxFrameLength;

    private const int InitialPayloadCapacity = 4096;

    private readonly BinaryReader _reader;
    private readonly WireBufferReader _payloadReader = new();
    private byte[] _payload = Array.Empty<byte>();

    public PipeEventReader(Stream stream) => _reader = new BinaryReader(stream);

    public PipeBuildEventArgs? Read()
    {
        PipeRecordKind kind;
        try
        {
            kind = (PipeRecordKind)_reader.Read7Bit();
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        if (kind == PipeRecordKind.EndOfFile)
        {
            return null;
        }

        var length = _reader.Read7Bit();
        if (length < 0 || length > MaxRecordLength)
        {
            throw new InvalidDataException($"Record length {length} is negative or exceeds the {MaxRecordLength} byte limit.");
        }

        if (!FillPayload(length))
        {
            // The stream ended part-way through a record.
            return null;
        }

        _payloadReader.Reset(_payload, length);
        try
        {
            var b = WireBaseFields.ReadFramed(_payloadReader);
            return Materialize(kind, b, _payloadReader);
        }
        catch (IOException)
        {
            // A record we cannot fully parse (e.g. truncated, or bytes that decode to an invalid
            // string length — WireBufferReader throws EndOfStreamException, an IOException). The
            // payload was already consumed from the underlying stream, so surface a placeholder and
            // keep reading rather than aborting the whole stream.
            return new PipeCustomBuildEventArgs();
        }
        catch (FormatException)
        {
            // A malformed varint inside the payload (WireIO.Read7Bit). Same recovery as above.
            return new PipeCustomBuildEventArgs();
        }
        catch (ArgumentException)
        {
            // Bytes that decode to a value the event types reject, e.g. a corrupt tick count or
            // DateTimeKind in WireBufferReader.ReadDateTime (ArgumentOutOfRangeException derives from
            // ArgumentException). Same recovery as above. Note this is distinct from an oversized
            // length prefix, which is a resource-exhaustion guard and is deliberately left to throw.
            return new PipeCustomBuildEventArgs();
        }
    }

    public void Dispose() => _reader.Dispose();

    /// <summary>
    /// Reads the next <paramref name="length"/> payload bytes into the reusable record buffer,
    /// growing it geometrically so a long-lived reader converges on the largest record it sees
    /// instead of allocating per record.
    /// </summary>
    private bool FillPayload(int length)
    {
        if (_payload.Length < length)
        {
            var capacity = Math.Max(_payload.Length * 2, InitialPayloadCapacity);
            _payload = new byte[Math.Min(Math.Max(capacity, length), MaxRecordLength)];
        }

        var read = 0;
        while (read < length)
        {
            var count = _reader.Read(_payload, read, length - read);
            if (count <= 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private static PipeBuildEventArgs Materialize(PipeRecordKind kind, WireBaseFields b, WireBufferReader r) => kind switch
    {
        PipeRecordKind.BuildStarted => ApplyBase(new PipeBuildStartedEventArgs { BuildEnvironment = ReadProperties(r) }, b),
        PipeRecordKind.BuildFinished => ApplyBase(new PipeBuildFinishedEventArgs { Succeeded = r.ReadBoolean() }, b),
        PipeRecordKind.ProjectStarted => ApplyBase(
            new PipeProjectStartedEventArgs
            {
                ProjectId = r.Read7Bit(),
                ProjectFile = r.ReadNullableString(),
                TargetNames = r.ReadNullableString(),
                ToolsVersion = r.ReadNullableString(),
                GlobalProperties = ReadProperties(r),
                Properties = ReadProperties(r),
                Items = ReadItems(r),
                ParentProjectBuildEventContext = ReadOptionalContext(r),
            }, b),
        PipeRecordKind.ProjectFinished => ApplyBase(
            new PipeProjectFinishedEventArgs { ProjectFile = r.ReadNullableString(), Succeeded = r.ReadBoolean() }, b),
        PipeRecordKind.ProjectEvaluationStarted => ApplyBase(
            new PipeProjectEvaluationStartedEventArgs { ProjectFile = r.ReadNullableString() }, b),
        PipeRecordKind.ProjectEvaluationFinished => ApplyBase(
            new PipeProjectEvaluationFinishedEventArgs
            {
                ProjectFile = r.ReadNullableString(),
                Properties = ReadProperties(r),
                Items = ReadItems(r),
            }, b),
        PipeRecordKind.TargetStarted => ApplyBase(
            new PipeTargetStartedEventArgs
            {
                TargetName = r.ReadNullableString(),
                ProjectFile = r.ReadNullableString(),
                TargetFile = r.ReadNullableString(),
                ParentTarget = r.ReadNullableString(),
                BuildReason = r.HasRemaining ? (PipeTargetBuiltReason)r.Read7Bit() : PipeTargetBuiltReason.None,
            }, b),
        PipeRecordKind.TargetFinished => ApplyBase(
            new PipeTargetFinishedEventArgs
            {
                TargetName = r.ReadNullableString(),
                ProjectFile = r.ReadNullableString(),
                TargetFile = r.ReadNullableString(),
                Succeeded = r.ReadBoolean(),
                TargetOutputs = r.HasRemaining ? ReadTaskItems(r, null) : Array.Empty<PipeItem>(),
            }, b),
        PipeRecordKind.TaskStarted => ApplyBase(
            new PipeTaskStartedEventArgs
            {
                TaskName = r.ReadNullableString(),
                ProjectFile = r.ReadNullableString(),
                TaskFile = r.ReadNullableString(),
            }, b),
        PipeRecordKind.TaskFinished => ApplyBase(
            new PipeTaskFinishedEventArgs
            {
                TaskName = r.ReadNullableString(),
                ProjectFile = r.ReadNullableString(),
                TaskFile = r.ReadNullableString(),
                Succeeded = r.ReadBoolean(),
            }, b),
        PipeRecordKind.Message => ApplyBase(ReadMessageFields(new PipeBuildMessageEventArgs(), r), b),
        PipeRecordKind.TaskCommandLine => ApplyBase(ReadTaskCommandLine(r), b),
        PipeRecordKind.Error => ApplyBase(
            new PipeBuildErrorEventArgs
            {
                Subcategory = r.ReadNullableString(),
                Code = r.ReadNullableString(),
                File = r.ReadNullableString(),
                ProjectFile = r.ReadNullableString(),
                LineNumber = r.Read7Bit(),
                ColumnNumber = r.Read7Bit(),
                EndLineNumber = r.Read7Bit(),
                EndColumnNumber = r.Read7Bit(),
            }, b),
        PipeRecordKind.Warning => ApplyBase(
            new PipeBuildWarningEventArgs
            {
                Subcategory = r.ReadNullableString(),
                Code = r.ReadNullableString(),
                File = r.ReadNullableString(),
                ProjectFile = r.ReadNullableString(),
                LineNumber = r.Read7Bit(),
                ColumnNumber = r.Read7Bit(),
                EndLineNumber = r.Read7Bit(),
                EndColumnNumber = r.Read7Bit(),
            }, b),

        PipeRecordKind.TaskParameter => ApplyBase(ReadTaskParameter(r), b),

        // An event the writer had no dedicated kind for: the payload carries the originating type name.
        PipeRecordKind.Custom => ApplyBase(new PipeCustomBuildEventArgs { EventType = ReadOptionalTypeName(r) }, b),

        // Unknown/future record kind: the payload layout is unknown to this reader, so ignore it entirely.
        _ => ApplyBase(new PipeCustomBuildEventArgs(), b),
    };

    private static PipeTaskParameterEventArgs ReadTaskParameter(WireBufferReader r)
    {
        var kind = (PipeTaskParameterKind)r.Read7Bit();
        var itemType = r.ReadNullableString();
        return new PipeTaskParameterEventArgs
        {
            Kind = kind,
            ItemType = itemType,
            Items = ReadTaskItems(r, itemType),
        };
    }

    // Reads a bare list of task items (spec + metadata) written by the serializer's WriteTaskItems, stamping
    // each with the supplied item type (empty when none, as for target outputs).
    private static IReadOnlyList<PipeItem> ReadTaskItems(WireBufferReader r, string? itemType)
    {
        // Each item is at least a spec string length prefix plus a metadata count (two bytes minimum).
        var count = r.ReadCount(minimumElementSize: 2);
        if (count == 0)
        {
            return Array.Empty<PipeItem>();
        }

        var items = new PipeItem[count];
        for (var i = 0; i < count; i++)
        {
            var spec = r.ReadString();
            items[i] = new PipeItem(itemType ?? string.Empty, spec, ReadProperties(r));
        }

        return items;
    }

    private static PipeTaskCommandLineEventArgs ReadTaskCommandLine(WireBufferReader r)
    {
        var e = new PipeTaskCommandLineEventArgs
        {
            CommandLine = r.ReadNullableString(),
            TaskName = r.ReadNullableString(),
        };
        return ReadMessageFields(e, r);
    }

    private static T ReadMessageFields<T>(T e, WireBufferReader r)
        where T : PipeBuildMessageEventArgs
    {
        e.Importance = (PipeMessageImportance)r.Read7Bit();
        e.Subcategory = r.ReadNullableString();
        e.Code = r.ReadNullableString();
        e.File = r.ReadNullableString();
        e.ProjectFile = r.ReadNullableString();
        e.LineNumber = r.Read7Bit();
        e.ColumnNumber = r.Read7Bit();
        e.EndLineNumber = r.Read7Bit();
        e.EndColumnNumber = r.Read7Bit();
        return e;
    }

    private static string? ReadOptionalTypeName(WireBufferReader r) =>
        r.HasRemaining ? r.ReadNullableString() : null;

    // Reads a possibly-null BuildEventContext appended among the event-specific fields (as written by the
    // serializer's WriteContext). Absent entirely when produced by an older writer, hence the HasRemaining guard.
    private static PipeBuildEventContext? ReadOptionalContext(WireBufferReader r)
    {
        if (!r.HasRemaining || !r.ReadBoolean())
        {
            return null;
        }

        return new PipeBuildEventContext(
            r.Read7Bit(), r.Read7Bit(), r.Read7Bit(), r.Read7Bit(), r.Read7Bit(), r.Read7Bit(), r.Read7Bit());
    }

    private static IReadOnlyList<PipeProperty> ReadProperties(WireBufferReader r)
    {
        // Each property is at least a name string length prefix plus a nullable-value flag (two bytes minimum).
        var count = r.ReadCount(minimumElementSize: 2);
        if (count == 0)
        {
            return Array.Empty<PipeProperty>();
        }

        var properties = new PipeProperty[count];
        for (var i = 0; i < count; i++)
        {
            var name = r.ReadString();
            var value = r.ReadNullableString();
            properties[i] = new PipeProperty(name, value);
        }

        return properties;
    }

    private static IReadOnlyList<PipeItem> ReadItems(WireBufferReader r)
    {
        // Each item is at least an item-type and evaluated-include string length prefix plus a metadata
        // count (three bytes minimum).
        var count = r.ReadCount(minimumElementSize: 3);
        if (count == 0)
        {
            return Array.Empty<PipeItem>();
        }

        var items = new PipeItem[count];
        for (var i = 0; i < count; i++)
        {
            var itemType = r.ReadString();
            var evaluatedInclude = r.ReadString();
            items[i] = new PipeItem(itemType, evaluatedInclude, ReadProperties(r));
        }

        return items;
    }

    private static T ApplyBase<T>(T e, WireBaseFields b)
        where T : PipeBuildEventArgs
    {
        e.Message = b.Message;
        e.HelpKeyword = b.HelpKeyword;
        e.SenderName = b.SenderName;
        e.Timestamp = b.Timestamp;
        e.ThreadId = b.ThreadId;
        e.BuildEventContext = b.HasContext
            ? new PipeBuildEventContext(b.SubmissionId, b.NodeId, b.EvaluationId, b.ProjectInstanceId, b.ProjectContextId, b.TargetId, b.TaskId)
            : null;
        return e;
    }
}
