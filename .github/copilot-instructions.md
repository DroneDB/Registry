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

### Best Practices
- Use Entity Framework Core with migrations.
- Return appropriate HTTP responses (e.g., `Ok()`, `BadRequest()`, `NotFound()`).
- Use DTOs for data transfer rather than exposing entities directly.

## Vue.js Frontend (`Registry.Web/ClientApp`)

### Build Instructions
The Vue.js SPA should be built using:

```bash
cd Registry.Web/ClientApp
npx webpack
```

### Coding Standards
- Use **single-file components** (`.vue`).
- Follow the **Vue Style Guide (https://vuejs.org/style-guide/)**.
- Use **PascalCase** for component names.
- Use **props** for passing data and **emit** for communication.
- Use **v-model** for two-way binding.
- Handle asynchronous operations using **async/await** or **Promises**.
- Use **composition API** (if applicable) for better scalability.
- Validate all forms before submission.

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





