using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Utils;

internal sealed class WuwaLogDecodeStream(Stream source) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => source.Length;

    public override long Position
    {
        get => source.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        Decode(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = source.Read(buffer);
        Decode(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Decode(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return Impl();

        async Task<int> Impl()
        {
            int read = await source.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false);
            Decode(buffer.AsSpan(offset, read));
            return read;
        }
    }

    private static void Decode(Span<byte> buffer)
    {
        foreach (ref byte value in buffer)
        {
            value = (byte)((value & 1) != 0
                ? value ^ 0xA5
                : value ^ 0xEF);
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
