# Chess Trainer

A **WinUI 3 / C#** desktop app and a responsive **Blazor web/mobile app** for deliberate chess calculation. The desktop app uses local SQLite without login. The cloud edition shares its library, studies and results across browsers and phones, protected by owner-restricted Microsoft sign-in.

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

## Web and mobile

`src\ChessTrainer.Web` is an installable, online-first web app, not a separate native iOS/Android binary. It reuses the desktop chess engine and supports all three practice modes, variations, annotations, results, editable studies, FEN/CSV/screenshot imports, and the same piece/board/theme galleries. On iPhone/iPad use Safari's **Share > Add to Home Screen**; Chrome/Edge offer **Install app** or **Add to Home screen**.

The server runs chess logic and grading. Practice responses do not contain the reference solution or grading before submission. Browsers and phones use a shared Azure SQL database; screenshot transcription uses the existing keyless Azure AI deployment. The service worker does not cache private pages, API responses or answers. An internet connection is required, including after installation.

**Desktop data is copied, not continuously synchronized.** Migration leaves the original database untouched. Subsequent Windows-app edits stay local; subsequent web/mobile edits are shared between cloud devices. The WinUI app has not been switched to the cloud API.

### Local web development

Install the .NET 10 SDK. This explicit development-only identity bypass must never be enabled on an exposed server. Use a separate database, not the desktop database:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Storage__SqlitePath = "$PWD\artifacts\cloud\development.db"
$env:Authentication__DevelopmentIdentity = 'true'
$env:Authentication__OwnerId = '11111111-1111-1111-1111-111111111111'
$env:Authentication__TenantId = '22222222-2222-2222-2222-222222222222'
New-Item -ItemType Directory -Force artifacts\cloud | Out-Null
dotnet run --project src\ChessTrainer.Web --no-launch-profile -- --task initialize
dotnet run --project src\ChessTrainer.Web --no-launch-profile -- --task migrate --source-db "$env:LOCALAPPDATA\ChessTrainer\trainer.db" --puzzle-limit 10000
dotnet run --project src\ChessTrainer.Web --no-launch-profile --urls http://localhost:5097
```

The bounded copy includes up to 10,000 puzzles plus studies, positions, history and appearance settings. Remove `--puzzle-limit` only when intentionally copying the full dataset. If the desktop app has not been run, create its database first using the Windows instructions above. Local image recognition additionally requires `Vision__Endpoint`, `Vision__Deployment` and an Azure CLI identity with inference permission.

### Azure deployment

`infra\Deploy-Web.ps1` provisions a dedicated resource group with VNet-integrated Azure Container Apps Consumption, a private Basic container registry, a user-assigned managed identity, an Entra-only Azure SQL server/database, a SQL private endpoint/private DNS zone, and a single-tenant Microsoft sign-in registration. SQL public network access stays disabled, including when subscription policy enforces that setting. The app identity receives SQL data access and resource-scoped inference access to the existing `rg-chess-trainer-ai` model; SQL passwords and model keys are not used.

Run from a fresh PowerShell session with an appropriately privileged Azure CLI account. The selected subscription must permit resource creation, role assignment, required network-feature registration and app registration. The script registers `Microsoft.Network/AllowBringYourOwnPublicIpAddress` when needed by Container Apps infrastructure; subscriptions requiring feature approval must finish that approval before deployment. SQL and Container Apps region availability are independent; `-SqlLocation` and `-SqlServerName` allow a different SQL region/name when provisioning is restricted.

```powershell
.\infra\Deploy-Web.ps1 -ProvisionOnly
.\infra\Deploy-Web.ps1 -DeployOnly -LocalDatabase "$env:LOCALAPPDATA\ChessTrainer\trainer.db"
```

Provisioning records non-secret resource identities and the URL in ignored `artifacts\cloud\deployment.json`. Deployment publishes Linux containers without requiring Docker. A short-lived Container Apps job initializes the database and copies data from inside the VNet; the developer machine does not need public SQL access or a VPN.

For migration, SQLite's backup API creates a consistent snapshot, including committed WAL data, without writing to the original database. The snapshot is temporarily included in a private ACR migration image, never in the web-serving image or source control. A separate bootstrap identity temporarily becomes SQL Entra administrator, creates the schema and grants the runtime identity only SELECT/INSERT/UPDATE. The script restores the provisioning user's SQL administrator role and the normal S0 tier in `finally`. Successful bootstraps remove their job, identity, registry image and local database snapshot. Failed jobs/images remain for diagnostics; they are private and must be removed after troubleshooting.

Migration processes 50,000-puzzle batches and prints progress in the bootstrap job's console logs. Puzzle IDs use binary, case-sensitive SQL collation, preserving distinct Lichess IDs. Reruns upsert puzzles, add missing study/position/attempt IDs and preserve existing cloud edits and settings. A failed migration can leave earlier cloud batches committed; rerun to finish. Back up the source first as usual.

After the initial copy, omit `-LocalDatabase` for ordinary deployments. Bootstrap grants use the managed identity's **client ID** for its contained SQL user SID; Azure role assignments and SQL administrator configuration use its **object ID**. Replacing the runtime identity recreates only its named SQL access principal and reapplies the restricted grants, not application tables or saved data.

Exact per-rating counts are maintained transactionally by database triggers. Random selection weights each rating by its puzzle count, then reads an offset within that rating, preserving uniform selection without repeatedly scanning millions of rows on the S0 database. Existing cloud libraries receive a one-time count backfill during initialization.

**Costs and operating limits:** Azure SQL S0 (250 GB maximum), ACR Basic, the private endpoint/private DNS and applicable VNet infrastructure have standing charges even while idle. Full migrations temporarily use SQL S3 and a 1 CPU / 2 GiB bootstrap job, then return to S0. Container Apps scales from zero to one web replica (0.5 CPU / 1 GiB); requests and AI inference may incur additional usage charges. Prices depend on region and subscription. No custom domain or paid monitoring workspace is provisioned. Console diagnostics are available through Container Apps; durable centralized log retention is not configured. If deployment is forcibly terminated, verify that the SQL administrator and S0 tier have been restored; a terminated shell cannot guarantee cleanup.

Blazor uses server-side interactive circuits. Cold starts and reconnects are expected; saved drafts survive server restarts, while unsaved text can be lost. One replica is intentional; scaling to multiple replicas requires a circuit/session strategy. Results load 200 attempts at a time; **Load older attempts** keeps earlier history accessible. The Easy Auth client secret expires after one year; rerunning provisioning renews it when fewer than 30 days remain.

Microsoft sign-in is required before entering the cloud workspace. Public health endpoints and installation/static assets contain no personal data. Easy Auth validates sign-in, and the application additionally checks both the configured tenant and owner object IDs. The original login-free requirement remains unchanged for local Windows use.

### API

JSON endpoints under `/api` require the owner's authenticated session. Writes also require `X-ChessTrainer-Request: 1`; cross-origin access is not enabled. The browser UI uses the same server application services directly over its authenticated Blazor circuit.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/me` | Owner and puzzle count |
| `POST /api/attempts` | Start a random rating-filtered puzzle or study position |
| `GET /api/attempts?offset=0&limit=200`, `GET /api/attempts/{id}` | Paginated history or a sanitized practice/review snapshot |
| `POST /api/attempts/{id}/commands` | Notation, board moves, variations, navigation, notes, mode or submission |
| `GET/POST /api/studies`, `PUT /api/studies/{id}` | List/create/rename studies |
| `GET/POST /api/studies/{id}/positions`, `PUT /api/positions/{id}` | List/import/edit positions |
| `GET/PUT /api/settings/{name}` | Theme, board and piece preferences |
| `POST /api/vision` | Explicitly requested PNG-to-FEN transcription |

Attempt commands include the last numeric `revision`; stale edits return HTTP 409 instead of silently overwriting another device's changes. Position saves overwrite in place, without user-facing study versioning. The API limits requests to 120/minute per user/IP; image transcription is additionally limited to 20/hour per owner per running process.

`GET /health` checks the process; `GET /health/ready` checks database connectivity/schema. Neither is proof that sign-in or image transcription is working. Configure Microsoft sign-in through the hosting platform; do not expose the container directly behind a proxy that accepts client-supplied identity headers.

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

Tests cover chess legality, setup semantics, all three modes, hidden grading, variations, alternate mates, atomic notation input, rating filtering, compressed import, rollback, study imports/organization, FEN validation, ungraded positions, annotations and immutable snapshots, legacy JSON compatibility, persistent draft/result round trips, and the authenticated vision request/response contract. Cloud integration tests additionally cover answer withholding, frozen-board practice, stale-device conflicts, ownership, forged headers, write protection, repeatable read-only migration, atomic position batches and real SVG delivery. These tests use SQLite and do not replace live SQL/managed-identity deployment checks. The separate VerifyVision tool checks actual Azure inference rather than a mock.

Refresh the bundled CC0 sample from the first 2,000 records of the current archive:

```powershell
dotnet run --project tools\RefreshStarter -- data\starter-puzzles.csv
```

## License

Original project code is licensed under the [MIT License](LICENSE). Third-party dependencies, artwork and data retain their respective licenses.

Dependency and artwork notices are in `THIRD-PARTY-NOTICES.txt` and are copied into application output. `tools\Download-Pieces.ps1` reproducibly downloads the five SVG sets from a pinned Lichess source revision; their license files are in `src\ChessTrainer.App\Assets\Pieces`.
