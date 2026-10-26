namespace SubReel.Launcher.Infrastructure.Modpacks;

internal sealed class LimitedWriteStream(Stream inner, long maxBytes, Action<int>? onWrite = null) : Stream
{
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacity(count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(buffer.Length);
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureCapacity(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        // The caller owns the wrapped file stream.
        base.Dispose(disposing);
    }

    private void EnsureCapacity(int count)
    {
        if (count < 0 || count > maxBytes - _written)
            throw new InvalidDataException("Скачиваемый файл превышает допустимый объём сборки.");
        onWrite?.Invoke(count);
        _written += count;
    }
}
