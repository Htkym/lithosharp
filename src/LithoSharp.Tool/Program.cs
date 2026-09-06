namespace LithoSharp.Tool;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            return args.FirstOrDefault() == "__host"
                ? await FactoryHost.RunAsync(args[1..], shutdown.Token)
                : await Cli.RunAsync(args, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 130;
        }
        catch (CliUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Run 'lithosharp --help' for usage.");
            return 2;
        }
        catch (ProjectCompilationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

internal sealed class CliUsageException(string message) : Exception(message);
