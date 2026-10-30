using WpaFusion.Capture;

try
{
    var options = CaptureOptions.Parse(args);
    if (options.Help)
    {
        Console.WriteLine(CaptureOptions.Usage);
        return 0;
    }

    string runDirectory;
    if (options.RunCount > 1)
    {
        var batch = new CaptureBatch(options);
        batch.Run();
        runDirectory = batch.RunDirectory!;
    }
    else
    {
        var session = new CaptureSession(options);
        session.Run();
        runDirectory = session.RunDirectory!;
    }

    Console.WriteLine($"Raw capture: {runDirectory}");
    if (!options.SkipAttribute && (options.Sep || options.Emon))
    {
        await CapturePostProcessor.RunAsync(runDirectory);
    }
    else
    {
        Console.WriteLine(options.SkipAttribute ? "Automatic attribution skipped (--skip-attribute)." : "No PMU input was collected; attribution is not applicable.");
        Console.WriteLine($"Output: {runDirectory}");
    }

    return 0;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"wpafusion-capture: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"wpafusion-capture failed: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    // Unexpected failure: report it with a failing exit code instead of crashing with a runtime dump.
    Console.Error.WriteLine($"wpafusion-capture failed unexpectedly: {ex}");
    return 1;
}
