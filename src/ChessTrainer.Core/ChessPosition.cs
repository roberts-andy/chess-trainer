using System.Text.RegularExpressions;
using ChessDotNet;

namespace ChessTrainer.Core;

public sealed partial class ChessPosition
{
    private readonly ChessGame game;

    public ChessPosition(string fen) => game = new ChessGame(fen);
    public string Fen => game.GetFen();
    public bool WhiteToMove => game.WhoseTurn == Player.White;
    public int MoveNumber => game.FullMoveNumber;
    public bool IsCheckmate => game.IsCheckmated(game.WhoseTurn);

    public char PieceAt(string square) => game.GetPieceAt(new Position(square))?.GetFenCharacter() ?? ' ';

    public IReadOnlyList<string> LegalMoves() => game.GetValidMoves(game.WhoseTurn)
        .Where(m => m.Promotion is null or 'Q' or 'R' or 'B' or 'N')
        .Select(ToUci).ToArray();

    public string PlayUci(string uci)
    {
        uci = uci.ToLowerInvariant();
        if (!UciPattern().IsMatch(uci))
            throw new ArgumentException("Use a move such as e2e4 or e7e8q.");
        var move = new Move(uci[..2], uci[2..4], game.WhoseTurn,
            uci.Length == 5 ? char.ToUpperInvariant(uci[4]) : null);
        if (game.MakeMove(move, false) == MoveType.Invalid)
            throw new ArgumentException($"Illegal move: {uci}. No puzzle assessment has been made.");
        return game.Moves[^1].SAN;
    }

    public (string Uci, string San) PlayNotation(string notation)
    {
        if (UciPattern().IsMatch(notation.ToLowerInvariant()))
        {
            string uci = notation.ToLowerInvariant();
            return (uci, PlayUci(uci));
        }

        string normalized = NormalizeSan(notation);
        var candidates = new List<string>();
        foreach (string uci in LegalMoves())
        {
            var copy = new ChessPosition(Fen);
            if (NormalizeSan(copy.PlayUci(uci)) == normalized)
                candidates.Add(uci);
        }
        if (candidates.Count != 1)
            throw new ArgumentException($"Cannot read '{notation}' as a legal, unambiguous SAN move. No puzzle assessment has been made.");
        return (candidates[0], PlayUci(candidates[0]));
    }

    public static string[] Tokenize(string notation)
    {
        string withoutNumbers = MoveNumberPattern().Replace(notation, " ");
        var tokens = withoutNumbers.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(t => t.Contains('(') || t.Contains(')') || t.Contains('{') || t.Contains('}')))
            throw new ArgumentException("Enter one line at a time. Use 'Branch opponent response' to add variations.");
        return tokens.Where(t => t != "...").ToArray();
    }

    private static string NormalizeSan(string value) =>
        value.Trim().Replace('0', 'O').TrimEnd('+', '#', '!', '?');

    private static string ToUci(Move move) =>
        (move.OriginalPosition.ToString() + move.NewPosition +
         (move.Promotion?.ToString() ?? "")).ToLowerInvariant();

    [GeneratedRegex(@"^[a-h][1-8][a-h][1-8][qrbn]?$")]
    private static partial Regex UciPattern();

    [GeneratedRegex(@"\d+\.(?:\.\.)?")]
    private static partial Regex MoveNumberPattern();
}
