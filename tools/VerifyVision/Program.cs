using ChessTrainer.Core;

if (args.Length != 4)
    throw new ArgumentException("Usage: VerifyVision <PNG path> <Azure endpoint> <deployment> <expected placement>");
using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
var result = await new AzureVisionRecognizer(client).RecognizeAsync(await File.ReadAllBytesAsync(args[0]), args[1], args[2], false);
Console.WriteLine($"Recognized: {result.Placement}");
Console.WriteLine($"Uncertainties: {result.Uncertainties}");
if (result.Placement != args[3]) throw new InvalidDataException("Recognition does not match the known position.");
Console.WriteLine("Authenticated Azure screenshot-to-FEN check passed.");
