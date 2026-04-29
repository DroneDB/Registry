# copilot-instructions.md

## .NET Core Backend (`Registry.*`)

### General Guidelines
- Use **PascalCase** for class names, method names, and properties.
- Use **camelCase** for local variables and parameters.
- Keep methods **short and focused**.
- Follow **SOLID, DRY and YAGNI principles**.
- Use **async/await** for asynchronous operations.
- Ensure **null checks** and **argument validation** in public methods.
- Structure the project using hexagonal architecture (e.g., Controllers, Services, Ports, Adapters, Models, etc...).
- Use **dependency injection** for services.
- Prefer **interface-based design**.
- Use **logging** via `ILogger<T>`.

### Testing
- Backend tests use **NUnit 4.x** + **Shouldly** + Moq. Integration tests use `NativeDdbWrapper` for direct C++ library calls (no Docker/subprocess). Test helpers: `TestArea.cs` in `Registry.Common/Test/` for isolated temp directories, `TestFS.cs` for extracting test archives.
- Config: Use Options pattern (`AppSettings.cs`). Key settings: `AppSettings:Secret` (JWT signing key), `AppSettings:StoragePath` (or CLI arg `StorageFolder`). The DroneDB library path is resolved automatically from system PATH — no dedicated environment variable exists.

### Best Practices
- Use Entity Framework Core with migrations. Two DbContexts: `RegistryContext` (app data) + `ApplicationDbContext : IdentityDbContext<User>` (auth/Identity). 4 separate migration projects at solution root: `Registry.Web.Data.SqliteMigrations`, `Registry.Web.Data.MySqlMigrations`, `Registry.Web.Identity.SqliteMigrations`, `Registry.Web.Identity.MySqlMigrations`. Provider resolved at runtime via config.
- Return appropriate HTTP responses (e.g., `Ok()`, `BadRequest()`, `NotFound()`).
- Controllers return DTOs only, never entities directly. Mapping is manual via extension methods `ToDto()` and `ToEntity()` in `Utilities/Extenders.cs` — **no AutoMapper**.
- Architecture: Interface-first with `I*Manager` interfaces in `Services/Ports/`. Controllers depend only on these interfaces. Implementation lives in `Services/*.cs` and `Services/Adapters/`. This is a simplified port-adapter pattern.

## Vue.js Frontend (`Registry.Web/ClientApp`)

### Build Instructions
- **Sviluppo locale**: `npm run pub-dev` (builda e copia automaticamente l'output in `registry-data/ClientApp/`, dove il backend serve i file statici).
- **Produzione**: `npm run build:prod` (webpack output in `build/`).

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
- State: Vuex for state management, composables in `src/composables/` for reusable logic.
- API client: Functions in `src/api/` use axios instances with JWT interceptors.
- Testing: **No unit test framework configured** (no Jest/Vitest). Only Playwright for E2E testing.

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





