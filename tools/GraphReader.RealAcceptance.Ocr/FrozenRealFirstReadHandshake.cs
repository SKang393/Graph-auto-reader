// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;

namespace GraphReader.RealAcceptance.Ocr;

/// <summary>
/// Identity-only synchronous boundary for EngaugeDigWholeWorkflowTruthAdapter.Read.
/// The parent must durably account for the read before acknowledging this nonce.
/// Candidate admission and the live parent ledger lease remain runner concerns.
/// </summary>
internal sealed class FrozenRealFirstReadHandshake
{
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly string request;
    private readonly string expectedAcknowledgement;
    private readonly string corpusRequestPrefix;
    private readonly string corpusAcknowledgement;
    private readonly string nonce;
    private readonly TimeSpan timeout;
    private readonly object sync = new();
    private bool requested;
    private bool acknowledged;
    private bool corpusRequested;
    private string? confirmedCorpus;

    internal FrozenRealFirstReadHandshake(
        TextReader input,
        TextWriter output,
        string attemptId,
        string candidateSha256,
        TimeSpan timeout)
        : this(input, output, attemptId, candidateSha256, timeout,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))
    {
    }

    internal FrozenRealFirstReadHandshake(
        TextReader input,
        TextWriter output,
        string attemptId,
        string candidateSha256,
        TimeSpan timeout,
        string nonce)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (string.IsNullOrEmpty(attemptId) || attemptId.Length > 256 ||
            attemptId.Any(static value => value is < '!' or > '~') ||
            !IsLowerHash(candidateSha256) || !IsLowerHash(nonce) ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new InvalidDataException("REAL_READ_HANDSHAKE_IDENTITY_INVALID");
        }
        this.input = input;
        this.output = output;
        this.timeout = timeout;
        this.nonce = nonce;
        request = $"G22_FIRST_READ/1 {attemptId} {candidateSha256} {nonce}";
        expectedAcknowledgement = $"G22_READ_ACK/1 {nonce}";
        corpusRequestPrefix = $"G22_CORPUS/1 {attemptId} {candidateSha256}";
        corpusAcknowledgement = $"G22_CORPUS_ACK/1 {nonce}";
    }

    internal void BeforeFirstPayloadRead(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            BeforeFirstPayloadReadCore(cancellationToken);
        }
    }

    private void BeforeFirstPayloadReadCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (acknowledged)
        {
            return;
        }
        if (requested)
        {
            throw new InvalidOperationException("REAL_READ_HANDSHAKE_ALREADY_FAILED");
        }
        requested = true;
        Exchange(request, expectedAcknowledgement, cancellationToken);
        acknowledged = true;
    }

    internal void ConfirmCorpusContent(string contentSha256, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!acknowledged || !IsLowerHash(contentSha256))
            {
                throw new InvalidDataException("REAL_CORPUS_HANDSHAKE_NOT_ADMITTED");
            }
            if (confirmedCorpus == contentSha256)
            {
                return;
            }
            if (corpusRequested)
            {
                throw new InvalidOperationException("REAL_CORPUS_HANDSHAKE_ALREADY_REQUESTED");
            }
            corpusRequested = true;
            Exchange($"{corpusRequestPrefix} {contentSha256} {nonce}",
                corpusAcknowledgement, cancellationToken);
            confirmedCorpus = contentSha256;
        }
    }

    private void Exchange(string outgoing, string expected, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        output.WriteLine(outgoing);
        output.Flush();
        // Read a bounded frame. ReadLine would allocate without a bound if the
        // parent accidentally forwarded model output instead of the protocol.
        char[] character = new char[1];
        var response = new System.Text.StringBuilder(expected.Length);
        while (true)
        {
            int count = input.ReadAsync(character.AsMemory(), deadline.Token)
                .AsTask().GetAwaiter().GetResult();
            if (count == 0 || response.Length > expected.Length)
            {
                throw new InvalidDataException("REAL_READ_HANDSHAKE_ACK_INVALID");
            }
            if (character[0] == '\n')
            {
                break;
            }
            response.Append(character[0]);
        }
        if (!string.Equals(response.ToString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("REAL_READ_HANDSHAKE_ACK_INVALID");
        }
    }

    private static bool IsLowerHash(string value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
