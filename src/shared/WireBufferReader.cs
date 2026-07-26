// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.IO;
using System.Text;

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// Parses wire-format primitives directly out of a reusable byte buffer window. The receiver reads
/// each record payload into one buffer and decodes fields by offset, so no per-record streams,
/// readers or intermediate copies are allocated. Overrunning the window throws
/// <see cref="EndOfStreamException"/> and a malformed varint throws <see cref="FormatException"/>,
/// both of which the reader treats as a record it cannot parse.
/// </summary>
internal sealed class WireBufferReader
{
    private byte[] _buffer = Array.Empty<byte>();
    private int _position;
    private int _end;

    /// <summary>Gets a value indicating whether any bytes remain before the current limit.</summary>
    public bool HasRemaining => _position < _end;

    /// <summary>Gets the number of unread bytes before the current limit.</summary>
    public int Remaining => _end - _position;

    /// <summary>Points this reader at the first <paramref name="length"/> bytes of <paramref name="buffer"/>.</summary>
    public void Reset(byte[] buffer, int length)
    {
        _buffer = buffer;
        _position = 0;
        _end = length;
    }

    /// <summary>
    /// Bounds reading to the next <paramref name="length"/> bytes and returns the previous limit,
    /// to be restored with <see cref="PopLimit"/>.
    /// </summary>
    /// <exception cref="EndOfStreamException">The declared length extends past the current limit.</exception>
    public int PushLimit(int length)
    {
        if (length < 0 || length > _end - _position)
        {
            throw new EndOfStreamException("The record payload ended before the declared length.");
        }

        var previousEnd = _end;
        _end = _position + length;
        return previousEnd;
    }

    /// <summary>Skips any unread bytes up to the current limit and restores the previous one.</summary>
    public void PopLimit(int previousEnd)
    {
        _position = _end;
        _end = previousEnd;
    }

    public byte ReadByte()
    {
        if (_position >= _end)
        {
            throw new EndOfStreamException("The record payload ended prematurely.");
        }

        return _buffer[_position++];
    }

    public bool ReadBoolean() => ReadByte() != 0;

    /// <summary>Reads a 7-bit variable-length encoded integer written by <see cref="WireIO.Write7Bit"/>.</summary>
    public int Read7Bit()
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = ReadByte();
            if (shift == 28 && (b & 0xF0) != 0)
            {
                // The 5th byte can only carry bits 28..31: a continuation bit or any bit beyond
                // bit 31 cannot come from Write7Bit and would silently produce a garbage value.
                break;
            }

            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new FormatException("Malformed 7-bit encoded integer on the pipe stream.");
    }

    /// <summary>
    /// Reads a 7-bit encoded element count and validates it against the bytes remaining. Each serialized
    /// element occupies at least <paramref name="minimumElementSize"/> bytes on the wire, so a count whose
    /// elements could not fit in <see cref="Remaining"/> — or a negative count — can only come from corrupt
    /// or hostile input and must be rejected before it is used to size an allocation. Bounding by the byte
    /// budget rather than the count alone keeps a small record from sizing a large array (the array itself
    /// costs several bytes per element, more than the one-byte wire minimum).
    /// </summary>
    /// <param name="minimumElementSize">The smallest number of bytes a single element can occupy on the wire.</param>
    /// <exception cref="EndOfStreamException">The count is negative or its elements exceed the remaining payload.</exception>
    public int ReadCount(int minimumElementSize = 1)
    {
        var count = Read7Bit();
        if (count < 0 || (long)count * minimumElementSize > Remaining)
        {
            throw new EndOfStreamException($"Element count {count} is negative or exceeds the {Remaining} bytes remaining in the record.");
        }

        return count;
    }

    /// <summary>Reads a <see cref="DateTime"/> written by <see cref="WireIO.WriteDateTime"/>.</summary>
    public DateTime ReadDateTime()
    {
        if (_end - _position < 9)
        {
            throw new EndOfStreamException("The record payload ended prematurely.");
        }

        var ticks = BitConverter.ToInt64(_buffer, _position);
        var kind = (DateTimeKind)_buffer[_position + 8];
        _position += 9;
        return new DateTime(ticks, kind);
    }

    /// <summary>Reads a length-prefixed UTF-8 string written by <see cref="BinaryWriter.Write(string)"/>.</summary>
    public string ReadString()
    {
        var byteLength = Read7Bit();
        if (byteLength < 0 || byteLength > _end - _position)
        {
            throw new EndOfStreamException("The record payload ended before the declared string length.");
        }

        if (byteLength == 0)
        {
            return string.Empty;
        }

        var value = Encoding.UTF8.GetString(_buffer, _position, byteLength);
        _position += byteLength;
        return value;
    }

    /// <summary>Reads a string written by <see cref="WireIO.WriteNullable(BinaryWriter,string)"/>.</summary>
    public string? ReadNullableString() => ReadBoolean() ? ReadString() : null;
}
