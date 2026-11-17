using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ChessTrainer.App;

internal sealed record PieceSet(string Id, string Name, string Description, string Credit)
{
    public static IReadOnlyList<PieceSet> All { get; } =
    [
        new("chessnut", "Chessnut", "Bold, geometric Staunton pieces.", "Alexis Luengas · Apache 2.0"),
        new("celtic", "Celtic", "Traditional silhouettes with decorative detail.", "Maurizio Monge · MIT"),
        new("spatial", "Spatial", "Sculpted pieces with a dimensional finish.", "Maurizio Monge · MIT"),
        new("fantasy", "Fantasy", "Expressive, illustrated tournament pieces.", "Maurizio Monge · MIT"),
        new("rhosgfx", "RhosGFX", "Playful pieces with bold outlines.", "RhosGFX · CC0 1.0")
    ];

    public Image CreateImage(char piece, Action<string> failed)
    {
        string file = $"{(char.IsUpper(piece) ? 'w' : 'b')}{char.ToUpperInvariant(piece)}.svg";
        var source = new SvgImageSource();
        source.OpenFailed += (_, _) => failed($"Unable to load {Name} piece artwork ({file}). Rebuild or restore the Assets folder.");
        source.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Pieces", Id, file));
        return new Image
        {
            Source = source, Stretch = Stretch.Uniform, Margin = new Thickness(3),
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }
}
