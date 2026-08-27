using System.IO;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[256];

        for (var i = 0u; i < table.Length; i++)
        {
            var value = i;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(Stream stream, byte[] buffer, CancellationToken cancellationToken = default)
    {
        var crc = 0xFFFFFFFFu;

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < read; i++)
            {
                crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    public static uint ComputeFile(string path, byte[] buffer, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);

        return Compute(stream, buffer, cancellationToken);
    }
}
