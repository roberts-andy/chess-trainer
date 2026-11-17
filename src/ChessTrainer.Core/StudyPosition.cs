using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace ChessTrainer.Core;

public sealed record Study(Guid Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record StudyPosition(Guid Id, Guid StudyId, string Title, string Fen, string Solution, string Notes)
{
    public Puzzle ToPuzzle() => new($"study-{Id:N}", Fen, Solution, 0, "", "", true, Title, StudyId);
    public override string ToString() => Title;

    public static StudyPosition Create(Guid studyId, string title, string fen, string solution = "", string notes = "")
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Give the position a title.");
        string normalized = ValidateFen(fen);
        var position = new ChessPosition(normalized);
        var moves = ChessPosition.Tokenize(solution).Select(position.PlayNotation).Select(m => m.Uci).ToArray();
        return new(Guid.NewGuid(), studyId, title.Trim(), normalized, string.Join(" ", moves), notes);
    }

    public static string ValidateFen(string fen)
    {
        string[] fields = fen.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 6) throw new ArgumentException("A FEN must have all six fields, including side to move and move counters.");
        string[] ranks = fields[0].Split('/');
        if (ranks.Length != 8) throw new ArgumentException("A FEN board must contain eight ranks.");
        foreach (string rank in ranks)
        {
            int squares = 0;
            bool digit = false;
            foreach (char piece in rank)
            {
                if (piece is >= '1' and <= '8')
                {
                    if (digit) throw new ArgumentException("Combine consecutive empty squares into one FEN digit.");
                    squares += piece - '0';
                    digit = true;
                }
                else
                {
                    if (!"KQRBNPkqrbnp".Contains(piece)) throw new ArgumentException("The FEN contains an unknown piece.");
                    squares++;
                    digit = false;
                }
            }
            if (squares != 8) throw new ArgumentException("Each FEN rank must describe exactly eight squares.");
        }
        if (fields[0].Count(c => c == 'K') != 1 || fields[0].Count(c => c == 'k') != 1)
            throw new ArgumentException("A position must have exactly one king of each color.");
        if (ranks[0].Any(c => c is 'P' or 'p') || ranks[7].Any(c => c is 'P' or 'p'))
            throw new ArgumentException("Pawns cannot be on the first or eighth rank.");
        if (fields[1] is not ("w" or "b")) throw new ArgumentException("The side to move must be w or b.");
        if (fields[2] != "-" && (fields[2].Any(c => !"KQkq".Contains(c)) || fields[2].Distinct().Count() != fields[2].Length))
            throw new ArgumentException("Invalid FEN castling rights.");
        if (fields[3] != "-" && (fields[3].Length != 2 || fields[3][0] is < 'a' or > 'h' ||
            fields[3][1] != (fields[1] == "w" ? '6' : '3')))
            throw new ArgumentException("Invalid FEN en-passant square for the side to move.");
        if (!int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out int fullMove) || fullMove < 1)
            throw new ArgumentException("FEN counters must be nonnegative; the full-move number starts at one.");
        string normalized = string.Join(" ", fields);
        var board = new ChessPosition(normalized);
        string[] kingSquares = (from file in "abcdefgh" from rank in "12345678"
            let square = $"{file}{rank}" where char.ToLowerInvariant(board.PieceAt(square)) == 'k' select square).ToArray();
        if (Math.Abs(kingSquares[0][0] - kingSquares[1][0]) <= 1 && Math.Abs(kingSquares[0][1] - kingSquares[1][1]) <= 1)
            throw new ArgumentException("The two kings cannot be adjacent.");
        foreach (var (right, king, rook) in new[] { ('K', "e1", "h1"), ('Q', "e1", "a1"), ('k', "e8", "h8"), ('q', "e8", "a8") })
            if (fields[2].Contains(right) &&
                (board.PieceAt(king) != (char.IsUpper(right) ? 'K' : 'k') || board.PieceAt(rook) != (char.IsUpper(right) ? 'R' : 'r')))
                throw new ArgumentException("Castling rights require a king and rook on their original squares.");
        return board.Fen;
    }

    public static IReadOnlyList<StudyPosition> ReadCsv(Stream source, Guid studyId)
    {
        using var parser = new TextFieldParser(source, Encoding.UTF8, true, true) { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        string[] header = parser.ReadFields() ?? throw new InvalidDataException("The position CSV is empty.");
        int Column(string name) => Array.FindIndex(header, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        int fen = Column("FEN"), title = Column("Title"), solution = Column("Solution"), notes = Column("Notes");
        if (fen < 0) throw new InvalidDataException("The CSV needs a FEN column. Optional columns: Title, Solution, Notes.");
        var positions = new List<StudyPosition>();
        while (!parser.EndOfData)
        {
            long record = parser.LineNumber;
            string[] row = parser.ReadFields()!;
            string Field(int index) => index < 0 ? "" : index < row.Length ? row[index] :
                throw new InvalidDataException($"Missing column on CSV record {record}.");
            try
            {
                positions.Add(Create(studyId, title < 0 || string.IsNullOrWhiteSpace(Field(title))
                    ? $"Position {positions.Count + 1}" : Field(title), Field(fen), Field(solution), Field(notes)));
            }
            catch (ArgumentException ex) { throw new InvalidDataException($"CSV record {record}: {ex.Message}", ex); }
        }
        if (positions.Count == 0) throw new InvalidDataException("The CSV contains no positions.");
        return positions;
    }
}
