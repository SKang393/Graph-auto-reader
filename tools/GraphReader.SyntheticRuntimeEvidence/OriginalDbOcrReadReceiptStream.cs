// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>
/// Reports the first positive payload read before returning its bytes to the caller.
/// A failed receipt is never retried. The parent then retains an uncertain read.
/// </summary>
internal sealed class OriginalDbOcrReadReceiptStream(Stream inner, Action positiveRead) : Stream
{
    private bool receiptAttempted;
    private bool receiptFailed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (receiptFailed) throw new InvalidDataException("OCR_READ_RECEIPT_FAILED");
        int count = inner.Read(buffer);
        if (count > 0 && !receiptAttempted)
        {
            receiptAttempted = true;
            try { positiveRead(); }
            catch
            {
                receiptFailed = true;
                throw;
            }
        }
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        // The caller owns and locks the original stream for the whole evaluation.
        base.Dispose(disposing);
    }
}
