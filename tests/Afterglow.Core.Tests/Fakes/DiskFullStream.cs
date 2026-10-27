namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// A write stream on a drive that has filled up (or gone away): every write
/// and flush throws, and whether it was closed is recorded.
/// </summary>
internal sealed class DiskFullStream : Stream
{
    public bool Closed { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !Closed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new IOException("There is not enough space on the disk.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new IOException("There is not enough space on the disk.");

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Closed = true;
        base.Dispose(disposing);
    }
}
