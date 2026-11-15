using System.Security.Cryptography;

namespace SimilarVideoFinder.Core;

public static class HashMatcher
{
    public static async Task<string> Calculate(FileRecord file, bool full, Action<int>? bytes, CancellationToken cancellation)
    {
        if (!file.IsCurrent()) throw new IOException("File changed or is unavailable.");
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | (full ? FileOptions.SequentialScan : FileOptions.RandomAccess));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[full ? 1024 * 1024 : 64 * 1024];
        if (full)
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
            { hash.AppendData(buffer, 0, count); bytes?.Invoke(count); }
        }
        else
        {
            foreach (long offset in new[] { 0L, Math.Max(0, (file.Size - buffer.Length) / 2), Math.Max(0, file.Size - buffer.Length) }.Distinct())
            {
                stream.Position = offset;
                int remaining = (int)Math.Min(buffer.Length, file.Size - offset), filled = 0;
                while (filled < remaining)
                {
                    int count = await stream.ReadAsync(buffer.AsMemory(filled, remaining - filled), cancellation);
                    if (count == 0) throw new IOException("Unexpected end of file.");
                    filled += count; bytes?.Invoke(count);
                }
                hash.AppendData(buffer, 0, filled);
            }
        }
        if (!file.IsCurrent()) throw new IOException("File changed during hashing.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
