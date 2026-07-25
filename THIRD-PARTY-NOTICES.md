# Third-Party Notices

Knotarium (licensed under Apache-2.0, see [LICENSE](LICENSE)) bundles and builds on the
open-source components below. Each remains under its own license, held by its respective
copyright owners.

This file lists the **direct runtime dependencies** of the shipped application. Each package
carries additional **transitive** dependencies under permissive licenses (MIT / Apache-2.0 /
BSD / ISC); the complete resolved set and full license texts are available through the package
managers — `Frontend/package-lock.json` (npm) and the NuGet restore graph (`dotnet list package
--include-transitive`) — and from each project's linked source.

## Frontend (npm)

| Package | License | Project |
|---|---|---|
| react, react-dom | MIT | https://github.com/facebook/react |
| @xyflow/react (React Flow) | MIT | https://github.com/xyflow/xyflow |
| @monaco-editor/react (+ Monaco Editor) | MIT | https://github.com/suren-atoyan/monaco-react · https://github.com/microsoft/monaco-editor |
| @dagrejs/dagre | MIT | https://github.com/dagrejs/dagre |
| zustand | MIT | https://github.com/pmndrs/zustand |
| lucide-react | ISC | https://github.com/lucide-icons/lucide |
| yaml | ISC | https://github.com/eemeli/yaml |

Build/test tooling (not shipped in the runtime bundle): Vite, Vitest, ESLint (MIT), TypeScript,
Playwright (Apache-2.0), and their dependencies.

## Fonts (bundled with the offline help)

The offline help under `help/` embeds these font files so it renders without network access.
Both are the latin subset only, redistributed unmodified.

| Font | License | Project |
|---|---|---|
| Plus Jakarta Sans | SIL Open Font License 1.1 | https://github.com/tokotype/PlusJakartaSans |
| JetBrains Mono | SIL Open Font License 1.1 | https://github.com/JetBrains/JetBrainsMono |

## Backend (.NET / NuGet)

| Package | License | Project |
|---|---|---|
| Microsoft.EntityFrameworkCore.Sqlite, Microsoft.Data.Sqlite | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.* (Configuration, DI, Hosting, Http, Logging, Caching) | MIT | https://github.com/dotnet/runtime |
| Microsoft.CodeAnalysis.CSharp (Roslyn) | MIT | https://github.com/dotnet/roslyn |
| Microsoft.OpenApi, Microsoft.OpenApi.YamlReader | MIT | https://github.com/microsoft/OpenAPI.NET |
| MailKit, MimeKit *(transitive)* | MIT | https://github.com/jstedfast/MailKit · https://github.com/jstedfast/MimeKit |
| MQTTnet | MIT | https://github.com/dotnet/MQTTnet |
| Cronos | MIT | https://github.com/HangfireIO/Cronos |
| YamlDotNet | MIT | https://github.com/aaubry/YamlDotNet |
| BouncyCastle.Cryptography | MIT | https://github.com/bcgit/bc-csharp |
| Npgsql | PostgreSQL License | https://github.com/npgsql/npgsql |
| Serilog.AspNetCore, Serilog.Formatting.Compact | Apache-2.0 | https://github.com/serilog/serilog-aspnetcore |
| OpenTelemetry.* (Extensions.Hosting, Exporter.Console, Instrumentation.AspNetCore/Http) | Apache-2.0 | https://github.com/open-telemetry/opentelemetry-dotnet |
| SQLitePCLRaw *(transitive, native SQLite)* | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |

Test-only packages (xUnit, FsCheck, coverlet, NetArchTest, Microsoft.NET.Test.Sdk,
Microsoft.AspNetCore.Mvc.Testing) are not part of the distributed application.

---

If you believe an attribution is missing or incorrect, please open an issue.
