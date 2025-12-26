---
trigger: always_on
description: Quality Assurance and Security
---

# Quality Assurance and Security

## Unit Testing
*   **Mandate**: Critical logic must be tested.
*   **Backend**: Use `xUnit` for C# unit tests. Mock dependencies using `Moq` or `NSubstitute`.
*   **Frontend**: Use `Vitest` and `React Testing Library` for component and logic testing.
*   **Coverage**: Aim for high coverage on business logic and complex utilities.

## Documentation
*   **Code Comments**: Use XML documentation comments (`///`) for public APIs and complex methods in C#.
*   **READMEs**: Include concise README.md files in major directories explaining their purpose.
*   **Verbosity**: Keep documentation clear and to the point. Avoid stating the obvious.

## Security
*   **Input Sanitization**: Validate and sanitize all user inputs. Use parameterized queries (EF Core does this by default) to prevent SQL Injection.
*   **XSS Prevention**: React escapes content by default. Be cautious with `dangerouslySetInnerHTML`.
*   **Authentication**:
    *   Use JWT (JSON Web Tokens) for stateless authentication.
    *   Store tokens in HTTP-only, Secure, SameSite cookies.
    *   Use Argon2id for password hashing.
*   **Authorization**: Implement Role-Based Access Control (RBAC). Check `User.Role` and `User.MaxRating` for parental controls.
*   **File Access**: Strictly validate file paths to prevent Local File Inclusion (LFI). Jail file watchers to authorized directories.
