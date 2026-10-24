using System.Diagnostics;
using System.Text;

namespace Library.Api;

public record PacketWindowEvidence(long Bytes, long PeakWorkingSetBytes, string Pipeline, string ConsumerOutput);

// A bounded binary pipe, not an intermediate media file. The copy-only producer
// absorbs sparse-stream read-ahead without decoding frames. Only one encoder runs.
public static class SamplePipeline
{
    public static async Task<PacketWindowEvidence> Run(string executable, string[] producerArguments, string[] consumerArguments,
        CancellationToken ct, long byteLimit = 256L * 1024 * 1024, long memoryLimit = 1024L * 1024 * 1024)
    {
        static Process Create(string executable, string[] arguments, bool input)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = input, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            return new Process { StartInfo = start };
        }
        using var producer = Create(executable, producerArguments, false);
        using var consumer = Create(executable, consumerArguments, true);
        var started = new List<Process>();
        void Start(Process process)
        {
            try { if (!process.Start()) throw new IOException("Could not start sample pipeline process."); }
            catch (System.ComponentModel.Win32Exception error) { throw new IOException("Configured sample pipeline executable could not be started.", error); }
            started.Add(process);
        }
        void KillBoth() { foreach (var process in started) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } } }
        async Task<T> Guard<T>(Func<Task<T>> action) { try { return await action(); } catch { KillBoth(); throw; } }
        async Task<string> ReadText(StreamReader reader) => await Guard(async () => {
            var text = new StringBuilder(); var buffer = new char[4096]; var bytes = 0; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
                if (bytes > 64 * 1024) throw new IOException("Sample pipeline diagnostic output exceeded its bound.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        });
        try
        {
            // Consumer is started first so producer output is always drained with backpressure.
            Start(consumer); Start(producer);
            using var cancellation = ct.Register(KillBoth);
            var producerError = ReadText(producer.StandardError); var consumerError = ReadText(consumer.StandardError);
            var consumerOutput = ReadText(consumer.StandardOutput);
            var pump = Guard(async () => {
                long total = 0; var buffer = new byte[64 * 1024]; int count;
                try
                {
                    while ((count = await producer.StandardOutput.BaseStream.ReadAsync(buffer, ct)) > 0)
                    {
                        total += count;
                        if (total > byteLimit) throw new IOException("Sample packet window exceeded its 256 MiB byte budget.");
                        await consumer.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    await consumer.StandardInput.BaseStream.FlushAsync(ct);
                    return total;
                }
                finally { consumer.StandardInput.Close(); }
            });
            var memory = Guard(async () => {
                long peak = 0;
                while (started.Any(p => !p.HasExited))
                {
                    long total = 0;
                    foreach (var process in started)
                    {
                        try { process.Refresh(); if (!process.HasExited) total += process.WorkingSet64; }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    }
                    peak = Math.Max(peak, total);
                    if (total > memoryLimit) throw new IOException("Sample pipeline exceeded its combined 1 GiB resident-memory budget; publication is refused.");
                    await Task.Delay(100, ct);
                }
                return peak;
            });
            await Task.WhenAll(pump, memory, producerError, consumerError, consumerOutput, producer.WaitForExitAsync(ct), consumer.WaitForExitAsync(ct));
            ct.ThrowIfCancellationRequested();
            static string Detail(string value) => new(value.Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(1024).ToArray());
            if (producer.ExitCode != 0 || consumer.ExitCode != 0) throw new IOException($"Sample pipeline failed (copy {producer.ExitCode}, encoder {consumer.ExitCode}). {Detail(producerError.Result)} {Detail(consumerError.Result)}");
            if (!string.IsNullOrWhiteSpace(producerError.Result) || !string.IsNullOrWhiteSpace(consumerError.Result)) throw new IOException($"Sample pipeline reported errors; validation refused. {Detail(producerError.Result)} {Detail(consumerError.Result)}");
            if (pump.Result == 0) throw new IOException("Empty sample packet window.");
            return new(pump.Result, memory.Result, "copy-window-pipe-v1", consumerOutput.Result);
        }
        catch (Exception) { KillBoth(); ct.ThrowIfCancellationRequested(); throw; }
        finally
        {
            foreach (var process in started) if (!process.HasExited) { KillBoth(); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
