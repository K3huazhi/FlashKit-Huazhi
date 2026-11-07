using System.Diagnostics;
using FlashKit.Core.Models;
using FlashKit.Services.Logging;
using FlashKit.Services.Packages;

var logging = new LoggingService();
logging.EntryAdded += (_, e) =>
{
    if (e.Kind is LogKind.Warning or LogKind.Error)
    {
        Console.WriteLine($"[{e.Kind}] {e.Message}");
    }
};

if (args.Length == 0)
{
    Console.WriteLine("usage: FlashKit.PackageProbe extract <zipOrPayload> | pack <out.zip> <partition=image>...");
    return 2;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "extract":
            if (args.Length < 2)
            {
                return 2;
            }

            await ExtractAsync(args[1]);
            break;
        case "pack":
            if (args.Length < 4)
            {
                return 2;
            }

            await PackAsync(args[1], args.Skip(2).ToArray());
            break;
        default:
            return 2;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

async Task ExtractAsync(string packagePath)
{
    var extractor = new PackageExtractor(logging);
    var progress = new Progress<OperationProgress>(p =>
    {
        if (p.Percentage >= 0)
        {
            Console.WriteLine($"PROGRESS {p.Percentage:0.0}% {p.Status}");
        }
    });
    var sw = Stopwatch.StartNew();
    var result = await extractor.ExtractAsync(packagePath, progress, line => Console.WriteLine($"LOG {line}"));
    sw.Stop();
    Console.WriteLine($"DONE {result.ImageCount} images in {sw.Elapsed}");
    Console.WriteLine($"OUTPUT {result.OutputDirectory}");
    foreach (var image in result.Manifest.Images)
    {
        Console.WriteLine($"IMAGE {image.PartitionName}\t{image.FileName}\t{image.SizeBytes}\t{string.Join(",", image.SourceOperations)}");
    }

    foreach (var warning in result.Manifest.Warnings)
    {
        Console.WriteLine($"WARN {warning}");
    }
}

async Task PackAsync(string outputPath, string[] imageSpecs)
{
    var images = new List<PackImageInput>();
    foreach (var spec in imageSpecs)
    {
        var separator = spec.IndexOf('=');
        if (separator <= 0)
        {
            throw new ArgumentException($"invalid image spec: {spec}");
        }

        images.Add(new PackImageInput
        {
            PartitionName = spec[..separator],
            FilePath = spec[(separator + 1)..]
        });
    }

    var packer = new PackagePacker(logging);
    var progress = new Progress<OperationProgress>(p =>
    {
        Console.WriteLine($"PROGRESS {p.Percentage:0.0}% {p.Status}");
    });
    var result = await packer.PackAsync(images, outputPath, progress, line => Console.WriteLine($"LOG {line}"));
    Console.WriteLine($"DONE {result.OutputPath} {result.SizeBytes}");
}
