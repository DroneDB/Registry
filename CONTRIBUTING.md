# Contributing

:tada: First off, thanks for considering being a contributor! :tada:

## How can I contribute?

DDB contributors are expected to follow the [Collective Code of Construction Contract (C4)](https://rfc.zeromq.org/spec:42/C4/). You should read the document before making a pull request.

Take a look at the list of [open issues](https://github.com/DroneDB/registry/issues) to find out how you can help.

## Code of Conduct

This project adheres to the [Contributor Covenant](CONDUCT.md). By participating, you are expected to uphold this code. Please report unacceptable behavior to the project maintainers.

---

## Development Setup

### Prerequisites

- **.NET 10 SDK** (or later)
- **Node.js >= 18** (for the Vue.js frontend)
- **DroneDB C++ library** (`ddb.dll`/`libddb.so`) available in system PATH
- **Git** for version control

### Backend (.NET Core)

```bash
# Clone the repository
git clone https://github.com/DroneDB/Registry.git
cd Registry

# Build the solution
dotnet build Registry.sln

# Run the web application
dotnet run --project Registry.Web/Registry.Web.csproj

# Run all tests
dotnet test Registry.sln
```

The backend will open at `http://localhost:7000/` (or the configured port). Use the `Default-Alt-Port` profile in Visual Studio for local development.

### Frontend (Vue.js)

```bash
# Navigate to the frontend project
cd Registry.Web/ClientApp

# Install dependencies
npm install

# Build for local development (copies output to registry-data/ClientApp/)
npm run pub-dev

# Build for production
npm run build:prod
```

### Login Credentials (Development)

- **Username:** `admin`
- **Password:** `_Rainbow1`

---

## Architecture Overview

Registry follows a **hexagonal architecture** (ports and adapters pattern):

```
Controllers (Web API)
    |
    v
Services / Managers (Business Logic)
    |
    v
Ports (Interfaces)
    |
    v
Adapters (Implementations)
    |
    v
External Systems (DroneDB C++, Database, Cache, File System)
```

### Key Projects

| Project | Purpose |
|---------|---------|
| `Registry.Web/` | ASP.NET Core web API, controllers, services, and Vue.js frontend |
| `Registry.Web.Data/` | Entity Framework Core models, contexts, and migrations |
| `Registry.Web.Identity/` | ASP.NET Core Identity configuration and user management |
| `Registry.Adapters/` | Implementations of port interfaces (DroneDB, file system, thumbnails) |
| `Registry.Ports/` | Core port interfaces (IDDB, IDdbWrapper, DroneDB models) |
| `Registry.Common/` | Shared utilities, extensions, and test helpers |

### Database

Two `DbContext` instances are used:

- **`RegistryContext`** - Application data (organizations, datasets, batches, job indices)
- **`ApplicationDbContext`** - ASP.NET Core Identity data (users, roles, claims)

Four separate migration projects exist for different provider combinations:

- `Registry.Web.Data.SqliteMigrations`
- `Registry.Web.Data.MySqlMigrations`
- `Registry.Web.Identity.SqliteMigrations`
- `Registry.Web.Identity.MySqlMigrations`

The database provider is resolved at runtime via configuration.

---

## Coding Standards

### C# Backend

#### Naming Conventions

- **PascalCase** for class names, method names, and properties
- **camelCase** for local variables and parameters
- **I-prefix** for interfaces (e.g., `IObjectsManager`, `IDdbManager`)

#### General Guidelines

- Keep methods **short and focused**
- Follow **SOLID, DRY, and YAGNI** principles
- Use **async/await** for asynchronous operations
- Ensure **null checks** and **argument validation** in public methods
- Use **dependency injection** for services
- Prefer **interface-based design**
- Use **logging** via `ILogger<T>`

#### Data Flow

- Controllers return **DTOs only**, never entities directly
- Mapping is manual via extension methods `ToDto()` and `ToEntity()` in `Utilities/Extenders.cs`
- **No AutoMapper** is used

#### HTTP Responses

Return appropriate HTTP status codes:

- `Ok()` for successful GET operations
- `BadRequest()` for invalid input
- `NotFound()` for missing resources
- `UnauthorizedException` for authentication failures

### Vue.js Frontend

- Use **single-file components** (`.vue`)
- Follow the [Vue Style Guide](https://vuejs.org/style-guide/)
- Use **PascalCase** for component names
- Use **props** for passing data and **emit** for communication
- Use **v-model** for two-way binding
- Handle asynchronous operations using **async/await** or **Promises**
- Use **Composition API** for better scalability
- UI: **PrimeVue v4.x** (Lara theme with custom DDB preset)
- State: **Composables** in `src/composables/` (no Vuex/Pinia)

---

## Comment Standards

All public API surfaces must include XML documentation comments.

### XML Documentation (`///`)

| Element | Rule |
|---------|------|
| **Public classes/interfaces** | Always `/// <summary>` (multi-line) |
| **Public methods** | Always `/// <summary>` + `/// <returns>` |
| **Public method parameters** | `/// <param name="...">` when not self-evident |
| **Public properties** | Always `/// <summary>` (single-line) |
| **Protected methods** | `/// <summary>` recommended |
| **Private methods** | No XML docs; use `//` inline comments |
| **DTOs / Records** | `/// <summary>` on class + each property |
| **EF Entities** | `/// <summary>` on class + documented properties |

### Summary Style

**Class/Interface/Method level** (multi-line):
```csharp
/// <summary>
/// Controller for managing datasets within organizations.
/// </summary>
```

**Property/Field level** (single-line):
```csharp
/// <summary>Tool identifier (kebab-case).</summary>
```

### Single-Line Comments (`//`)

| Pattern | Usage |
|---------|-------|
| `// TODO:` | Action items to address later |
| `// NOTE:` | Developer notes explaining non-obvious decisions |
| `// Ref: URL` | Reference to external documentation |

**Placement:** Prefer above-line placement. Inline (same line) only for brief annotations.

### Block Comments (`/* */`)

- **Allowed:** Only in catch blocks for ignore semantics: `catch { /* ignore */ }`
- **Prohibited:** For commenting out code (use version control instead)

---

## Testing Guidelines

### Backend Tests

- **Framework:** NUnit 4.x with Shouldly assertions and Moq for mocking
- **Integration tests:** Use `NativeDdbWrapper` for direct C++ library calls (no Docker/subprocess)
- **Test helpers:**
  - `TestArea.cs` - Isolated temp directories for file-based tests
  - `TestFS.cs` - Extract test archives to temp locations

### Writing Tests

```csharp
[Test]
public void SomeOperation_ShouldWork()
{
    using var testArea = new TestArea("SomeOperation");

    // Arrange
    var setup = /* ... */;

    // Act
    var result = sut.SomeOperation(setup);

    // Assert
    result.ShouldBe(expected);
}
```

### Running Tests

```bash
# Run all tests
dotnet test Registry.sln

# Run tests for a specific project
dotnet test Registry.Web.Test/Registry.Web.Test.csproj

# Run with coverage
dotnet test Registry.sln /p:CollectCoverage=true
```

### Frontend Tests

- **No unit test framework** is configured (no Jest/Vitest)
- Use **Playwright** for E2E testing of the web platform

---

## Pull Request Guidelines

1. **Fork** the repository and create a feature branch from `master`
2. **Make your changes** following the coding standards above
3. **Add or update tests** for new functionality
4. **Run the full test suite** to ensure no regressions:
   ```bash
   dotnet test Registry.sln
   ```
5. **Build the frontend** if you made Vue.js changes:
   ```bash
   cd Registry.Web/ClientApp && npm run pub-dev
   ```
6. **Commit** with clear, descriptive messages
7. **Push** your branch and open a pull request against `master`
8. **Describe** your changes in the PR description, including:
   - What problem does this solve?
   - How did you test it?
   - Any breaking changes or migration requirements?

---

## Getting Help

- Check the [documentation](https://docs.dronedb.app)
- Open a [GitHub issue](https://github.com/DroneDB/Registry/issues) for bugs or feature requests
- Join the [DroneDB community](https://github.com/DroneDB) for discussions

