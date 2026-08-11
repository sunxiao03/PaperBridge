using System.Diagnostics;
using System.Text.Json;
using PaperBridge.Application.Abstractions;
using PaperBridge.Infrastructure.Pdf;

if (args.Length > 0 && string.Equals(args[0], "--acceptance", StringComparison.OrdinalIgnoreCase))
{
    return await RunAcceptanceAsync(args);
}

if (args.Length > 0 && string.Equals(args[0], "--duration", StringComparison.OrdinalIgnoreCase))
{
    return await RunDurationAsync(args);
}

return await RunResourceLoopAsync(args);

static async Task<int> RunResourceLoopAsync(string[] arguments)
{
    var filePath = arguments.Length > 0
        ? Path.GetFullPath(arguments[0])
        : Path.GetFullPath(Path.Combine(
            Environment.CurrentDirectory,
            "output",
            "pdf",
            "pdfium-text-layer-sample.pdf"));
    var iterations = arguments.Length > 1 && int.TryParse(arguments[1], out var requestedIterations)
        ? requestedIterations
        : 100;

    if (!ValidateInput(filePath, iterations, 10_000))
    {
        return 2;
    }

    await RunSingleDocumentIterationAsync(filePath, 0);
    CollectFully();
    var before = ProcessSnapshot.Capture();
    var timer = Stopwatch.StartNew();
    var sampleInterval = Math.Min(50, iterations);
    var samples = new List<ResourceSample>();

    for (var iteration = 0; iteration < iterations; iteration++)
    {
        await RunSingleDocumentIterationAsync(filePath, iteration);

        var completed = iteration + 1;
        if (completed % sampleInterval == 0 || completed == iterations)
        {
            CollectFully();
            samples.Add(new ResourceSample(completed, ProcessSnapshot.Capture()));
        }
    }

    timer.Stop();
    CollectFully();
    var after = ProcessSnapshot.Capture();
    WriteJson(new
    {
        Scenario = "resource-loop",
        File = filePath,
        Iterations = iterations,
        ElapsedMilliseconds = timer.ElapsedMilliseconds,
        Before = before,
        After = after,
        Samples = samples,
        Delta = CreateDelta(before, after)
    });
    return 0;
}

static async Task<int> RunAcceptanceAsync(string[] arguments)
{
    if (arguments.Length < 2)
    {
        Console.Error.WriteLine("Usage: --acceptance <pdf> [rounds] [tabs]");
        return 2;
    }

    var filePath = Path.GetFullPath(arguments[1]);
    var rounds = arguments.Length > 2 && int.TryParse(arguments[2], out var requestedRounds)
        ? requestedRounds
        : 12;
    var tabCount = arguments.Length > 3 && int.TryParse(arguments[3], out var requestedTabs)
        ? requestedTabs
        : 5;
    if (!ValidateInput(filePath, rounds, 100) || tabCount is < 2 or > 20)
    {
        Console.Error.WriteLine("Acceptance tabs must be between 2 and 20.");
        return 2;
    }

    int pageCount;
    long coldFirstPageMilliseconds;
    var coldTimer = Stopwatch.StartNew();
    await using (var coldDocument = PdfiumDocument.Open(filePath))
    {
        pageCount = coldDocument.PageCount;
        _ = await coldDocument.ExtractPageTextAsync(0);
        _ = await coldDocument.RenderPageAsync(0, new PdfRenderRequest(scale: 0.75));
    }

    coldTimer.Stop();
    coldFirstPageMilliseconds = coldTimer.ElapsedMilliseconds;
    if (pageCount < 100)
    {
        Console.Error.WriteLine($"Acceptance input must contain at least 100 pages; actual: {pageCount}.");
        return 2;
    }

    for (var warmupRound = 0; warmupRound < 4; warmupRound++)
    {
        await RunMultiTabRoundAsync(filePath, pageCount, tabCount, warmupRound);
    }

    CollectFully();
    var before = ProcessSnapshot.Capture();
    var totalTimer = Stopwatch.StartNew();
    var samples = new List<AcceptanceSample>();

    for (var round = 1; round <= rounds; round++)
    {
        var roundTimer = Stopwatch.StartNew();
        await RunMultiTabRoundAsync(filePath, pageCount, tabCount, round);
        roundTimer.Stop();
        CollectFully();
        samples.Add(new AcceptanceSample(round, roundTimer.ElapsedMilliseconds, ProcessSnapshot.Capture()));
    }

    totalTimer.Stop();
    CollectFully();
    var after = ProcessSnapshot.Capture();
    var delta = CreateDelta(before, after);
    var sustainedPrivateGrowth = HasSustainedPrivateGrowth(samples);
    var passed = Math.Abs(delta.HandleCount) <= 2 &&
                 delta.PrivateMemoryBytes <= 64L * 1024 * 1024 &&
                 !sustainedPrivateGrowth;

    WriteJson(new
    {
        Scenario = "stage-1-long-document-multi-tab",
        File = filePath,
        PageCount = pageCount,
        Rounds = rounds,
        SimultaneousTabs = tabCount,
        PagesRenderedPerTabPerRound = 3,
        ColdOpenExtractAndFirstRenderMilliseconds = coldFirstPageMilliseconds,
        ElapsedMilliseconds = totalTimer.ElapsedMilliseconds,
        Before = before,
        After = after,
        Samples = samples,
        Delta = delta,
        Evaluation = new
        {
            Passed = passed,
            SustainedPrivateGrowth = sustainedPrivateGrowth,
            MaximumHandleDelta = 2,
            MaximumPrivateMemoryDeltaBytes = 64L * 1024 * 1024
        }
    });
    return passed ? 0 : 1;
}

static async Task<int> RunDurationAsync(string[] arguments)
{
    if (arguments.Length < 3)
    {
        Console.Error.WriteLine("Usage: --duration <pdf> <minutes> [tabs]");
        return 2;
    }

    var filePath = Path.GetFullPath(arguments[1]);
    if (!double.TryParse(arguments[2], System.Globalization.CultureInfo.InvariantCulture, out var minutes) ||
        minutes is < 0.05 or > 480)
    {
        Console.Error.WriteLine("Duration must be between 0.05 and 480 minutes.");
        return 2;
    }

    var tabCount = arguments.Length > 3 && int.TryParse(arguments[3], out var requestedTabs)
        ? requestedTabs
        : 5;
    if (!File.Exists(filePath) || tabCount is < 2 or > 20)
    {
        Console.Error.WriteLine("The PDF must exist and tabs must be between 2 and 20.");
        return 2;
    }

    int pageCount;
    await using (var document = PdfiumDocument.Open(filePath))
    {
        pageCount = document.PageCount;
    }

    for (var warmup = 0; warmup < 20; warmup++)
    {
        await RunMultiTabRoundAsync(filePath, pageCount, tabCount, warmup);
    }

    CollectFully();
    var before = ProcessSnapshot.Capture();
    var timer = Stopwatch.StartNew();
    var requestedDuration = TimeSpan.FromMinutes(minutes);
    var samples = new List<AcceptanceSample>();
    var round = 0;
    while (timer.Elapsed < requestedDuration)
    {
        round++;
        var roundTimer = Stopwatch.StartNew();
        await RunMultiTabRoundAsync(filePath, pageCount, tabCount, round);
        roundTimer.Stop();
        if (round == 1 || round % 25 == 0 || timer.Elapsed >= requestedDuration)
        {
            CollectFully();
            samples.Add(new AcceptanceSample(round, roundTimer.ElapsedMilliseconds, ProcessSnapshot.Capture()));
        }
    }

    timer.Stop();
    CollectFully();
    var after = ProcessSnapshot.Capture();
    var delta = CreateDelta(before, after);
    var sustainedPrivateGrowth = HasSustainedPrivateGrowth(samples);
    var sustainedHandleGrowth = HasSustainedHandleGrowth(samples);
    var passed = Math.Abs(delta.HandleCount) <= 32 &&
                 delta.PrivateMemoryBytes <= 64L * 1024 * 1024 &&
                 !sustainedPrivateGrowth &&
                 !sustainedHandleGrowth;
    WriteJson(new
    {
        Scenario = "duration-multi-tab",
        File = filePath,
        RequestedMinutes = minutes,
        ActualElapsedMilliseconds = timer.ElapsedMilliseconds,
        Rounds = round,
        SimultaneousTabs = tabCount,
        Before = before,
        After = after,
        Samples = samples,
        Delta = delta,
        Evaluation = new
        {
            Passed = passed,
            SustainedPrivateGrowth = sustainedPrivateGrowth,
            SustainedHandleGrowth = sustainedHandleGrowth,
            MaximumHandleDelta = 32,
            MaximumPrivateMemoryDeltaBytes = 64L * 1024 * 1024
        }
    });
    return passed ? 0 : 1;
}

static bool ValidateInput(string filePath, int iterations, int maximumIterations)
{
    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"PDF not found: {filePath}");
        return false;
    }

    if (iterations >= 1 && iterations <= maximumIterations)
    {
        return true;
    }

    Console.Error.WriteLine($"Iterations must be between 1 and {maximumIterations}.");
    return false;
}

static async Task RunSingleDocumentIterationAsync(string filePath, int iteration)
{
    await using var document = PdfiumDocument.Open(filePath);
    var pageIndex = iteration % document.PageCount;
    _ = await document.ExtractPageTextAsync(pageIndex);
    _ = await document.RenderPageAsync(pageIndex, new PdfRenderRequest(scale: 1.35));
}

static async Task RunMultiTabRoundAsync(string filePath, int pageCount, int tabCount, int round)
{
    var documents = Enumerable.Range(0, tabCount)
        .Select(_ => PdfiumDocument.Open(filePath))
        .ToArray();
    try
    {
        await Task.WhenAll(documents.Select((document, tabIndex) => Task.Run(async () =>
        {
            for (var pageOffset = 0; pageOffset < 3; pageOffset++)
            {
                var pageIndex = (round * 37 + tabIndex * 83 + pageOffset * 11) % pageCount;
                _ = await document.ExtractPageTextAsync(pageIndex);
                _ = await document.RenderPageAsync(pageIndex, new PdfRenderRequest(scale: 0.75));
            }
        })));
    }
    finally
    {
        foreach (var document in documents)
        {
            await document.DisposeAsync();
        }
    }
}

static ResourceDelta CreateDelta(ProcessSnapshot before, ProcessSnapshot after) =>
    new(
        after.PrivateMemoryBytes - before.PrivateMemoryBytes,
        after.WorkingSetBytes - before.WorkingSetBytes,
        after.HandleCount - before.HandleCount);

static bool HasSustainedPrivateGrowth(IReadOnlyList<AcceptanceSample> samples)
{
    if (samples.Count < 5)
    {
        return false;
    }

    var tail = samples.TakeLast(5).Select(sample => sample.Snapshot.PrivateMemoryBytes).ToArray();
    const long noiseToleranceBytes = 256 * 1024;
    return tail[^1] - tail[0] > 4L * 1024 * 1024 &&
           tail.Zip(tail.Skip(1), (left, right) => right - left)
               .All(delta => delta > noiseToleranceBytes);
}

static bool HasSustainedHandleGrowth(IReadOnlyList<AcceptanceSample> samples)
{
    if (samples.Count < 5)
    {
        return false;
    }

    var tail = samples.TakeLast(5).Select(sample => sample.Snapshot.HandleCount).ToArray();
    return tail[^1] - tail[0] > 2 &&
           tail.Zip(tail.Skip(1), (left, right) => right - left).All(delta => delta >= 0);
}

static void WriteJson(object value) =>
    Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

static void CollectFully()
{
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
}

internal sealed record ProcessSnapshot(long PrivateMemoryBytes, long WorkingSetBytes, int HandleCount)
{
    public static ProcessSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessSnapshot(process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount);
    }
}

internal sealed record ResourceSample(int CompletedIterations, ProcessSnapshot Snapshot);

internal sealed record AcceptanceSample(int Round, long ElapsedMilliseconds, ProcessSnapshot Snapshot);

internal sealed record ResourceDelta(long PrivateMemoryBytes, long WorkingSetBytes, int HandleCount);
