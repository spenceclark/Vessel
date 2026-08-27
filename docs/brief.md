# Vessel

> The lightweight observability proxy for LLM traffic

## Overview

- Very lightweight proxy that sits in front of LLM traffic.
- Captures request/response
- Understands OpenAI, Anthropic and Ollama endpoints and request/response formats
- Forwards the whole request + headers as-is
- Provides a UI that:
  - Allows requests to be viewed/filtered
  - View request level values like stop reason,
  - Request metrics like duration, token counts, tok/s, ttft
  - The prompt and reply
- Initially just for my own use - but if it works and is useful i'd have it open-source MIT

## Requirement

- Low overhead on wall-clock - if request/response its stored to a datastore the ideally do that in a background  thread to allow the request to run unhindered.
- Persists data - but has easy "clear down" options / max-size to stop it getting huge.
- Support:
  - Ollama - own api format + their OpenAI and Anthropic formats
  - llama.cpp - its format
  - Unsloth Desktop - its openai compatible
  - LM Studio - whichver it supports
  - ANy other OpenAI compatible end point
  - Actual OpenAI live endpoints (initiate and terminate SSL in proxy?)
  - Actual Anthropic live endpoints (initiate and terminate SSL in proxy?)

## Tech

Looking for recommended tech stack.

- Backend
  - I'm only really familiar with dotnet. Any other tech would mean relying on AI to understand it fully.
  - But dotnet comes with overhead of user needing framework installed - OR using prebuilt docker image
  - So, open to recommendations

- Frontend
  - I've no real front-end knowledge so leave this to recommendations. Web-based likely? Or Electron? Or..

- Storage
  - Just raw local file dumps  / JSONL??
  - Or an actual store like SQLLite / DuckDB
  - Postgres overkill

- UI:
  - Session Details
    - Request - Total/Failed
    - Avg Latency
    - Avg Tok/s
    - Avg ttft
    - "Reset" sesssion at any time
  - History
    - All requests in reverse order on left:

    ```text
    /chat/completions            4.5s
    claude-opus-4-8          88 tok/s
    [#AgentName]
    ```

    - Clicking one shows:
      - Stats
      - Headers
      - Prompts
      - Response
    - Filtering / searching

## Backends

- One "Default" chosen by user (e.g Ollama) - Name + Port (and type?)
- Multiple others can be setup in advance
- Individual requests can be routed to different backends via HTTP header (views show where they were routed)
- "Tags" can be added using HTTP header - e.g. to indicate "Agent Name" - which then flows through to UI (and filterable)

## Ollama Specific

- If on Ollama could also expose ollama-specific details "ollama ps" memory usage - open/view ollama server.log

## Anything else

Open to suggestions of other features developers might find useful.
