# LedgerMem .NET SDK

Official C# / .NET 8 client for the [LedgerMem](https://proofly.dev) memory API.

## Install

```bash
dotnet add package LedgerMem
```

## Quickstart

```csharp
using LedgerMem;

var client = new LedgerMemClient(
    apiKey: Environment.GetEnvironmentVariable("LEDGERMEM_API_KEY"),
    workspaceId: Environment.GetEnvironmentVariable("LEDGERMEM_WORKSPACE_ID")
);

var memory = await client.CreateAsync("Shah prefers dark mode in terminals.");
var results = await client.SearchAsync("ui preferences", limit: 5);

foreach (var hit in results.Hits)
    Console.WriteLine($"{hit.Score:F2} {hit.Content}");
```

## Configuration

| Env var | Purpose |
| --- | --- |
| `LEDGERMEM_API_KEY` | Bearer token (required) |
| `LEDGERMEM_WORKSPACE_ID` | Workspace identifier (required) |
| `LEDGERMEM_API_URL` | Override base URL (default `https://api.proofly.dev`) |

## API

| Method | HTTP | Description |
| --- | --- | --- |
| `SearchAsync(query, limit?, actorId?)` | `POST /v1/search` | Semantic + keyword search |
| `CreateAsync(content, metadata?, actorId?)` | `POST /v1/memories` | Store a new memory |
| `UpdateAsync(id, content?, metadata?)` | `PATCH /v1/memories/:id` | Patch an existing memory |
| `DeleteAsync(id)` | `DELETE /v1/memories/:id` | Remove a memory |
| `ListAsync(limit?, cursor?, actorId?)` | `GET /v1/memories` | Paginated listing |

## License

MIT
