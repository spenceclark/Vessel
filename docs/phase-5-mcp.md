- **`promptPreview` is an N+1 full-body decode (review finding).** `search_requests`
  calls `GetMcpPromptText` per returned row — a point query + full decompress/decode
  of the entire stored body (uncapped) to keep 200 chars, up to 100× per search,
  violating `SqliteReadStore`'s no-body-scans principle. Fix structurally, not with
  bounded reads (the flatteners need parsed JSON; capped decodes parse to nothing):
  **schema v2 migration** adds nullable `prompt_preview` (and `response_preview`)
  TEXT columns, populated at write time by the enricher from `EnrichedRecord`'s
  already-computed flattened text (whitespace-collapsed, ~200 chars). Reads become
  a column select; `GetMcpPromptText`'s per-row decode path is deleted. Pre-migration
  rows stay NULL → preview omitted (the tool contract already treats it as optional);
  no backfill — that would be the very scan being eliminated, and retention ages old
  rows out. Note: first-ever second migration — the `user_version` loop gets its
  first real exercise; test v1→v2 upgrade on a populated database explicitly.

  **Implemented.** `_migrations` in `SqliteCaptureStore` gained a v2 entry
  (`ALTER TABLE requests ADD COLUMN prompt_preview/response_preview TEXT`); `InsertBatch`
  writes both from `EnrichedRecord.PromptText`/`ResponseText` via a new `MakePreview`
  helper (whitespace-collapsed, first 200 chars, no truncation suffix). `SqliteReadStore`
  gained an `includePreview` opt-in on `ListRequests` (default `false`, so the REST
  `/requests` endpoint is byte-for-byte unchanged) that appends `prompt_preview` to the
  select and populates a new optional `Summary.PromptPreview` (`JsonIgnore` when null).
  `McpTools.SearchRequests` now passes `includePreview: true` and reads
  `summary.PromptPreview` directly — `GetMcpPromptText` and the now-dead `Preview`
  truncation helper were deleted. `GetMcpRequest`'s full flatten-at-read (`get_request`)
  is untouched.

  Tests added: `StorageTests.Migrations_V1ToV2_PreservesDataAndAddsNullablePreviewColumns`
  (hand-built v1 schema + rows + FTS, reopened through the current store — asserts
  `user_version` 2, old row/FTS intact, new columns NULL on the pre-migration row);
  `StorageTests.InsertBatch_PopulatesPreviewColumns_ForEachFormat` (theory over
  openai-chat, anthropic-messages, ollama-chat, ollama-generate — real `FormatEnricher`
  + adapters, asserts the stored preview is the collapsed/capped prompt and response
  text); `StorageTests.InsertBatch_RawFormat_LeavesPreviewColumnsNull`;
  `McpTests.M2b_SearchRequests_ReadsPreviewColumn_NeverDecodesBody` (105 rows seeded with
  a poisoned, non-zstd `request_body`/`response_body` and a real `prompt_preview` column
  value — a `limit=100` search succeeding and returning the exact stored previews is only
  possible if the row read never touches the body columns at all, since decompressing the
  poisoned bytes would throw; also covers a NULL-preview row omitting the field). Also
  updated `Migrations_FreshDbThenReopen`'s `user_version` expectation from 1 to 2.

  Full suite green ×2 (318 tests, 0 failures).
