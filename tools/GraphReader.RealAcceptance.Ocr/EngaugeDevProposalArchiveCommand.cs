// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevProposalArchiveCommand
{
    internal static async Task<object> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length != 8 || args[7] != "--explicit-opt-in")
            throw new ArgumentException("ENGAUGE_PROPOSAL_ARCHIVE_ARGUMENTS_INVALID");
        string root = Path.GetFullPath(Directory.GetCurrentDirectory());
        string keyPath = Path.GetFullPath(args[6]);
        string relative = Path.GetRelativePath(Path.Combine(root, "artifacts"), keyPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException("ENGAUGE_PROPOSAL_ARCHIVE_KEY_OUTSIDE_PROJECT");
        for (string? current = keyPath; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ENGAUGE_PROPOSAL_ARCHIVE_KEY_REPARSE_PATH");
        }

        byte[] key = new byte[32];
        try
        {
            using var source = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            source.ReadExactly(key);
            if (source.ReadByte() != -1) throw new InvalidDataException("ENGAUGE_PROPOSAL_ARCHIVE_KEY_INVALID");
            using var archive = new EngaugeDevProposalArchive(root, args[5], key);
            var request = new EngaugeDevProposalPreparationRequest(root, args[1], args[2], args[3], args[4]);
            EngaugeDevProposalPreparationResult result = await EngaugeDevProposalPreparation.RunAsync(
                request, archive.ConsumeAsync, token).ConfigureAwait(false);
            EngaugeDevProposalArchiveReceipt receipt = archive.Complete(result, token);
            return new { status = "pass", receipt, preparation = result };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
