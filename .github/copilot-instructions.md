# copilot-instructions.md

## .NET Core Backend (`Registry.*`)

### General Guidelines
- Use **PascalCase** for class names, method names, and properties.
- Use **camelCase** for local variables and parameters.
- Keep methods **short and focused**.
- Follow **SOLID principles**.
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





