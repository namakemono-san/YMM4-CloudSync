using System.IO;
using System.Net;
using System.Net.Http;

namespace YMM4CloudSync.Core.Commons;

public sealed class ProgressStreamContent(
    Stream stream,
    long total,
    IProgress<double>? progress,
    int buffer = 64 * 1024)
    : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context)
    {
        var buf = new byte[buffer];
        long sent = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buf);
            if (read == 0) break;

            await target.WriteAsync(buf.AsMemory(0, read));
            sent += read;

            if (total > 0)
                progress?.Report((double)sent / total * 100);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = total;
        return true;
    }
}