# Chess Trainer

A native **WinUI 3 / C#** desktop app for deliberate chess calculation. Single user, local SQLite storage, no login.

## Run

Windows 10 version 1809 or later (x64) and the .NET 10 SDK:

```powershell
dotnet run --project src\ChessTrainer.App -p:Platform=x64
```

The Windows App SDK and .NET runtime are bundled in the build output; no separate Windows App SDK runtime installation is needed.

Create a portable release:

```powershell
dotnet publish src\ChessTrainer.App -c Release -p:Platform=x64 -o artifacts\ChessTrainer
.\artifacts\ChessTrainer\ChessTrainer.App.exe
```

## Practice

Set an inclusive rating range and choose **Next puzzle**. Puzzles are selected uniformly from the local library, avoiding the immediately previous puzzle when the range has another match.

The heading identifies the side to solve. **Puzzle rating** is the Lichess puzzle difficulty rating, not your personal rating. The details button retains the original Lichess ID (for example `04eXD`) for reference without using it as the main title.

1. **Move pieces:** drag a piece or click its source and destination; SAN is recorded. You move **both colors**. Promotions offer queen, rook, bishop and knight.
2. **Enter notation:** enter SAN (`Be6+ Kd8 Qf8#`) or UCI (`a2e6 d7d8 f7f8`); the board follows the selected move.
3. **Visualization:** enter notation while the board stays at the puzzle's initial position. It is not updated by navigation until submission.

The move list and arrow buttons navigate the current line. To replace a continuation, select its last retained move and use **Line actions > Trim after selected position**. To explore another opponent response, select one of your moves and use **Branch opponent response**. Each variation is a full independent line with a fixed shared prefix; editing its parent later does not rewrite it.

Input validation checks only chess legality and notation. There are no hints, automatic opponent responses, engine evaluations or puzzle-correctness indicators before **Submit answer**. Typed entries are added atomically: a bad token leaves the line unchanged. Add or clear pending text before submission or changing attempts.

Submission locks and saves the attempt and reveals the published solution. Correctness is assessed against the Lichess continuation; alternative checkmates are accepted. Short matching lines are **Incomplete**. A differing solver move is **Incorrect**. A differing opponent response is **Unverified**, not incorrect: Lichess publishes only one principal variation, and this app does not bundle an engine to grade arbitrary branches. Each line receives its own result; the history summary reports the main-line result.

Moves and lines are saved after edits, including unsubmitted attempts. The latest draft is restored on startup. **Results** opens previous attempts for move-by-move review or resumes an unsubmitted attempt without showing its answer.

## Appearance and layout

Open **Appearance** in the navigation rail, or use the picture button beneath the board, to compare five piece sets: **Chessnut**, **Celtic**, **Spatial**, **Fantasy**, and **RhosGFX**. Each preview shows all twelve pieces on light and dark squares. Selection applies immediately and persists across restarts. All SVG artwork is bundled for offline use, with artist credits and licenses.

Choose **Light** or **Dark** under Application theme. The board gallery offers **Sage**, **Walnut**, **Slate**, **Sand**, **Ocean**, and **Rose**. Theme, board and pieces are independent preferences, remembered across restarts.

The practice workspace expands into the available window width. The board remains square and fits the available height; the analysis region scrolls independently while **Submit answer** stays visible. Narrow windows stack the board and analysis instead of introducing horizontal scrolling.

### Style guide

The visual foundation is [Microsoft Fluent 2 for Windows](https://fluent2.microsoft.design/components/windows), using native WinUI controls and [Windows responsive-layout guidance](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design):

- System typography with a clear title, section-heading, body and caption hierarchy.
- Consistent spacing, neutral surfaces, subtle borders, and a single system accent for primary actions and selection.
- Native navigation, tooltips, keyboard focus, accessible control names, and familiar Windows interactions.
- Bounded responsive grids, independent scrolling, and visible primary actions rather than fixed-width pages.

Puzzle themes and solution-oriented titles remain hidden before submission so presentation does not give away the answer.

## Studies and your positions

Open **Studies**, create a named study, then add positions. Rename studies or move selected positions between them to organize your collection. **Practice selected position** opens a new attempt in the normal workspace; all three input modes, variations, submission and Results history work with study positions.

Select a position in the study list to edit its title, FEN, reference solution or notes. **Save changes** overwrites that position in place; repeated saves do not create copies or versions. Choose **New position** to clear the editor and add a separate position. Existing practice attempts retain their saved snapshots.

Three input paths are available as visible **Import source** buttons:

1. **FEN:** paste a complete six-field FEN, add a title and optional starting notes, preview the board, and save it to the selected study. Unlike Lichess puzzle imports, this FEN is the exact starting position: no setup move is applied.
2. **CSV:** choose a file with a `FEN` column. Optional columns are `Title`, `Solution`, and `Notes` (case-insensitive); quoted commas and multiline notes are supported. Missing titles become Position 1, Position 2, etc. The entire batch is validated and saved transactionally: a malformed row does not partially import.
3. **Screenshot:** choose an image file or paste a bitmap from the clipboard, select White/Black at bottom, then choose **Generate FEN with Azure AI**. Only this action sends the image to the configured Azure resource. Review the generated FEN using **Preview FEN board**, correct any transcription errors and hidden-state fields, and check the confirmation before saving.

An image cannot reliably determine side to move, castling rights, en-passant rights or counters. AI returns piece placement only; the editable FEN defaults these fields to `w - - 0 1`. Uncertain identifications are displayed, never silently accepted. No image is stored in the database; the confirmed FEN is stored.

An optional **reference solution** can be entered as SAN or UCI, including both sides. It begins at the exact supplied FEN. Without a reference solution, submission saves the analysis as **Unverified**, not Correct/Incorrect. Reference solutions are hidden in the practice workspace until submission.

### Comments and annotations

In Practice or a saved Results attempt, select a move (or Starting position) and choose **Notes** beside Your analysis. Comments and annotation symbols (`!`, `?`, `!!`, `??`, `!?`, `?!`) save automatically for that exact line and ply. **Back to moves** returns to move entry. Notes remain editable after submission while the submitted moves and grading stay locked. Variations copy their shared-prefix notes independently; trimming a continuation removes notes attached to its discarded moves.

### Azure AI Foundry

The app is configured for the dedicated resource group **rg-chess-trainer-ai** in **East US 2**, Foundry resource **chess-trainer-vision-f70cfb6a**, project **chess-trainer**, and deployment **chess-vision** (`gpt-4.1-mini`, version `2025-04-14`, Global Standard). This is pay-as-you-go inference, not provisioned throughput; image requests incur Azure usage charges. Global Standard inference may be processed outside the resource's region according to Azure's global deployment routing. No agent, agent memory, or tool execution is needed for transcription.

Authentication uses **Azure.Identity / AzureCliCredential**, reusing the machine's Azure CLI sign-in. API keys are disabled on the resource; no credentials are saved by the app. The provisioning account has the resource-scoped **Cognitive Services OpenAI User** role. If sign-in expires, refresh the Azure CLI sign-in to the subscription containing this resource. A different user needs this role on their own configured resource.

The **Azure AI connection** expander on Studies allows changing the HTTPS resource endpoint and deployment; saved overrides live in local SQLite settings. Packaged defaults are in `src\ChessTrainer.App\Data\foundry.json`. Only Azure OpenAI public-cloud resource root endpoints are accepted. The reproducible deployment template is `infra\foundry.json`; it creates the Foundry resource/project, 10k-TPM model quota allocation and resource-scoped inference role. Redeploy it into an explicitly chosen resource group using parameters `accountName`, `principalId` (the intended user's Entra object ID) and optional `location`.

To run a real, billable end-to-end inference check on a known PNG diagram (with White at bottom):

```powershell
dotnet run --project tools\VerifyVision -- <image.png> <Azure-endpoint> <deployment-name> <expected-FEN-piece-placement>
```

## Puzzle data

The bundled starter CSV contains 2,000 puzzles sourced from [Lichess's open puzzle database](https://database.lichess.org/#puzzles), released under **CC0 1.0**. Ratings are snapshots, not live ratings.

**Puzzle library** supports:

- Streaming a 10,000-row sample from the start of the official archive (not a representative sample of the entire database).
- Importing a local `.csv` or `.csv.zst`.
- Streaming the full <https://database.lichess.org/lichess_db_puzzle.csv.zst>.

The full database has millions of rows; allow several GB of free space (including transaction overhead) and time to download/decompress/index it. Imports are streamed and transactional. Cancelling or any parse/network/database error rolls back the import without replacing the existing library. Re-imports update matching puzzle IDs rather than duplicating them. Normal practice and results remain local. The app advances Lichess's supplied FEN by the first setup move before displaying the puzzle; the second UCI move starts the answer.

Stay on the Puzzle library page while an import is active; finish or cancel it before continuing practice (imports hold a database write transaction).

Data and logs live in `%LOCALAPPDATA%\ChessTrainer`:

- `trainer.db`: puzzles, studies, custom FEN positions, reference solutions, comments/annotations, attempt snapshots, timestamps, modes, results and preferences.
- `errors.log` / `crash.log`: local diagnostics.

Back up the directory with the app closed. Practice, studies and history remain local. Network operations are explicitly requested Lichess database downloads and Azure screenshot transcription (plus Azure authentication). Selecting or pasting an image alone does not upload it.

## Development

```powershell
dotnet build ChessTrainer.sln -p:Platform=x64
dotnet test tests\ChessTrainer.Tests
```

Tests cover chess legality, setup semantics, all three modes, hidden grading, variations, alternate mates, atomic notation input, rating filtering, compressed import, rollback, study imports/organization, FEN validation, ungraded positions, annotations and immutable snapshots, legacy JSON compatibility, persistent draft/result round trips, and the authenticated vision request/response contract. The separate VerifyVision tool checks actual Azure inference rather than a mock.

Refresh the bundled CC0 sample from the first 2,000 records of the current archive:

```powershell
dotnet run --project tools\RefreshStarter -- data\starter-puzzles.csv
```

Dependency and artwork notices are in `THIRD-PARTY-NOTICES.txt` and are copied into application output. `tools\Download-Pieces.ps1` reproducibly downloads the five SVG sets from a pinned Lichess source revision; their license files are in `src\ChessTrainer.App\Assets\Pieces`.
