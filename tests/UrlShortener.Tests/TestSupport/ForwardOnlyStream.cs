namespace UrlShortener.Tests.TestSupport;

/// <summary>Non-seekable stream: HttpClient sends it with chunked transfer encoding (no Content-Length).</summary>
public sealed class ForwardOnlyStream : Stream
{
    private readonly MemoryStream _inner;

    public ForwardOnlyStream(byte[] data) => _inner = new MemoryStream(data);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
