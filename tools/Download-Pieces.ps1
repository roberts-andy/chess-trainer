$ErrorActionPreference = 'Stop'
$revision = '190e40b57b1a1bf758a9171502ce1702e4720f8e'
$assets = Join-Path $PSScriptRoot '..\src\ChessTrainer.App\Assets\Pieces'
$sets = @('chessnut', 'celtic', 'spatial', 'fantasy', 'rhosgfx')
$jobs = foreach ($set in $sets) {
    $directory = Join-Path $assets $set
    New-Item -ItemType Directory -Force $directory | Out-Null
    foreach ($color in @('w', 'b')) {
        foreach ($piece in @('K', 'Q', 'R', 'B', 'N', 'P')) {
            [pscustomobject]@{
                Url = "https://raw.githubusercontent.com/lichess-org/lila/$revision/public/piece/$set/$color$piece.svg"
                Path = Join-Path $directory "$color$piece.svg"
            }
        }
    }
}
$jobs | ForEach-Object -Parallel {
    $ErrorActionPreference = 'Stop'
    Invoke-WebRequest $_.Url -OutFile $_.Path
} -ThrottleLimit 6
Invoke-WebRequest 'https://raw.githubusercontent.com/LexLuengas/chessnut-pieces/master/LICENSE.txt' -OutFile (Join-Path $assets 'Chessnut-Apache-2.0.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/LexLuengas/chessnut-pieces/master/COPYRIGHT.txt' -OutFile (Join-Path $assets 'Chessnut-Copyright.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/maurimo/chess-art/main/LICENSE' -OutFile (Join-Path $assets 'Monge-MIT.txt')
Write-Output "Downloaded $($jobs.Count) SVG pieces from Lichess revision $revision."
