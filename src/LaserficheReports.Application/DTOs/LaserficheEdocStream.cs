namespace LaserficheReports.Application.DTOs;

/// <summary>
/// An electronic document response opened from Laserfiche. It carries only
/// browser-safe response metadata; authentication headers are never included.
/// </summary>
public sealed class LaserficheEdocStream : IDisposable
{
    private readonly IDisposable _owner;
    private bool _disposed;

    public LaserficheEdocStream(
        Stream content,
        string contentType,
        string? contentDisposition,
        string? fileName,
        string? extension,
        long? contentLength,
        IDisposable owner)
    {
        var source = content ?? throw new ArgumentNullException(nameof(content));
        Content = new OwnerClosingStream(source, owner);
        ContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType;
        ContentDisposition = contentDisposition;
        FileName = fileName;
        Extension = extension;
        ContentLength = contentLength;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>Readable upstream content stream.</summary>
    public Stream Content { get; }

    /// <summary>Original media type, or application/octet-stream when absent.</summary>
    public string ContentType { get; }

    /// <summary>Original Content-Disposition header when supplied by Laserfiche.</summary>
    public string? ContentDisposition { get; }

    /// <summary>Original filename from Content-Disposition when available.</summary>
    public string? FileName { get; }

    /// <summary>File extension including the leading dot when available.</summary>
    public string? Extension { get; }

    /// <summary>Original Content-Length when supplied by Laserfiche.</summary>
    public long? ContentLength { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Content.Dispose();
        _owner.Dispose();
    }

    private sealed class OwnerClosingStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable _owner;

        public OwnerClosingStream(Stream inner, IDisposable owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _owner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _owner.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}