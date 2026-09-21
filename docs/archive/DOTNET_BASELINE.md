# Archived .NET / WinUI baseline

The retired C# / WinUI implementation is preserved only by Git history and tag
`relay-dotnet-a6bf987` (commit `a6bf987e5e4697412417dd05d7ab54e7eeddcd9d`).

Do not restore `src/`, `tests/`, or `Relay.slnx` onto active `main` or feature
branches. The production product is TypeScript + React Native/Expo + Tauri + SQLite.

To inspect the archived tree:

```powershell
git show relay-dotnet-a6bf987:README.md
git checkout relay-dotnet-a6bf987 --detach
```

Return to the active branch afterward. New work must not target the retired stack.
