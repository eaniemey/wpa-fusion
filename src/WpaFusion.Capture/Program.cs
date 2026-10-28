using WpaFusion.Capture;

try
{
    var options = CaptureOptions.Parse(args);
    if (options.Help)
    {
        Console.WriteLine(CaptureOptions.Usage);
        return 0;
    }

    return new CaptureSession(options).Run();
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
