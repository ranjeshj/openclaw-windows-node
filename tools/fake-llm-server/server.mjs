#!/usr/bin/env node
// Minimal OpenAI-compatible HTTP mock for the openclaw gateway compatibility tests.
//
// Scope (W2-minimal): one endpoint, non-streaming, deterministic.
//   POST /v1/chat/completions  -> canned assistant reply that echoes the last user message.
//   GET  /__assert/last-request -> JSON of the most recent request payload (for harness).
//   POST /__assert/reset        -> clears recorded state.
//   GET  /                      -> health probe ("ok").
//
// Configuration via env vars:
//   FAKE_LLM_PORT    (default 18888)
//   FAKE_LLM_BIND    (default 127.0.0.1)
//   FAKE_LLM_MODEL   (default "fake-llm")  reported as the response model
//
// Run locally:
//   node tools/fake-llm-server/server.mjs
//
// Then point an openai-compatible provider at http://127.0.0.1:18888/v1 with any apiKey.

import http from "node:http";

const PORT = parseInt(process.env.FAKE_LLM_PORT || "18888", 10);
const BIND = process.env.FAKE_LLM_BIND || "127.0.0.1";
const MODEL = process.env.FAKE_LLM_MODEL || "fake-llm";

/** @type {{ at: string, method: string, url: string, body: any } | null} */
let lastRequest = null;
let requestCount = 0;

function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (chunk) => chunks.push(chunk));
    req.on("end", () => {
      const raw = Buffer.concat(chunks).toString("utf8");
      if (!raw) return resolve(null);
      try {
        resolve(JSON.parse(raw));
      } catch (err) {
        reject(err);
      }
    });
    req.on("error", reject);
  });
}

function send(res, status, body, headers = {}) {
  const payload = typeof body === "string" ? body : JSON.stringify(body);
  res.writeHead(status, { "content-type": "application/json", ...headers });
  res.end(payload);
}

const server = http.createServer(async (req, res) => {
  try {
    const url = req.url || "/";

    if (req.method === "GET" && url === "/") {
      res.writeHead(200, { "content-type": "text/plain" });
      res.end("ok");
      return;
    }

    if (req.method === "GET" && url === "/__assert/last-request") {
      send(res, 200, { lastRequest, requestCount });
      return;
    }

    if (req.method === "POST" && url === "/__assert/reset") {
      lastRequest = null;
      requestCount = 0;
      send(res, 200, { reset: true });
      return;
    }

    if (req.method === "POST" && url.startsWith("/v1/chat/completions")) {
      const body = await readJsonBody(req);
      requestCount += 1;
      lastRequest = {
        at: new Date().toISOString(),
        method: req.method,
        url,
        body,
      };

      const messages = Array.isArray(body?.messages) ? body.messages : [];
      const lastUser = [...messages].reverse().find((m) => m && m.role === "user");
      const lastUserText =
        typeof lastUser?.content === "string"
          ? lastUser.content
          : Array.isArray(lastUser?.content)
            ? lastUser.content
                .map((p) => (typeof p === "string" ? p : p?.text || ""))
                .filter(Boolean)
                .join(" ")
            : "";

      const reply = `[fake-llm] echo: ${lastUserText || "(no user message)"}`;
      const completion = {
        id: `chatcmpl-fake-${requestCount}`,
        object: "chat.completion",
        created: Math.floor(Date.now() / 1000),
        model: body?.model || MODEL,
        choices: [
          {
            index: 0,
            finish_reason: "stop",
            message: { role: "assistant", content: reply },
          },
        ],
        usage: {
          prompt_tokens: Math.max(1, lastUserText.length),
          completion_tokens: reply.length,
          total_tokens: Math.max(1, lastUserText.length) + reply.length,
        },
      };
      send(res, 200, completion);
      return;
    }

    send(res, 404, { error: { message: `not found: ${req.method} ${url}` } });
  } catch (err) {
    send(res, 500, { error: { message: String(err?.message || err) } });
  }
});

server.listen(PORT, BIND, () => {
  // eslint-disable-next-line no-console
  console.log(`[fake-llm] listening on http://${BIND}:${PORT} (model=${MODEL})`);
});

for (const sig of ["SIGINT", "SIGTERM"]) {
  process.on(sig, () => {
    server.close(() => process.exit(0));
  });
}
