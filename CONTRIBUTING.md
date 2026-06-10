# Contributing to UKBatch

Thank you for considering a contribution to UKBatch! Issues, discussions, and pull requests are always welcome.

## Before Opening a Pull Request

For anything beyond a small bug fix or documentation change, please open an issue first to discuss the proposed approach.

### Build and Test Requirements

All builds and tests must pass without warnings (warnings are treated as errors):

```bash
dotnet build
dotnet test --filter "Category!=RequiresDocker"
```

Tests marked with `RequiresDocker` (for example, PostgreSQL Testcontainers and RabbitMQ integration tests) require a local container runtime. The CI pipeline executes only the Docker-free test suite.

### Coding Conventions

Please follow the existing conventions used throughout the codebase:

- Interfaces use the `I` prefix.
- Asynchronous methods end with the `Async` suffix.
- `CancellationToken` is the last parameter of every public asynchronous method.
- Concrete implementation types should be declared as `sealed`.
- Package versions are managed centrally through `Directory.Packages.props`. Do not specify `Version` attributes in project files.

## Legal

Contributions are accepted under the project's [MIT License](LICENSE), the same license under which UKBatch itself is distributed ("inbound = outbound").

By submitting a pull request, you confirm that you have the right to license your contribution under the MIT License.

UKBatch is developed using an open-core model. The maintainer may offer commercial products or editions built on top of this MIT-licensed core, which is fully permitted by the MIT License.

Any contribution accepted into this repository will always remain available under the MIT License in this repository.

## Code of Conduct

Be respectful, constructive, and professional.

Disagreements should focus on technical decisions and implementation details—not on people.
