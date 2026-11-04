using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// A <see cref="TextWriter"/> that collects text in a buffer rented from the shared array pool and
/// reports UTF-8 (so XmlWriter declares encoding="utf-8"). <see cref="ToString"/> is the only copy
/// of the text, where a StringWriter keeps a StringBuilder of the same size and then copies it.
/// </summary>
internal sealed class PooledUtf8TextWriter : TextWriter
{
    private char[] _buffer;
    private int _length;

    /// <param name="capacity">Expected length, in characters.</param>
    public PooledUtf8TextWriter(int capacity = 4096)
    {
        _buffer = ArrayPool<char>.Shared.Rent(Math.Max(capacity, 16));
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        Reserve(1);
        _buffer[_length++] = value;
    }

    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));

    public override void Write(ReadOnlySpan<char> buffer)
    {
        Reserve(buffer.Length);
        buffer.CopyTo(_buffer.AsSpan(_length));
        _length += buffer.Length;
    }

    public override void Write(string? value) => Write(value.AsSpan());

    public override string ToString() => new(_buffer, 0, _length);

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer.Length > 0)
        {
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = [];
            _length = 0;
        }

        base.Dispose(disposing);
    }

    private void Reserve(int count)
    {
        if (_length + count <= _buffer.Length)
            return;

        var larger = ArrayPool<char>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + count));
        _buffer.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = larger;
    }
}
