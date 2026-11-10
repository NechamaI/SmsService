# SMS Service Implementation Plan

> **Execution:** inline, by one implementer, directly on `main`. No git worktrees, no subagents. Steps use checkbox (`- [ ]`) syntax. **Every stage ends with a CHECKPOINT: stop, give the user a short summary, wait for approval, only then commit and continue.**

**Goal:** A REST service that accepts SMS send requests, persists them, sends them in the background to an external (mock) provider with retries, and exposes status queries. The whole system runs locally with `docker compose up --build`.

**Architecture:** One ASP.NET Core Web API (Controllers) with an in-process `BackgroundService` worker, EF Core on SQL Server 2022, and a separate minimal-API mock provider. Pure logic (`PhoneNumberNormalizer`, `SmsLengthCalculator`, `MessageSender`) has no DB/HTTP dependencies and is unit-tested TDD-style.

**Tech Stack:** .NET 10 (SDK 10.0.403), ASP.NET Core, EF Core 10 (SqlServer, Sqlite for tests), Swashbuckle.AspNetCore, xUnit, Microsoft.AspNetCore.Mvc.Testing, Docker Compose.

**Spec:** [SPEC.md](../SPEC.md), plus the amendments below (approved 2026-11-08).

## Approved Amendments to the SPEC

1. **Attempts:** `Attempts++` on **every** provider call (success, transient, permanent). Backoff is chosen by the post-increment value: 1→2s, 2→10s, 3→30s. `Failed` when a transient error happens and `Attempts >= 4`.
2. **Timeout = transient** (duplicate-send risk accepted and documented in README). **HTTP 408** is transient too. Our message `Id` is sent to the provider as `reference`.
3. **Length counting:** UCS2 counts UTF-16 code units (`string.Length`, emoji = 2). GSM7 counts septets (extension char = 2). Segments = `ceil`; an extension char straddling a segment boundary is ignored (documented). Body is stored as-is (no trim); rejected only if empty/whitespace.
4. **Worker:** marks the batch `Sending` and calls `SaveChanges` **before** any provider call, then sends one by one, saving after each. Worst-case blocking (20 × 5s timeout) is accepted and documented.
5. **Startup:** SQL Server healthcheck uses `/opt/mssql-tools18/bin/sqlcmd -C`; `EnableRetryOnFailure` + a short retry loop around `Migrate()`.
6. **Integration test:** `WebApplicationFactory` + SQLite in-memory (`EnsureCreated`), worker disabled, fake provider. Real SQL Server is covered by the manual E2E in Stage 7. If time runs short, this is the first thing dropped.

## Global Constraints

- Code and names in English. All times UTC (`DateTime` with `Kind = Utc`, serialized with `Z`).
- No unnecessary libraries. Allowed packages: `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Design`, `Swashbuckle.AspNetCore` (Api); `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.EntityFrameworkCore.Sqlite` (Tests). Write a tiny fake `TimeProvider` instead of adding a package.
- Configuration via `appsettings.json` + environment variables (`ConnectionStrings__Default`, `Provider__BaseUrl`, `Worker__Enabled`, `Worker__BatchSize`, `FAIL_RATE`).
- TDD (red → green) only for `PhoneNumberNormalizer`, `SmsLengthCalculator`, `MessageSender`. Other tests are written after the code.
- Each stage ends compiling with `dotnet build` and `dotnet test` green.

## Review Focus

Inputs the spec implies but doesn't spell out; each has a pinning test in the owning stage:

1. **Non-ASCII digits in phone numbers** (e.g., Arabic-Indic `٠٥٢...`, which `char.IsDigit` accepts) → rejected. *(Stage 3 test)*
2. **Missing/null fields or `{}` body on POST** → 400 ProblemDetails with errors for `to` and `body`, never 500. *(Stage 4 test)*
3. **`status` query given as a number (`?status=1`) or in lowercase (`pending`)** → number is 400, lowercase is accepted. *(Stage 4 test)*
4. **Out-of-range paging** (`page=0`, `pageSize=0`, `pageSize=201`) → 400. *(Stage 4 test)*
5. **Timestamps read back from the DB lose `Kind`** → API must return `createdAt` ending in `Z`. *(Stage 4 test)*

---

## File Map

```
SmsService.slnx
docker-compose.yml, .gitignore, README.md, SPEC.md
docs/PLAN.md
src/SmsService.Api/
    SmsService.Api.csproj, Dockerfile, .dockerignore, Program.cs, appsettings.json
    Controllers/MessagesController.cs
    Contracts/CreateMessageRequest.cs, MessageResponse.cs, PagedResponse.cs
    Domain/Message.cs, MessageStatus.cs, SmsEncoding.cs
    Validation/PhoneNumberNormalizer.cs, SmsLengthCalculator.cs, CreateMessageValidator.cs
    Sending/ISmsProvider.cs, ProviderResult.cs, HttpSmsProvider.cs, MessageSender.cs, SendingWorker.cs, WorkerOptions.cs
    Data/AppDbContext.cs, DatabaseMigrator.cs, Migrations/
src/MockSmsProvider/
    MockSmsProvider.csproj, Dockerfile, .dockerignore, Program.cs
tests/SmsService.Tests/
    SmsService.Tests.csproj
    PhoneNumberNormalizerTests.cs, SmsLengthCalculatorTests.cs, MessageSenderTests.cs
    Fakes/ManualTimeProvider.cs, Fakes/FakeSmsProvider.cs
    Api/ApiFactory.cs, Api/MessagesApiTests.cs
```

---

### Stage 1: Skeleton (~20 min)

**Files:** solution, three projects, both Dockerfiles, `docker-compose.yml`, `.gitignore`.

- [ ] **Step 1:** `git init -b main`; `dotnet new gitignore`.
- [ ] **Step 2:** Create projects:
  ```
  dotnet new sln -n SmsService
  dotnet new webapi --use-controllers -n SmsService.Api -o src/SmsService.Api
  dotnet new web -n MockSmsProvider -o src/MockSmsProvider
  dotnet new xunit -n SmsService.Tests -o tests/SmsService.Tests
  dotnet sln add src/SmsService.Api src/MockSmsProvider tests/SmsService.Tests
  dotnet add tests/SmsService.Tests reference src/SmsService.Api
  ```
  Remove template sample code (WeatherForecast). Add packages listed in Global Constraints. Add `dotnet-ef` as a local tool (`dotnet new tool-manifest; dotnet tool install dotnet-ef`).
- [ ] **Step 3:** Dockerfiles: multi-stage (`mcr.microsoft.com/dotnet/sdk:10.0` → `mcr.microsoft.com/dotnet/aspnet:10.0`), build context = the project folder, listen on 8080. `.dockerignore` excludes `bin/ obj/`.
- [ ] **Step 4:** `docker-compose.yml`:
  - `sqlserver`: `mcr.microsoft.com/mssql/server:2022-latest`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD=${SA_PASSWORD:-Sms_Service_Passw0rd}`, healthcheck `/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$$MSSQL_SA_PASSWORD" -C -b -Q "SELECT 1"`, interval 10s, retries 12, start_period 20s.
  - `mock-provider`: build `./src/MockSmsProvider`, port `8081:8080`, `FAIL_RATE=${FAIL_RATE:-0.15}`.
  - `api`: build `./src/SmsService.Api`, port `8080:8080`, `ConnectionStrings__Default=Server=sqlserver;Database=SmsService;User Id=sa;Password=...;TrustServerCertificate=True`, `Provider__BaseUrl=http://mock-provider:8080`, `depends_on: sqlserver: service_healthy, mock-provider: service_started` (aspnet image has no curl; the API tolerates a provider that is down via retries).
- [ ] **Step 5:** Verify: `dotnet build` succeeds; `dotnet test` passes (template test, deleted in Stage 3).
- [ ] **CHECKPOINT → on approval:** `git add -A; git commit -m "chore: solution skeleton and docker-compose"`

---

### Stage 2: Model & DB (~20 min)

**Interfaces — Produces:**
- `enum MessageStatus { Pending, Sending, Sent, Failed }`; `enum SmsEncoding { GSM7, UCS2 }`
- `class Message { Guid Id; string To; string Body; SmsEncoding Encoding; int Segments; MessageStatus Status; int Attempts; DateTime NextAttemptAt; string? LastError; string? ProviderMessageId; DateTime CreatedAt; DateTime UpdatedAt; DateTime? SentAt; }`
- `AppDbContext : DbContext` with `DbSet<Message> Messages`
- `static class DatabaseMigrator { static Task MigrateAsync(IServiceProvider services, CancellationToken ct) }`

- [ ] **Step 1:** Domain types in `Domain/`. `Message.LastErrorMaxLength = 500` as a public const.
- [ ] **Step 2:** `AppDbContext.OnModelCreating`: column types/lengths exactly per SPEC §5 (`To` nvarchar(16), `Body` nvarchar(1000), `Encoding` varchar(10) and `Status` varchar(20) via `HasConversion<string>()`, `LastError` nvarchar(500), `ProviderMessageId` nvarchar(100)); `Attempts` default 0; indexes `(Status, NextAttemptAt)` and `(Status, CreatedAt DESC)` (`IsDescending(false, true)`). Apply a `DateTime`/`DateTime?` value converter that sets `DateTimeKind.Utc` on read for every DateTime property.
- [ ] **Step 3:** `Program.cs`: `AddDbContext<AppDbContext>(o => o.UseSqlServer(cs, s => s.EnableRetryOnFailure()))`. `DatabaseMigrator.MigrateAsync`: only if `db.Database.IsSqlServer()`; up to 10 tries of `MigrateAsync()`, 3s apart, log each failure, rethrow on the last. Call it before `app.Run()`.
- [ ] **Step 4:** `dotnet ef migrations add InitialCreate -p src/SmsService.Api -o Data/Migrations`. Verify: `dotnet build` succeeds; the migration contains both indexes.
- [ ] **CHECKPOINT → on approval:** commit `feat: message model, DbContext and startup migration`

---

### Stage 3: Validation — TDD (~35 min)

**Interfaces — Produces:**
- `static class PhoneNumberNormalizer { const string ErrorMessage; static bool TryNormalize(string? input, out string normalized) }`
- `record SmsLengthResult(SmsEncoding Encoding, int Length, int Segments, int MaxLength) { bool IsWithinLimit => Length <= MaxLength; }`
- `static class SmsLengthCalculator { static SmsLengthResult Calculate(string body) }`

`ErrorMessage = "Invalid phone number. Expected an Israeli mobile number, e.g. 052-1234567 or +972521234567."`

#### 3a. PhoneNumberNormalizer

- [ ] **Step 1: Write failing tests** in `PhoneNumberNormalizerTests.cs`:
  ```csharp
  [Theory]
  [InlineData("0521234567")] [InlineData("052-123-4567")] [InlineData("(052) 123 4567")]
  [InlineData("+972521234567")] [InlineData("+972 52-123-4567")]
  [InlineData("972521234567")] [InlineData("00972521234567")]
  public void Valid_formats_normalize_to_E164(string input)
  { Assert.True(PhoneNumberNormalizer.TryNormalize(input, out var n)); Assert.Equal("+972521234567", n); }

  [Theory]
  [InlineData(null)] [InlineData("")] [InlineData("   ")]
  [InlineData("02-1234567")]       // landline
  [InlineData("+97221234567")]     // landline, intl
  [InlineData("052123456")]        // short
  [InlineData("05212345678")]      // long
  [InlineData("05212a4567")]       // letters
  [InlineData("052.123.4567")]     // dots are not stripped
  [InlineData("+9720521234567")]   // trunk zero after country code
  [InlineData("٠٥٢١٢٣٤٥٦٧")]       // Arabic-Indic digits (Review Focus 1)
  public void Invalid_formats_are_rejected(string? input)
  { Assert.False(PhoneNumberNormalizer.TryNormalize(input, out _)); }
  ```
- [ ] **Step 2:** `dotnet test --filter PhoneNumberNormalizerTests` → FAIL (type missing).
- [ ] **Step 3:** Implement: strip `' '`, `'-'`, `'('`, `')'`; map prefixes `+972`/`00972`/`972` → `0`; then require exactly `05` + 8 ASCII digits (`c is >= '0' and <= '9'`, not `char.IsDigit`); output `"+972" + local[1..]`.
- [ ] **Step 4:** Tests → PASS. Delete the template `UnitTest1.cs`.

#### 3b. SmsLengthCalculator

- [ ] **Step 1: Write failing tests** in `SmsLengthCalculatorTests.cs` (helper `R(string s) => SmsLengthCalculator.Calculate(s)`):
  - `new string('a',160)` → GSM7, Length 160, Segments 1, within limit; `161` → Segments 2.
  - `306` → 2 segments; `307` → 3 segments.
  - `459` → 3 segments, within limit; `460` → `IsWithinLimit == false`, `MaxLength == 459`.
  - `new string('א',70)` → UCS2, 1 segment; `71` → 2; `201` → 3, within limit; `202` → not within limit, `MaxLength == 201`.
  - `"€"` → Length 2; `"{"` → Length 2; `new string('a',159) + "€"` → Length 161, Segments 2.
  - `new string('a',100) + "א"` → UCS2.
  - `"😀"` → UCS2, Length 2.
  - `"Hello\nWorld @£$"` → GSM7 (basic set includes these).
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement with these exact character sets (ESC `\u001B` itself is NOT accepted as input):
  ```csharp
  const string Basic = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
  const string Extension = "\f^{}\\[~]|€";
  ```
  GSM7 if every char is in `Basic ∪ Extension` (length = basic×1 + extension×2), else UCS2 with length = `body.Length`. Limits: GSM7 160/153/459, UCS2 70/67/201. Segments: `length <= single ? 1 : ceil(length / multi)`.
- [ ] **Step 4:** Tests → PASS.

#### 3c. CreateMessageValidator (no TDD; covered by Stage 4 API tests)

- [ ] **Step 5:** `static class CreateMessageValidator { static ValidatedMessage? Validate(CreateMessageRequest req, ModelStateDictionary errors) }` where `record ValidatedMessage(string To, string Body, SmsLengthResult Length)`. Adds errors under keys `"to"` and `"body"`:
  - to missing/invalid → `PhoneNumberNormalizer.ErrorMessage`
  - body null/whitespace → `"Message body is required."`
  - over limit → `$"Message is {Length} characters long ({Encoding}); the maximum is {MaxLength}."`
- [ ] **CHECKPOINT → on approval:** commit `feat: phone normalization and SMS length calculation (TDD)`

---

### Stage 4: API (~40 min)

**Interfaces — Consumes:** Stage 2 types; Stage 3 `CreateMessageValidator`.
**Produces:**
- `record CreateMessageRequest(string? To, string? Body)`
- `record MessageResponse(Guid Id, string To, string Body, MessageStatus Status, int Attempts, string? LastError, int Segments, SmsEncoding Encoding, DateTime CreatedAt, DateTime UpdatedAt, DateTime? SentAt)` with `static MessageResponse From(Message m)`
- `record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)`
- `public partial class Program;` (for `WebApplicationFactory`)

- [ ] **Step 1:** `Program.cs`: `AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))`, `AddProblemDetails()`, `AddSwaggerGen()`; `UseSwagger()` + `UseSwaggerUI()` in all environments (the docker container runs as Production).
- [ ] **Step 2:** `MessagesController` at route `messages`:
  - `POST` → validate; on failure `ValidationProblem(ModelState)`. On success create `Message` (`Id = Guid.NewGuid()`, `Status = Pending`, `Attempts = 0`, `NextAttemptAt = CreatedAt = UpdatedAt = now` from injected `TimeProvider`), save, `return AcceptedAtAction(nameof(GetById), new { id }, new { id, status = "Pending" })`.
  - `GET {id:guid}` → 200 `MessageResponse` / 404.
  - `GET ?status&page=1&pageSize=50` → `status`: `Enum.TryParse(ignoreCase: true)` **and** reject all-digit strings (otherwise `"1"` parses); `page >= 1`; `1 <= pageSize <= 200`; any violation → `ValidationProblem` with the field key. Order by `CreatedAt DESC`, `Skip/Take`, `CountAsync` for `total`.
  - Register `TimeProvider.System` as a singleton.
- [ ] **Step 3: Integration tests (test-after)** — `ApiFactory : WebApplicationFactory<Program>`: environment `"Testing"`; in `ConfigureTestServices` remove `DbContextOptions<AppDbContext>` and `IDbContextOptionsConfiguration<AppDbContext>` registrations, register SQLite on a single open `SqliteConnection("DataSource=:memory:")`, call `EnsureCreated()`. `MessagesApiTests`:
  - `Post_valid_returns_202_with_location_and_get_returns_it`: POST `{to:"052-123-4567", body:"שלום"}` → 202, `Location` ends with `/messages/{id}`, response `status == "Pending"`; GET → `to == "+972521234567"`, `encoding == "UCS2"`, `segments == 1`, `createdAt` string ends with `"Z"` (Review Focus 5).
  - `Post_invalid_returns_problem_details_per_field`: `{to:"123", body:""}` → 400, `application/problem+json`, `errors` has `to` and `body`.
  - `Post_empty_object_returns_400_not_500`: `{}` → 400 with `to` and `body` errors (Review Focus 2).
  - `Get_unknown_returns_404`.
  - `List_filters_by_status_newest_first`: post 2 messages, `?status=pending` → `total == 2`, first item is the newer one (Review Focus 3, lowercase).
  - `List_rejects_bad_query` theory: `?status=Bogus`, `?status=1`, `?page=0`, `?pageSize=0`, `?pageSize=201` → 400 (Review Focus 3, 4).
- [ ] **Step 4:** `dotnet test` → all PASS.
- [ ] **CHECKPOINT → on approval:** commit `feat: messages API with ProblemDetails, Swagger and integration tests`

---

### Stage 5: Sending (~50 min)

**Interfaces — Produces:**
- `record ProviderResult(bool Success, bool IsTransient, string? ProviderMessageId, string? Error)` with factories `Ok(string id)`, `Transient(string error)`, `Permanent(string error)`
- `interface ISmsProvider { Task<ProviderResult> SendAsync(Guid reference, string to, string body, CancellationToken ct); }`
- `class MessageSender(ISmsProvider provider, TimeProvider time) { const int MaxAttempts = 4; Task SendAsync(Message message, CancellationToken ct); }` — mutates `message`, no DB.
- `class WorkerOptions { bool Enabled = true; int BatchSize = 20; }` bound from section `Worker`.

#### 5a. MessageSender — TDD

- [ ] **Step 1: Fakes:** `ManualTimeProvider : TimeProvider` (settable `UtcNow`, overrides `GetUtcNow()`); `FakeSmsProvider : ISmsProvider` (returns a configured `ProviderResult` or throws a configured exception; records the last `(reference, to, body)`).
- [ ] **Step 2: Write failing tests** in `MessageSenderTests.cs` (now = `2026-01-01T00:00:00Z`, message created with `Status = Sending`):
  - `Success_sets_Sent_with_SentAt_and_ProviderMessageId`: `Ok("prov-1")` → `Sent`, `SentAt == now`, `ProviderMessageId == "prov-1"`, `Attempts == 1`, `LastError == null`, `UpdatedAt == now`.
  - `Transient_returns_to_Pending_with_backoff` theory `(startAttempts, expectedDelaySec)`: `(0,2) (1,10) (2,30)` → `Pending`, `Attempts == start+1`, `NextAttemptAt == now + delay`, `LastError` set.
  - `Transient_on_fourth_attempt_fails`: start `Attempts = 3`, `Transient("503")` → `Failed`, `Attempts == 4`.
  - `Permanent_error_fails_immediately`: start 0, `Permanent("400 blocked")` → `Failed`, `Attempts == 1`, `LastError == "400 blocked"`.
  - `Provider_exception_is_treated_as_transient`: provider throws `InvalidOperationException("boom")` → `Pending`, `LastError` contains `"boom"`.
  - `Cancellation_propagates`: provider throws `OperationCanceledException` with a cancelled token → `SendAsync` throws `OperationCanceledException`.
  - `LastError_is_truncated_to_500`: `Transient(new string('x', 600))` → `LastError.Length == 500`.
  - `Passes_id_to_and_body_to_provider`.
- [ ] **Step 3:** Run → FAIL.
- [ ] **Step 4:** Implement per Amendment 1. Backoff table `[2s, 10s, 30s]` indexed by `Attempts - 1`. Catch non-cancellation exceptions → transient with `ex.Message`.
- [ ] **Step 5:** Tests → PASS.

#### 5b. HttpSmsProvider & SendingWorker (no TDD)

- [ ] **Step 6:** `HttpSmsProvider` as typed client (`AddHttpClient<ISmsProvider, HttpSmsProvider>`, `BaseAddress = Provider:BaseUrl`, `Timeout = 5s`). `POST /sms` with `{ to, text, reference }`. Mapping: 2xx → `Ok(json.id)`; 408, 429, 5xx → `Transient("HTTP {code}: {body}")`; other 4xx → `Permanent(...)`; `HttpRequestException` or `TaskCanceledException` when `!ct.IsCancellationRequested` → `Transient`.
- [ ] **Step 7 (optional, test-after, ~10 min):** one theory with a stub `HttpMessageHandler`: 200 → success; 408/429/503 → transient; 400 → permanent.
- [ ] **Step 8:** `SendingWorker : BackgroundService` (registered only if `Worker:Enabled`; `MessageSender` scoped):
  - On start: `ExecuteUpdateAsync` `Sending → Pending` (log the count).
  - Loop on `PeriodicTimer(1s)`: new scope → query `Status == Pending && NextAttemptAt <= now`, order by `NextAttemptAt`, take `BatchSize` → set `Sending` + `UpdatedAt`, `SaveChangesAsync` → for each: `sender.SendAsync`, `SaveChangesAsync`.
  - Wrap each iteration in try/catch (log, keep looping); let `OperationCanceledException` on shutdown end the loop.
- [ ] **Step 9:** `ApiFactory` sets `Worker:Enabled=false` and replaces `ISmsProvider` with `FakeSmsProvider`. `dotnet test` → all PASS.
- [ ] **CHECKPOINT → on approval:** commit `feat: background sending with retries and HTTP provider`

---

### Stage 6: Mock provider (~15 min)

- [ ] **Step 1:** `src/MockSmsProvider/Program.cs` minimal API:
  - `FAIL_RATE` env var, parsed with `CultureInfo.InvariantCulture`, default `0.15`.
  - `POST /sms` `{ to, text, reference }`: `await Task.Delay(Random.Shared.Next(100, 801))`; missing `to`/`text` → 400; `to` ends with `"999"` → 400 `{ error: "Recipient blocked" }`; `Random.Shared.NextDouble() < failRate` → 503; else 200 `{ id: Guid.NewGuid() }`. Log every request with the reference and the outcome.
  - `GET /health` → 200.
- [ ] **Step 2:** Verify: `dotnet build`; `dotnet run --project src/MockSmsProvider` and a manual POST returns 200 or 503.
- [ ] **CHECKPOINT → on approval:** commit `feat: mock SMS provider`

---

### Stage 7: Manual end-to-end + README (~30 min)

- [ ] **Step 1:** `docker compose up --build` → sqlserver healthy, api applies the migration, Swagger at `http://localhost:8080/swagger`.
- [ ] **Step 2:** Scenarios (via Swagger or curl), record the results:
  - Valid Hebrew message → `Pending` → `Sent` within a few seconds, `providerMessageId` set.
  - Number ending `999` (e.g., `052-123-4999`) → `Failed` after 1 attempt.
  - Restart with `FAIL_RATE=0.9` → attempts climb with the backoff, end `Sent` or `Failed` after 4.
  - Invalid phone / 202-char Hebrew body → 400 ProblemDetails.
  - `GET /messages?status=Failed` filters correctly.
  - `docker compose restart api` while messages are in flight → nothing stuck in `Sending`.
- [ ] **Step 3:** `README.md` with these sections:
  - **How to run:** `docker compose up --build`, URLs (API `:8080/swagger`, mock `:8081`), env vars (`FAIL_RATE`, `SA_PASSWORD`), running tests (`dotnet test`).
  - **Endpoints** with curl examples and sample responses.
  - **Spec:** link to [SPEC.md](SPEC.md) as the source of truth, which already includes the approved amendments, and to this plan in `docs/`.
  - **Design decisions:** 202 + background worker, EF Core vs stored procedures, statuses and retry policy, GSM7/UCS2 limits.
  - **Known limitations:** duplicate send on timeout/crash, single instance, worker blocking, extension char at a segment boundary, SQLite instead of Testcontainers.
  - **AI usage statement:** developed with Claude Code (Claude Opus 5.5). Workflow: brainstorming to challenge the SPEC, then a written plan, then implementation stage by stage with TDD for the critical logic and human review/approval before every commit. All code was reviewed by me; design decisions, assumptions and amendments are mine.
  - **What I'd add with more time** (Part B pointers): employees model and send-to-employee endpoint (SPEC §9), idempotency key on `POST /messages`, delivery reports (DLR), a second provider with failover, an external queue instead of DB polling with multiple workers (row locking / `UPDLOCK, READPAST`), Testcontainers integration tests on real SQL Server, monitoring/metrics and health endpoints, a simple UI.
- [ ] **Step 4:** `dotnet test` → all PASS (final verification).
- [ ] **CHECKPOINT → on approval:** commit `docs: README and end-to-end verification`
