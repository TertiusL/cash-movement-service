namespace CashMovements.StatementClient;

internal static class StatementCsvExporter
{
    internal static async Task<long> ExportAsync(IAsyncStreamReader<StatementFrame> frames, string outputPath,
        TextWriter progress, CancellationToken cancellationToken)
    {
        string destination = Path.GetFullPath(outputPath);
        if (File.Exists(destination))
        {
            throw new IOException($"The destination already exists: {destination}");
        }

        string partialPath = $"{destination}.{Guid.NewGuid():N}.partial";
        bool partialCreated = false;
        long movementCount;
        try
        {
            await using (FileStream file = new(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true))
            {
                partialCreated = true;
                await using StreamWriter writer = new(file, new UTF8Encoding(false, true), leaveOpen: true);
                writer.NewLine = "\r\n";
                progress.WriteLine($"Writing partial file: {partialPath}");
                movementCount = await WriteStatementAsync(frames, writer, progress, cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, destination, overwrite: false);
        }
        catch
        {
            if (partialCreated)
            {
                progress.WriteLine($"Partial file: {partialPath}");
            }
            throw;
        }

        progress.WriteLine($"Completed {movementCount} movements: {destination}");
        return movementCount;
    }

    private static async Task<long> WriteStatementAsync(IAsyncStreamReader<StatementFrame> frames, StreamWriter writer,
        TextWriter progress, CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        if (!await frames.MoveNext(cancellationToken) || frames.Current.Header is not StatementHeader header)
        {
            throw new InvalidDataException("The statement did not begin with a header.");
        }

        await writer.WriteLineAsync(StatementCsv.Columns.AsMemory(), cancellationToken);
        string closingBalance = header.OpeningBalance;
        string opening = StatementCsv.SummaryRecord("opening", header, closingBalance);
        await writer.WriteLineAsync(opening.AsMemory(), cancellationToken);

        long movementCount = 0;
        TimeSpan lastReportedAt = TimeSpan.Zero;
        while (await frames.MoveNext(cancellationToken))
        {
            if (frames.Current.Row is not StatementRow row)
            {
                throw new InvalidDataException("Expected a movement row after the statement header.");
            }

            string record = StatementCsv.MovementRecord(row);
            await writer.WriteLineAsync(record.AsMemory(), cancellationToken);
            closingBalance = row.RunningBalance;
            movementCount++;
            TimeSpan elapsedTime = elapsed.Elapsed;
            if (movementCount % 1000 == 0 || elapsedTime - lastReportedAt >= TimeSpan.FromSeconds(1))
            {
                progress.WriteLine($"Processed {movementCount} movements in {elapsedTime:c}.");
                lastReportedAt = elapsedTime;
            }
        }

        // The gRPC reader returns false only after the terminal status is OK.
        string closing = StatementCsv.SummaryRecord("closing", header, closingBalance);
        await writer.WriteLineAsync(closing.AsMemory(), cancellationToken);
        return movementCount;
    }
}
