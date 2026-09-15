# copilot-instructions.md

> **Doc rule**: Do not hardcode release or dependency version numbers in this file — they drift with every release. Read them from `Registry.Web/Registry.Web.csproj` (`<Version>`, `<DdbVersion>`, package references) and `Registry.Web/ClientApp/package.json` when you need current values. Framework identities (net10 TFM, Vue 3, PrimeVue 4, NUnit 4) are stable enough to write down.

## .NET Core Backend (`Registry.*`)

### General Guidelines
- Use **PascalCase** for class names, method names, and properties.
- Use **camelCase** for local variables and parameters.
- Keep methods **short and focused**.
- Follow **SOLID, DRY and YAGNI principles**.
- Use **async/await** for asynchronous operations.
- Ensure **null checks** and **argument validation** in public methods.
- Structure the project using hexagonal architecture (e.g., Controllers, Services, Ports, Adapters, Models, etc...).
- Use **dependency injection** for services. Mind the fact that Registry.Web can be started in different modes and you need to check if the processing nodes need any specific service registered or not.
- Prefer **interface-based design**.
- Use **logging** via `ILogger<T>`.

### Testing
- Backend tests use **NUnit 4.x** + **Shouldly** + Moq. Integration tests use `NativeDdbWrapper` for direct C++ library calls (no Docker/subprocess). Test helpers: `TestArea.cs` in `Registry.Common/Test/` for isolated temp directories, `TestFS.cs` for extracting test archives.
- Native interop: `Registry.Adapters/DroneDB/NativeDdbWrapper.cs` binds `libddb` via ~35 `[DllImport("ddb", EntryPoint="DDB…")]` P/Invokes (no CLI subprocess, no binding generator — the ABI is a flat C API defined in DroneDB's `src/include/ddb.h`). The required native version is pinned as `<DdbVersion>` in `Registry.Web/Registry.Web.csproj`; bump it when the native ABI changes.
- Config: Use Options pattern (`AppSettings.cs`). Key settings: `AppSettings:Secret` (JWT signing key), `AppSettings:StoragePath` (or CLI arg `StorageFolder`). The DroneDB library path is resolved automatically from system PATH - no dedicated environment variable exists.

### Best Practices
- Use Entity Framework Core with migrations. Two DbContexts: `RegistryContext` (app data) + `ApplicationDbContext : IdentityDbContext<User>` (auth/Identity). 4 separate migration projects at solution root: `Registry.Web.Data.SqliteMigrations`, `Registry.Web.Data.MySqlMigrations`, `Registry.Web.Identity.SqliteMigrations`, `Registry.Web.Identity.MySqlMigrations`. Provider resolved at runtime via config.
- Return appropriate HTTP responses (e.g., `Ok()`, `BadRequest()`, `NotFound()`).
- Controllers return DTOs only, never entities directly. Mapping is manual via extension methods `ToDto()` and `ToEntity()` in `Utilities/Extenders.cs` - **no AutoMapper**.
- Architecture: Interface-first with `I*Manager` interfaces split between `Registry.Web/Services/Ports/` (web layer) and `Registry.Ports/` (core adapters like `IDdbManager`, `ICacheManager`). Controllers depend only on these interfaces. Implementation lives in `Services/*.cs` and `Services/Adapters/`. This is a simplified port-adapter pattern.

### Comment Standards

#### XML Documentation (`///`)

| Element | Rule |
|---------|------|
| **Public classes/interfaces** | Always `/// <summary>` (multi-line for classes, single-line for properties) |
| **Public methods** | Always `/// <summary>` + `/// <returns>` |
| **Public method parameters** | Always `/// <param name="...">` when the parameter's purpose is not self-evident from the name |
| **Public properties** | Always `/// <summary>` (single-line preferred) |
| **Protected methods** | `/// <summary>` recommended |
| **Private methods** | No XML docs required; use `//` inline comments |
| **DTOs / Records** | `/// <summary>` on class + each property (single-line) |
| **EF Entities** | `/// <summary>` on class + documented properties |
| **`<remarks>`** | Use when a method/class needs extended explanation beyond the summary |
| **`<exception>`** | Use when a method throws specific exceptions with semantic meaning (not generic `Exception`) |
| **`<see cref>`** | Use in class summaries to cross-reference related types |
| **`<c>`** | Use for inline code/value references within XML docs |

#### Summary Style

- **Class/Interface/Method level:** Multi-line format
  ```csharp
  /// <summary>
  /// Controller for managing datasets within organizations.
  /// </summary>
  ```
- **Property/Field level:** Single-line format
  ```csharp
  /// <summary>Tool identifier (kebab-case).</summary>
  ```

#### Single-Line Comments (`//`)

| Pattern | Usage |
|---------|-------|
| `// TODO:` | Action items to address later (keep minimal) |
| `// NOTE:` | Developer notes explaining non-obvious decisions |
| `// Ref: URL` | Reference to external documentation/sources |
| **Above-line placement** | Prefer placing `//` comments on the line(s) BEFORE the code they explain |
| **Inline (same line)** | Only for brief annotations (e.g., `= null!; // PK`) |

#### Block Comments (`/* */`)

- **Allowed:** Only in catch blocks for ignore semantics: `catch { /* ignore */ }`
- **Prohibited:** For commenting out code (use version control instead)

#### `#region` / `#endregion`

- Use for logical grouping in large files (500+ lines)
- Match region names between interfaces and implementations

#### Exclusions

- Auto-generated files (`*Designer.cs`, `*ModelSnapshot.cs`, migration files)
- 3rd-party code (`EchoStream.cs`, `SubsetStream.cs`)

## Vue.js Frontend (`Registry.Web/ClientApp` — git submodule of the separate **DroneDB/Hub** repo)

### Build Instructions
- **Local dev**: `npm run pub-dev` — builds and copies the output to `registry-data/ClientApp/`, where the backend serves the static files.
- **Production**: `npm run build:prod` (webpack output goes to `ClientApp/build/`).

### Coding Standards
- Use **single-file components** (`.vue`).
- Follow the **Vue Style Guide (https://vuejs.org/style-guide/)**.
- Use **PascalCase** for component names.
- Use **props** for passing data and **emit** for communication.
- Use **v-model** for two-way binding.
- Handle asynchronous operations using **async/await** or **Promises**.
- Use **composition API** (if applicable) for better scalability.
- Validate all forms before submission.
- UI: **PrimeVue v4.x** is the sole component library (Lara theme with custom DDB preset). Bootstrap 5 is used only for grid + utilities. No other UI libraries present.
- State: Composables in `webapp/js/composables/` (under `ClientApp/`, there is **no `src/` folder**) for reusable logic and state. No Vuex/Pinia — the app uses Vue composables directly. Layout: `webapp/js/{components,features,composables,libs,vendor,layout,dynamic}`; views live in `webapp/js/features/`.
- API client: Plain `fetch()` wrappers that attach an `Authorization: Bearer <token>` header — implemented in `webapp/js/libs/ddb/` (`registry.js`, `dataset.js`, `entry.js`) and `webapp/js/libs/api/`. **There is no axios dependency.**
- Testing: **Vitest 3** is configured (`vitest.config.js`, `happy-dom` environment) — but the `npm test` script is an intentional stub, so run `npx vitest`. No `@vitest/coverage` package is installed (`--coverage` will fail). Playwright is only a devDependency (no config file in this repo). 3D/Tiles rendering: **Giro3D + three.js** in `features/viewers/unified/`, plus vendored Potree and spark for splats.

### Comment Standards

#### Single-line comments (`//`)

| Pattern | Usage |
|---------|-------|
| `// Description` (above line) | Primary style for explanatory comments |
| `// Description` (inline) | Brief annotations on the same line (data declarations, one-liners) |
| `// ---- Section Name ----` | Section separator inside large methods/option-objects |
| `// TODO:` | Deferred action items |
| `// NOTE:` | Developer notes explaining non-obvious decisions (uppercase NOTE) |

Rules:
- Always one space after `//` — never `//comment`
- Capital first letter
- No trailing period on short comments; longer multi-line prose may end with a period
- Language: **English only**

#### Multi-line and block comments (`/* */`)

| Pattern | Usage |
|---------|-------|
| `catch (e) { /* noop */ }` | Intentionally empty catch — use `/* noop */` as the message |
| `catch (e) { /* ignore */ }` | Alternative for intentionally swallowed errors |
| `catch (e) { /* short note */ }` | One-liner description allowed when meaning differs from noop |
| `/* Description */` in CSS | Section labels inside `<style>` blocks |
| `/* webpackChunkName: "..." */` | Webpack magic comments in dynamic imports |
| `/* global ... */` | ESLint global declarations |

Rules:
- Catch variable: always **`e`** — never `_`, `__`, or bare `catch { }`
- No multi-line prose comments (`/* ... */`) in JS/Vue script code; use `//` for that

#### JSDoc (`/** */`)

| Context | Rule |
|---------|------|
| All composables (`composables/`) | Top-level `/** ComponentName - description. */` block required |
| All components (`components/`) | Top-level `/** ComponentName - description. */` block required |
| Complex views (`features/`) >500 lines | Top-level `/** ViewName - description. */` block required |
| Methods in complex files | `/** Short description. */` on individual methods |
| Simple views <500 lines | Optional |

Format (follow `ErrorBoundary.vue` and `VirtualScroller.vue` as canonical examples):
```js
/**
 * ComponentName - One-line summary.
 *
 * Optional additional context paragraph.
 *
 * Props:
 *   propName - Description.
 */
```

#### Vue template comments (`<!-- -->`)

- `<!-- Section Label -->` for template regions — already consistent, no change needed
- Capital first letter, no trailing period

#### ESLint

- Config: `eslint.config.js` (flat config, ESLint v10)
- `spaced-comment` rule enforces the space-after-`//` convention as a **warning**

## AI Agent Guidelines

### Code Analysis
- **Read existing code patterns** before implementing new features
- **Understand the complete data flow** from input files to database storage
- **Check for existing utilities** before writing new helper functions
- **Respect the layered architecture** - don't bypass abstraction layers

### Making Changes
- **Always ask for confirmation** before modifying code
- **Provide detailed explanations** of changes and their impact
- **Consider backwards compatibility** for API changes
- **Test changes thoroughly** including edge cases

### Problem Solving
- **Break down complex tasks** into smaller, manageable components
- **Leverage existing infrastructure** rather than reinventing solutions
- **Consider performance implications** of proposed changes
- **Think about error scenarios** and how to handle them gracefully

### Documentation
- **Comment complex algorithms** and spatial operations
- **Document API changes** with clear examples
- **Explain architectural decisions** for future maintainers
- **Keep README and docs updated** with significant changes
- **Do not create new documentation files without approval from the user or explicit instructions to do so**

## Quality Assurance
- **Run full test suite** before proposing changes
- **Verify cross-platform compatibility**
- **Check memory leaks** with tools like Valgrind
- **Validate spatial operations** with known test datasets
- **Performance regression testing** for core operations





