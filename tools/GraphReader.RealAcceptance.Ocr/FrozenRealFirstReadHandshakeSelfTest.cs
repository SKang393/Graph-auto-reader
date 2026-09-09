// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenRealFirstReadHandshakeSelfTest
{
    internal static object Run()
    {
        string nonce = new('e', 64);
        string hash = new('a', 64);
        var checks = new List<string>();
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var reader = new StringReader($"G22_READ_ACK/1 {nonce}\n");
        var handshake = new FrozenRealFirstReadHandshake(reader, writer, "attempt-1", hash,
            TimeSpan.FromSeconds(5), nonce);
        handshake.BeforeFirstPayloadRead(CancellationToken.None);
        handshake.BeforeFirstPayloadRead(CancellationToken.None);
        Require(writer.ToString() == $"G22_FIRST_READ/1 attempt-1 {hash} {nonce}{Environment.NewLine}",
            "one_identity_only_request_for_all_project_callbacks");
        checks.Add("one_identity_only_request_for_all_project_callbacks");

        using var releaseAcknowledgement = new ManualResetEventSlim();
        using var firstReadStarted = new ManualResetEventSlim();
        using var secondCallStarted = new ManualResetEventSlim();
        using var secondCallCompleted = new ManualResetEventSlim();
        using var concurrentWriter = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var gatedReader = new GatedAcknowledgementReader(
            $"G22_READ_ACK/1 {nonce}\n", firstReadStarted, releaseAcknowledgement);
        var concurrent = new FrozenRealFirstReadHandshake(gatedReader, concurrentWriter,
            "attempt-concurrent", hash, TimeSpan.FromSeconds(5), nonce);
        Exception? firstFailure = null;
        Exception? secondFailure = null;
        Task firstCall = Task.Run(() =>
        {
            try
            {
                concurrent.BeforeFirstPayloadRead(CancellationToken.None);
            }
            catch (Exception exception)
            {
                firstFailure = exception;
            }
        });
        Require(firstReadStarted.Wait(TimeSpan.FromSeconds(2)), "concurrent_first_call_reached_ack_wait");
        Task secondCall = Task.Run(() =>
        {
            secondCallStarted.Set();
            try
            {
                concurrent.BeforeFirstPayloadRead(CancellationToken.None);
            }
            catch (Exception exception)
            {
                secondFailure = exception;
            }
            finally
            {
                secondCallCompleted.Set();
            }
        });
        Require(secondCallStarted.Wait(TimeSpan.FromSeconds(2)), "concurrent_second_call_started");
        Require(!secondCallCompleted.Wait(TimeSpan.FromMilliseconds(100)),
            "concurrent_second_call_waits_for_first_ack");
        releaseAcknowledgement.Set();
        Require(Task.WaitAll(new[] { firstCall, secondCall }, TimeSpan.FromSeconds(2)),
            "concurrent_callbacks_complete");
        Require(firstFailure is null && secondFailure is null,
            "concurrent_callbacks_share_successful_ack");
        Require(concurrentWriter.ToString() ==
                $"G22_FIRST_READ/1 attempt-concurrent {hash} {nonce}{Environment.NewLine}",
            "concurrent_callbacks_emit_one_request");
        checks.Add("concurrent_callbacks_emit_one_request_and_share_ack");

        using var cancellationOnAck = new CancellationTokenSource();
        using var cancellationReader = new CancellationOnAcknowledgementReader(
            $"G22_READ_ACK/1 {nonce}\n", cancellationOnAck);
        using var cancellationWriter = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var cancellationHandshake = new FrozenRealFirstReadHandshake(
            cancellationReader, cancellationWriter, "attempt-cancel-on-ack", hash,
            TimeSpan.FromSeconds(5), nonce);
        cancellationHandshake.BeforeFirstPayloadRead(cancellationOnAck.Token);
        Require(cancellationOnAck.IsCancellationRequested,
            "cancellation_arrived_with_durable_ack");
        Require(cancellationWriter.ToString() ==
                $"G22_FIRST_READ/1 attempt-cancel-on-ack {hash} {nonce}{Environment.NewLine}",
            "ack_commit_returns_for_immediate_payload_read");
        checks.Add("cancellation_on_ack_does_not_prevent_immediate_payload_read");

        foreach (string invalid in new[] { "", "G22_READ_ACK/1 wrong\n", new string('x', 1024),
                     $"G22_READ_ACK/1 {nonce}\r\n", $"G22_READ_ACK/1 {nonce}extra\n" })
        {
            using var invalidReader = new StringReader(invalid);
            using var invalidWriter = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var rejected = new FrozenRealFirstReadHandshake(invalidReader, invalidWriter,
                "attempt-1", hash, TimeSpan.FromSeconds(5), nonce);
            ExpectFailure<InvalidDataException>(() => rejected.BeforeFirstPayloadRead(CancellationToken.None));
            string firstRequest = invalidWriter.ToString();
            ExpectFailure<InvalidOperationException>(() => rejected.BeforeFirstPayloadRead(CancellationToken.None));
            Require(invalidWriter.ToString() == firstRequest, "failed_handshake_cannot_retry");
        }
        checks.Add("malformed_stale_truncated_and_oversized_ack_rejected");
        checks.Add("failed_handshake_cannot_retry");

        using var cancelledWriter = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var cancelled = new FrozenRealFirstReadHandshake(reader, cancelledWriter,
            "attempt-1", hash, TimeSpan.FromSeconds(5), nonce);
        ExpectFailure<OperationCanceledException>(() =>
            cancelled.BeforeFirstPayloadRead(new CancellationToken(canceled: true)));
        Require(cancelledWriter.ToString().Length == 0, "precancelled_callback_emits_nothing");
        checks.Add("precancelled_callback_emits_nothing");
        ExpectFailure<InvalidDataException>(() => _ = new FrozenRealFirstReadHandshake(reader, writer,
            "attempt injected", hash, TimeSpan.FromSeconds(5), nonce));
        ExpectFailure<InvalidDataException>(() => _ = new FrozenRealFirstReadHandshake(reader, writer,
            "attempt-1", hash.ToUpperInvariant(), TimeSpan.FromSeconds(5), nonce));
        checks.Add("invalid_or_injected_identity_rejected");
        string contentHash = new('c', 64);
        using var corpusReader = new StringReader(
            $"G22_READ_ACK/1 {nonce}\nG22_CORPUS_ACK/1 {nonce}\n");
        using var corpusWriter = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var corpus = new FrozenRealFirstReadHandshake(corpusReader, corpusWriter,
            "attempt-content", hash, TimeSpan.FromSeconds(5), nonce);
        ExpectFailure<InvalidDataException>(() =>
            corpus.ConfirmCorpusContent(contentHash, CancellationToken.None));
        Require(corpusWriter.ToString().Length == 0, "content_cannot_precede_accounted_read");
        corpus.BeforeFirstPayloadRead(CancellationToken.None);
        corpus.ConfirmCorpusContent(contentHash, CancellationToken.None);
        string contentFrames = corpusWriter.ToString();
        corpus.ConfirmCorpusContent(contentHash, CancellationToken.None);
        Require(corpusWriter.ToString() == contentFrames && contentFrames.EndsWith(
            $"G22_CORPUS/1 attempt-content {hash} {contentHash} {nonce}{Environment.NewLine}",
            StringComparison.Ordinal), "one_aggregate_content_confirmation");
        ExpectFailure<InvalidOperationException>(() =>
            corpus.ConfirmCorpusContent(new string('d', 64), CancellationToken.None));
        checks.Add("content_confirmation_requires_read_ack_and_is_bound_once");

        using var staleCorpusReader = new StringReader(
            $"G22_READ_ACK/1 {nonce}\nG22_READ_ACK/1 {nonce}\n");
        using var staleCorpusWriter = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var staleCorpus = new FrozenRealFirstReadHandshake(staleCorpusReader, staleCorpusWriter,
            "attempt-stale-content", hash, TimeSpan.FromSeconds(5), nonce);
        staleCorpus.BeforeFirstPayloadRead(CancellationToken.None);
        ExpectFailure<InvalidDataException>(() =>
            staleCorpus.ConfirmCorpusContent(contentHash, CancellationToken.None));
        ExpectFailure<InvalidOperationException>(() =>
            staleCorpus.ConfirmCorpusContent(contentHash, CancellationToken.None));
        checks.Add("read_ack_cannot_substitute_for_content_ack_or_be_retried");
        return new
        {
            status = "pass",
            checks,
            private_corpus_access = false,
            sealed_corpus_access = false,
            model_inference = false,
        };
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new InvalidOperationException(code);
        }
    }

    private static void ExpectFailure<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("EXPECTED_HANDSHAKE_FAILURE_MISSING");
    }

    private sealed class GatedAcknowledgementReader(
        string acknowledgement,
        ManualResetEventSlim readStarted,
        ManualResetEventSlim release) : TextReader
    {
        private int offset;

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.Set();
            release.Wait(cancellationToken);
            if (offset == acknowledgement.Length)
            {
                return ValueTask.FromResult(0);
            }
            buffer.Span[0] = acknowledgement[offset++];
            return ValueTask.FromResult(1);
        }
    }

    private sealed class CancellationOnAcknowledgementReader(
        string acknowledgement,
        CancellationTokenSource cancellation) : TextReader
    {
        private int offset;

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset == acknowledgement.Length)
            {
                return ValueTask.FromResult(0);
            }
            char value = acknowledgement[offset++];
            buffer.Span[0] = value;
            if (value == '\n')
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(1);
        }
    }
}
