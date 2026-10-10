# AI essence generation — end-to-end verification (WI-6, issue #7)

Scope: the Essences page "AI Generate" modal, `POST /api/v1/essences/generate`, the
generation service, the per-user provider key in Settings, the provider call, output
validation, and what is persisted. Verified automatically with a **fake provider**
(scripted HTTP transport, no network, no keys). The live-key check is a manual step
for the maintainer (see the checklist at the end).

## 1. Flow trace

| Step | Where | Notes |
|---|---|---|
| Open modal, load providers and accounts | `src/Aura.Api/Pages/Dashboard/Essences.cshtml` (`btnAiGenerate`) | Providers come from `GET /api/v1/account/supported-providers`. |
| Submit prompt | `aiForm` submit handler (same file) | Posts `{prompt, cloudAccountId, provider}`. On success it opens the normal Create modal pre-filled with the draft. |
| Endpoint | `EssencesController.Generate` | Checks the cloud account exists, then delegates. Maps `ArgumentException` to 400 and `InvalidOperationException` to 422 `generation_failed`. |
| Provider key lookup | `AiEssenceBuilderService.GenerateAsync` → `UserAiKeyService.GetDecryptedKeyAsync` | Per-user, case-insensitive on provider name. Missing key → `InvalidOperationException` with a settings hint. |
| Provider call | `OpenAiCompatibleLlmProvider` (openai, openrouter) and `AnthropicLlmProvider` | Never throws for provider-side problems. Timeouts, HTTP errors, empty or unparseable bodies come back as failed results. |
| Output handling | `ExtractJson` (strips code fences), then `ValidateEssence` | `ValidateEssence` runs the worker's own parser, `DeploymentOrchestrationService.ParseAndSortLayers`. Up to 3 attempts, with the validation error fed back to the model. |
| Usage record | `AiGenerationLog` | Written for success and failure, with tokens summed across attempts. |
| Persistence | Nothing at generation time. The user clicks **Create**, which calls `POST /api/v1/essences`. | See gap G1. |

## 2. Defects found and fixed

| # | Layer | Defect | Fix |
|---|---|---|---|
| F1 | Service | Output was accepted if it contained any `layers` key. Empty `layers`, dependency cycles, unknown `runPolicy`, non-object layers and a non-object root (`[]`) all passed, or escaped as a raw `InvalidOperationException` that never retried. | `ValidateEssence` runs the worker's `ParseAndSortLayers`, so a draft that passes here parses at deployment time. Invalid drafts are retried with the reason attached. |
| F2 | Service | A provider failure (timeout, HTTP 429/503, bad key) threw before the usage row was written. Failed generations left no audit trail, and tokens already spent on earlier attempts were lost. | Provider failures stop the loop and fall through to the same logging path. Every outcome is recorded. |
| F3 | Service | Failures were not logged. | Structured `LogWarning` with provider, model, tokens, iterations, duration and reason. |
| F4 | API | Request-validation 400s (e.g. empty prompt) used ASP.NET's `ProblemDetails`, with no `error` or `message` field. The UI checked `res.error`, so the failure was treated as success and an empty editor opened. | Project-wide `InvalidModelStateResponseFactory` returns the standard `{error, message, statusCode}` shape (`src/Aura.Api/Middleware/InvalidModelStateResponse.cs`). Nothing in the repo consumed `ProblemDetails`. |
| F5 | UI | A rejected `fetch` or a non-JSON body (e.g. a gateway error page) threw inside the submit handler. The button stayed on "Generating..." with no message. | Request wrapped; any failure shows a message and re-enables the button. Success is now detected by `essenceJson`, not `error`. |
| F6 | Config | OpenAI and Anthropic endpoints were hardcoded. | `OPENAI_BASE_URL` and `ANTHROPIC_BASE_URL` read from env, the same pattern as `OPENROUTER_BASE_URL`. `.env.example` documents them. |
| F7 | UI | The model name was run through `esc()` and then set with `textContent`, so escaped characters showed literally. | Removed the extra escape. |

Behaviour that was already correct and is covered by tests: a missing key returns a clear 422 with no provider call and no usage row; an unknown provider returns 400; timeouts and HTTP errors degrade to 422 with the upstream reason; no unhandled-exception response contains a stack trace.

## 3. Automated coverage

File: `tests/Aura.Tests/EssenceGenerationE2ETests.cs` (18 tests). The real controller, service, provider and factory run. Only the HTTP transport is scripted.

- **(a) Valid output.** `Generate_valid_prompt_returns_essence_that_passes_project_validation`: 200, token counts, `ParseAndSortLayers` accepts the output and orders it by dependency, and no `Essences` row is written. `Generate_accepts_essence_wrapped_in_markdown_fences`.
- **(b) No key / bad provider.** `Generate_without_provider_key_returns_clear_standard_error`: 422 `generation_failed`, the settings message, zero transport calls, no usage row, no essence. `Generate_with_unsupported_provider_returns_bad_request`: 400.
- **(c) Malformed or invalid output.** Theory over 7 inputs (non-JSON, `[]`, no `layers`, empty `layers`, non-object layer, bad `runPolicy`, dependency cycle). Each: 422 after 3 attempts, nothing persisted to `Essences`, usage row with `Success=false` and `Iterations=3`. `Generate_recovers_when_retry_returns_valid_essence`: a bad first attempt is corrected on retry, and the retry prompt carries the error.
- **(d) Timeout / HTTP error.** `Generate_provider_timeout_returns_graceful_error`; theory over HTTP 429 and 503; `Generate_failure_after_a_spent_attempt_still_records_that_usage` (tokens from the first attempt are kept when the second fails).
- **Error shape.** `Invalid_model_state_returns_standard_error_shape`; `Unhandled_exception_returns_generic_error_without_stack_trace`.

Existing tests updated for the `AnthropicLlmProvider` constructor change: `AnthropicLlmProviderTests`, `LlmProviderFactoryTests`. The existing `AiEssenceBuilderServiceTests` still passes.

Results on this branch (`dotnet build -c Release`, `dotnet test -c Release`):

- Build: succeeded, 0 errors.
- Tests: 290 passed, 11 failed. The 11 failures are all in `PostgresIntegrationTests`, which use Testcontainers and need a running Docker daemon. They fail identically on the unmodified base branch in this sandbox. They must be run in CI or on a machine with Docker.

## 4. Known gaps (found, not fixed here)

- **G1. Save path is not validated.** `POST /api/v1/essences` and `PUT` store `EssenceJson` without running the parser, so a user can edit a generated draft into an invalid essence and save it. This predates WI-6 and is a separate change.
- **G2. Operation types are not checked at generation.** The API project does not reference the worker's `OperationRegistry`, so an unknown `operationType` passes generation and fails at run time.
- **G3. Deployments `validate` is a stub.** `POST /api/v1/deployments/{id}/validate` always returns `IsValid = true`.
- **G4. Undecryptable key.** A key encrypted with a different secret makes `Decrypt` throw. It currently surfaces as the generic 500 response, not a "re-enter your key" message.
- **G5. Retry cost.** Invalid output costs up to three paid provider calls. This is logged, but there is no cap by spend.
- **G6. UI not run in a browser.** The modal changes were checked for syntax (`node --check` on the extracted script) and by reading the code. The live Playwright suite needs the full stack and Postgres, so it was not run here.

## 5. Manual checklist: live provider key (maintainer)

Run this on your own server with a real key. Do not paste keys into tickets, chat, or logs.

1. **Store the key.** Settings → add a provider key (OpenRouter, OpenAI or Anthropic). The provider row should show a key as configured.
2. **Happy path.** Essences → **AI Generate**. Choose an account and a provider. Prompt: *"Create a resource group and a small Ubuntu VM in eastus."* Expect the editor to open pre-filled, with layers in dependency order. Click **Create** and confirm the essence appears in the list.
3. **Runnable.** Create a deployment from that essence and start a run. Expect the layers to be created in order. A parse failure at run creation means the validation in F1 missed something; file it.
4. **No key.** Remove the key for the provider and generate again. Expect the message *"No API key configured for provider '…'. Add one in Account Settings."* The editor must not open, and the button must re-enable.
5. **Bad key.** Enter an invalid key value and generate. Expect *"… API error 401 …"* in the modal and a re-enabled button.
6. **Empty prompt.** Submit with a blank prompt. Expect *"The Prompt field is required."* (F4 fix). It must not open the editor.
7. **Network failure.** Point the provider base URL at an unreachable host, restart, and generate. Expect *"… request failed …"*, not a stuck button.
8. **Logs and usage.** Server logs show `AI essence generated` or `AI essence generation failed` with a reason, and no key. The `AiGenerationLogs` table has one row per attempt, with tokens, for both successes and failures.
9. **Optional.** Confirm the endpoint override works by setting `OPENROUTER_BASE_URL` (or `OPENAI_BASE_URL` / `ANTHROPIC_BASE_URL`) to a proxy you control, then reverting it.
