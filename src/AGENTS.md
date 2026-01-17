# TeamZaps Application Specific Guidelines

Always respect the [general guidelines](../AGENTS.md).
This document extends those guidelines with application-specific conventions.


## Code Style Preferences

### General Style Preferences

#### Error Messages
- Exception messages should be clean and descriptive without emojis
- Use markdown formatting in user-facing messages: **bold**, *italic*
- Provide actionable guidance in error messages
- Use `.AddLogLevel()` to specify appropriate log level for user display
- Use `.AnswerUser()` if exception is intent as a user response
- Emojis are handled automatically by the exception handling pipeline

Example:
```csharp
throw new InvalidOperationException("Session is not currently accepting new participants.")
    .AddLogLevel(LogLevel.Warning)
    .AnswerUser();
```